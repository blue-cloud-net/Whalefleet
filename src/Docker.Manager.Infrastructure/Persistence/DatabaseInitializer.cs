using System.Globalization;
using Docker.Manager.Application.Abstractions.Persistence;
using Docker.Manager.Core.Hosts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Docker.Manager.Infrastructure.Persistence;

    /// <summary>
    /// 数据库初始化器实现：迁移 → 启用 WAL → 种入默认主机。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么通过 <see cref="IServiceScopeFactory"/> 取仓储</b>：本类是单例，
    /// 而仓储是 Scoped（依赖 <c>ICurrentUser</c> 这类请求级上下文）。单例直接注入 Scoped
    /// 会构成「捕获依赖」（captive dependency）——DI 容器在开发环境会直接报错，
    /// 生产环境则会静默地把请求级对象变成进程级，产生难以复现的状态泄漏。</para>
    /// <para><b>默认主机</b>：与 <c>docs/architecture.md</c> 的 0.0.8 约定一致——
    /// 固定主键、名称 <c>local</c>、socket 连接 <c>/var/run/docker.sock</c>；
    /// 审计人取默认值哨兵（启动引导无登录用户）。</para>
    /// <para><b>职责边界</b>：本类只负责「让数据库可用」（架构 + 基线数据）。
    /// 初始管理员引导在 <see cref="AdminBootstrapper"/>——它需要密码哈希器，
    /// 而「数据库初始化」不该因此依赖安全组件：否则任何只想建库的调用方
    /// 都必须先准备一个密码哈希器（测试环境已真实撞到过这个问题）。</para>
    /// </remarks>
    internal sealed class DatabaseInitializer : IDatabaseInitializer
    {
        /// <summary>
        /// 默认主机的固定主键（文档中给出，便于运维脚本引用）。
        /// </summary>
        internal static readonly Guid DefaultHostId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// 默认主机显示名。
    /// </summary>
    internal const string DefaultHostName = "local";

    /// <summary>
    /// 默认主机端点。
    /// </summary>
    internal const string DefaultHostEndpoint = "/var/run/docker.sock";

    private readonly IDbConnectionFactory _connections;
    private readonly SqliteSchemaMigrator _migrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    /// <summary>
    /// 初始化初始化器。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="migrator">架构迁移器。</param>
    /// <param name="scopeFactory">作用域工厂（用于按需创建 Scoped 仓储）。</param>
    /// <param name="logger">日志。</param>
    public DatabaseInitializer(
        IDbConnectionFactory connections,
        SqliteSchemaMigrator migrator,
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _migrator = migrator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var schemaVersion = await _migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await this.EnableWriteAheadLoggingAsync(cancellationToken).ConfigureAwait(false);
        await this.SeedDefaultHostAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("数据库初始化完成（schema 版本 {SchemaVersion}）", schemaVersion);
    }

    private async Task EnableWriteAheadLoggingAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL";

        var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // WAL 是数据库文件级属性（而非每连接），设置一次即持久生效；
        // 重复启动时这里只是把已有值读回来。
        _logger.LogDebug("SQLite journal_mode = {JournalMode}", mode);
    }

    private async Task SeedDefaultHostAsync(CancellationToken cancellationToken)
    {
        // 这里刻意用「物理行数」而不是 IHostRepository.CountAsync：
        // 仓储的计数会过滤软删除行，于是「默认主机被软删除」时业务计数为 0 →
        // 会被当成空表再次种入 → 与残留行的固定主键冲突，直接把启动搞崩。
        // 启动引导关心的是「这张表是否从未被初始化过」，属于基础设施关注点，不是业务查询。
        if (await this.CountHostRowsAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var hosts = scope.ServiceProvider.GetRequiredService<IHostRepository>();

        var host = DockerHost.Register(
            DefaultHostId,
            DefaultHostName,
            DockerHostType.Socket,
            new HostEndpoint(DefaultHostEndpoint));

        await hosts.AddAsync(host, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "hosts 表为空，已种入默认主机：id={HostId}, name={HostName}, endpoint={Endpoint}",
            host.Id.ToString("D", CultureInfo.InvariantCulture),
            host.Name,
            host.Endpoint.Value);
    }

    private async Task<long> CountHostRowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM hosts";

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
