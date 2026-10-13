using System.Globalization;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Abstractions.Persistence;
using Docker.Manager.Application.Security;
using Docker.Manager.Core.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docker.Manager.Infrastructure.Persistence;

/// <summary>
/// 初始管理员引导实现。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：0.0.4 的验收标准是「登录成功」，但此时还没有任何创建用户的入口
/// （用户管理 API 在 0.0.8，且要求 admin 身份），存在鸡生蛋问题。因此 0.0.6 的引导被提前到这一版。</para>
/// <para><b>为什么通过 <see cref="IServiceScopeFactory"/> 取仓储</b>：与 <see cref="DatabaseInitializer"/>
/// 同理——本类是单例，仓储是 Scoped，直接注入会构成捕获依赖。</para>
/// <para><b>日志纪律</b>：只记用户名，<b>绝不</b>记密码。</para>
/// </remarks>
internal sealed class AdminBootstrapper : IAdminBootstrapper
{
    /// <summary>
    /// 初始管理员所用的内置角色名。
    /// </summary>
    internal const string AdminRoleName = Role.AdminName;

    private readonly IDbConnectionFactory _connections;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AdminOptions> _options;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<AdminBootstrapper> _logger;

    /// <summary>
    /// 初始化引导器。
    /// </summary>
    /// <param name="connections">连接工厂（用于按物理行数判断表是否为空）。</param>
    /// <param name="scopeFactory">作用域工厂（用于按需创建 Scoped 仓储）。</param>
    /// <param name="options">引导配置。</param>
    /// <param name="passwordHasher">密码哈希器。</param>
    /// <param name="logger">日志。</param>
    public AdminBootstrapper(
        IDbConnectionFactory connections,
        IServiceScopeFactory scopeFactory,
        IOptions<AdminOptions> options,
        IPasswordHasher passwordHasher,
        ILogger<AdminBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _scopeFactory = scopeFactory;
        _options = options;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 判空用「物理行数」而不是 IUserRepository.CountAsync：仓储计数会过滤软删除行，
        // 于是「所有用户都被软删除」时业务计数为 0 → 会被当成空表再次引导 →
        // 与残留用户名冲突（部分唯一索引）而启动失败。
        // 启动引导关心的是「这张表是否从未被初始化过」，属于基础设施关注点，不是业务查询。
        if (await this.CountUserRowsAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return;
        }

        var options = _options.Value;

        if (!options.IsConfigured)
        {
            // 只记 Warning 而不让启动失败：本地开发不必为了起服务先设两个环境变量；
            // 而「没有账号」在登录时会明确表现为凭据错误，不会静默成安全漏洞。
            _logger.LogWarning(
                "users 表为空，但未配置 Admin:User / Admin:Password（环境变量 ADMIN__USER / ADMIN__PASSWORD），"
                + "因此未创建初始管理员。在创建账号 API（0.0.8）落地前，未设置账号将无法登录。");

            return;
        }

        if (!PasswordPolicy.IsSatisfied(options.Password))
        {
            // 这是系统里还没有任何账号的唯一时机，放行弱密码等于把门开着不管；
            // 用配置类异常（而非业务异常）更贴切：它会出现在启动日志里而不是 HTTP 响应里。
            throw new InvalidOperationException(
                "初始管理员密码不满足强度要求（" + PasswordPolicy.DescribeRequirement()
                + "）。请修正环境变量 ADMIN__PASSWORD 后重启。");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        var adminRole = await roles.FindByNameAsync(AdminRoleName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"内置角色 {AdminRoleName} 不存在，数据库迁移不完整。");

        // 用户名格式由实体校验；不合法会抛 ArgumentException 使启动失败（而不是静默跳过）。
        var admin = User.Create(
            Guid.NewGuid(),
            options.User!,
            _passwordHasher.Hash(options.Password!),
            adminRole.Id);

        await users.AddAsync(admin, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("users 表为空，已创建初始管理员：{Username}", admin.Username);
    }

    /// <summary>
    /// 统计 <c>users</c> 表的物理行数。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>物理行数。</returns>
    private async Task<long> CountUserRowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM users";

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
