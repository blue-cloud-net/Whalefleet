using System.Globalization;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Application.Security;
using Whalefleet.Core.Users;
using Whalefleet.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whalefleet.Infrastructure.Persistence;

/// <summary>
/// 初始管理员引导实现。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：0.0.4 的验收标准是「登录成功」，但此时还没有任何创建用户的入口
/// （用户管理 API 在 0.0.8，且要求 admin 身份），存在鸡生蛋问题。因此部署迭代（现 0.0.15）的引导被提前到这一版。</para>
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

    /// <summary>
    /// 未配置 <c>Admin:User</c> 时使用的默认用户名。
    /// </summary>
    internal const string DefaultAdminUserName = "admin";

    private readonly IDbConnectionFactory _connections;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AdminOptions> _options;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SecretFileStore _secretStore;
    private readonly ILogger<AdminBootstrapper> _logger;

    /// <summary>
    /// 初始化引导器。
    /// </summary>
    /// <param name="connections">连接工厂（用于按物理行数判断表是否为空）。</param>
    /// <param name="scopeFactory">作用域工厂（用于按需创建 Scoped 仓储）。</param>
    /// <param name="options">引导配置。</param>
    /// <param name="passwordHasher">密码哈希器。</param>
    /// <param name="secretStore">密钥文件存储（用于落盘自动生成的密码）。</param>
    /// <param name="logger">日志。</param>
    public AdminBootstrapper(
        IDbConnectionFactory connections,
        IServiceScopeFactory scopeFactory,
        IOptions<AdminOptions> options,
        IPasswordHasher passwordHasher,
        SecretFileStore secretStore,
        ILogger<AdminBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _scopeFactory = scopeFactory;
        _options = options;
        _passwordHasher = passwordHasher;
        _secretStore = secretStore;
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

        // 显式关闭自举：账号由外部预置（随备份恢复 / 运维脚本写库）。
        // 此时继续自举会与既有账号抢 admin 这个用户名，把启动搞崩。
        if (!options.AutoBootstrap)
        {
            _logger.LogInformation(
                "已关闭管理员自举（Admin:AutoBootstrap=false），users 表为空时不会创建任何账号。");

            return;
        }

        // 未配置密码时自动生成并落盘，而不是"记个 Warning 然后什么都不做"：
        // 后者意味着零配置部署根本登不进去，用户还得回头翻文档、编辑 .env、重启。
        // 自举出来的密码写在数据目录的 initial-admin.txt（0600），
        // **不进日志**——日志会被采集器上报到集中式系统，等价于把密码扩散出去。
        var password = options.Password;
        var generated = false;

        if (String.IsNullOrWhiteSpace(password))
        {
            // 用户名照常生效（部署方明确指定了就该尊重），
            // 只有密码改由生成——它才是必须保密的那一半。
            _logger.LogInformation(
                "未配置 Admin:Password，已为初始管理员自动生成随机密码（用户名取自 Admin:User，未配置时为 admin）。");

            password = GeneratedPassword.Create();
            generated = true;
        }

        if (!PasswordPolicy.IsSatisfied(password))
        {
            // 这是系统里还没有任何账号的唯一时机，放行弱密码等于把门开着不管；
            // 用配置类异常（而非业务异常）更贴切：它会出现在启动日志里而不是 HTTP 响应里。
            // 自动生成的密码走不到这里（生成器保证过策略），因此该分支只可能由显式弱配置触发。
            throw new InvalidOperationException(
                "初始管理员密码不满足强度要求（" + PasswordPolicy.DescribeRequirement()
                + "）。请修正环境变量 ADMIN__PASSWORD 后重启。");
        }

        var username = String.IsNullOrWhiteSpace(options.User) ? DefaultAdminUserName : options.User;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        var adminRole = await roles.FindByNameAsync(AdminRoleName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"内置角色 {AdminRoleName} 不存在，数据库迁移不完整。");

        // 用户名格式由实体校验；不合法会抛 ArgumentException 使启动失败（而不是静默跳过）。
        var admin = User.Create(
            Guid.NewGuid(),
            username,
            _passwordHasher.Hash(password),
            adminRole.Id);

        await users.AddAsync(admin, cancellationToken).ConfigureAwait(false);

        if (generated)
        {
            // 记在创建账号之后：文件写入失败时，启动会以异常中止，
            // 而 users 表已有行——下次启动会走"表非空"分支直接跳过，不会重复生成。
            var path = _secretStore.WriteInitialAdminCredential(username, password);

            _logger.LogInformation("已创建初始管理员：{Username}（密码见 {Path}）", admin.Username, path);
        }
        else
        {
            _logger.LogInformation("users 表为空，已创建初始管理员：{Username}", admin.Username);
        }
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
