using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Registries;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Registries;

/// <summary>
/// Registry 配置用例（列出 / 新增 / 删除 / 测试连通性），
/// 对应 <c>/api/v1/admin/registries/*</c>。
/// </summary>
/// <remarks>
/// <para><b>本层不做权限判定</b>：调用这些端点需要 <c>registry.read</c> / <c>registry.write</c>，
/// 由 Api 层的权限策略把关。与 <c>RoleService</c> 同一口径。</para>
/// <para><b>口令在应用层就要过一遍「保护器」</b>：加密发生在写入前，解密只发生在
/// 「真的要拿凭据去连 Docker」的那一刻。应用层拿到的是明文，但<b>只存在于那一次调用的栈上</b>，
/// 不落库、不进日志、不进响应。</para>
/// <para><b>唯一性判定在这里而不是实体里</b>：「这个名字有没有被占用」要查库，
/// 属于跨行规则（与 <c>RoleService</c> 判定角色名冲突同一口径）。</para>
/// <para><b>日志纪律</b>：只记配置主键、别名与地址，<b>绝不</b>记口令、
/// 口令密文或任何可还原的口令片段。</para>
/// </remarks>
public sealed class RegistryService : IRegistryCredentialResolver
{
    private readonly IRegistryCredentialRepository _registries;
    private readonly IRegistrySecretProtector _protector;
    private readonly IRegistryConnectivityProbe _probe;
    private readonly ILogger<RegistryService> _logger;

    /// <summary>
    /// 初始化 Registry 用例。
    /// </summary>
    /// <param name="registries">配置仓储。</param>
    /// <param name="protector">口令保护器。</param>
    /// <param name="probe">连通性探测。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public RegistryService(
        IRegistryCredentialRepository registries,
        IRegistrySecretProtector protector,
        IRegistryConnectivityProbe probe,
        ILogger<RegistryService> logger)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(logger);

        _registries = registries;
        _protector = protector;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>
    /// 列出 Registry 配置（<b>不含任何口令信息</b>）。
    /// </summary>
    /// <param name="request">查询请求（可按别名 / 地址模糊过滤）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置列表；无配置时返回<b>空列表</b>。</returns>
    public async Task<IReadOnlyList<RegistrySummary>> ListAsync(
        ListRegistriesRequest? request,
        CancellationToken cancellationToken = default)
    {
        var all = await _registries.QueryAllListAsync(cancellationToken).ConfigureAwait(false);
        var search = request?.Search?.Trim();

        var filtered = string.IsNullOrEmpty(search)
            ? all
            : all.Where(item => Matches(item, search)).ToList();

        return [.. filtered.Select(Map)];
    }

    /// <summary>
    /// 新增一条 Registry 配置。
    /// </summary>
    /// <param name="request">新增请求（别名、地址、账号、口令）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的配置（不含口令）。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、别名已占用（<c>1801</c>）或地址已占用（<c>1802</c>）。
    /// </exception>
    public async Task<RegistrySummary> AddAsync(
        AddRegistryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireText(request.Name, "name");
        var address = RegistryServerAddress.Parse(request.ServerAddress ?? string.Empty);
        var username = (request.Username ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;

        // 账号与口令必须成对：半截凭据推镜像时只会得到「认证失败」，
        // 而用户看不出是自己的配置缺了一半。
        if (username.Length > 0 ^ password.Length > 0)
        {
            throw BusinessException.Validation("账号与口令必须同时提供（匿名访问则两者都留空）");
        }

        if (await _registries.ExistsByNameAsync(name, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.RegistryNameAlreadyExists,
                $"Registry 别名「{name}」已被占用");
        }

        if (await _registries.ExistsByServerAddressAsync(address.Value, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.RegistryAddressAlreadyExists,
                $"Registry 地址「{address.Value}」已被占用");
        }

        var encrypted = username.Length > 0 ? _protector.Protect(password) : string.Empty;

        var entity = RegistryCredential.Register(
            Guid.NewGuid(),
            name,
            address.Value,
            username,
            encrypted,
            request.Description);

        await _registries.AddAsync(entity, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "已新增 Registry 配置 {RegistryId}（别名 {Name}，地址 {ServerAddress}，是否带凭据 {HasCredentials}）",
            entity.Id,
            entity.Name,
            entity.ServerAddress,
            entity.HasCredentials);

        return Map(entity);
    }

    /// <summary>
    /// 删除一条 Registry 配置（软删除）。
    /// </summary>
    /// <param name="request">删除请求（配置主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="BusinessException">
    /// 主键为空（<c>1000</c>）或配置不存在（<c>1800</c>）。
    /// </exception>
    public async Task DeleteAsync(
        RemoveRegistryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Id is not { } id || id == Guid.Empty)
        {
            throw BusinessException.Validation("id 不能为空");
        }

        var removed = await _registries.RemoveByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (!removed)
        {
            throw new BusinessException(ErrorCodes.RegistryNotFound, "Registry 配置不存在");
        }

        _logger.LogInformation("已删除 Registry 配置 {RegistryId}", id);
    }

    /// <summary>
    /// 测试 Registry 的连通性与凭据。
    /// </summary>
    /// <param name="request">测试请求（已保存的配置主键，或地址 + 凭据）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>测试结果。</returns>
    /// <exception cref="BusinessException">
    /// 既没给主键也没给地址（<c>1000</c>），或主键对应的配置不存在（<c>1800</c>）。
    /// </exception>
    /// <remarks>
    /// <b>「连不上」与「认证被拒」分别落到不同错误码</b>：前者是 <c>1805</c>（502，对端不可达），
    /// 后者是 <c>1804</c>（400，凭据不对）。前者该查网络与防火墙，后者该重填口令——
    /// 归成同一个码，用户只能靠猜。</remarks>
    public async Task<RegistryTestResult> TestAsync(
        TestRegistryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string address;
        string username;
        string password;

        if (request.Id is { } id && id != Guid.Empty)
        {
            var saved = await _registries.QueryByIdAsync(id, cancellationToken).ConfigureAwait(false);

            if (saved is null)
            {
                throw new BusinessException(ErrorCodes.RegistryNotFound, "Registry 配置不存在");
            }

            address = saved.ServerAddress;
            username = saved.Username;

            // 已保存配置的口令是密文，用前先解。
            password = saved.HasCredentials
                ? _protector.Unprotect(saved.EncryptedPassword)
                : string.Empty;
        }
        else
        {
            address = RegistryServerAddress.Parse(request.ServerAddress ?? string.Empty).Value;
            username = (request.Username ?? string.Empty).Trim();
            password = request.Password ?? string.Empty;

            if (username.Length > 0 ^ password.Length > 0)
            {
                throw BusinessException.Validation("账号与口令必须同时提供（匿名访问则两者都留空）");
            }
        }

        var outcome = await _probe
            .ProbeAsync(address, username, password, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Reachable)
        {
            throw new BusinessException(
                ErrorCodes.RegistryUnreachable,
                $"无法连接 Registry「{address}」：{outcome.Message}");
        }

        if (outcome.RequiresAuthentication && !outcome.Authenticated)
        {
            throw new BusinessException(
                ErrorCodes.RegistryAuthFailed,
                $"Registry「{address}」拒绝了该凭据：{outcome.Message}");
        }

        _logger.LogInformation(
            "Registry {ServerAddress} 连通性测试完成（可达={Reachable}，已认证={Authenticated}）",
            address,
            outcome.Reachable,
            outcome.Authenticated);

        return new RegistryTestResult(outcome.Reachable, outcome.Authenticated, outcome.Message);
    }

    /// <summary>
    /// 解析某个地址对应的 Registry 凭据（供推镜像时使用）。
    /// </summary>
    /// <param name="serverAddress">服务地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>凭据；未登记该地址时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>返回的明文只用于这一次调用</b>：调用方把它塞进 Docker 的认证配置，
    /// 随即丢弃。它不进日志、不进响应、不落库。
    /// </remarks>
    public async Task<RegistryCredentialValue?> ResolveCredentialAsync(
        string serverAddress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);

        var saved = await _registries
            .FindByServerAddressAsync(serverAddress.Trim().ToLowerInvariant(), cancellationToken)
            .ConfigureAwait(false);

        if (saved is null)
        {
            return null;
        }

        return new RegistryCredentialValue(
            saved.ServerAddress,
            saved.Username,
            saved.HasCredentials ? _protector.Unprotect(saved.EncryptedPassword) : string.Empty);
    }

    /// <summary>
    /// 把实体映射成列表项（<b>不含口令</b>）。
    /// </summary>
    private static RegistrySummary Map(RegistryCredential entity) => new(
        entity.Id,
        entity.Name,
        entity.ServerAddress,
        entity.Username,
        entity.HasCredentials,
        entity.Description,
        entity.CreationTime,
        entity.LastModificationTime);

    /// <summary>
    /// 按关键字过滤（别名或地址，忽略大小写）。
    /// </summary>
    private static bool Matches(RegistryCredential entity, string search) =>
        entity.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || entity.ServerAddress.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 必填文本校验。
    /// </summary>
    private static string RequireText(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw BusinessException.Validation($"{field} 不能为空")
            : value.Trim();
}

/// <summary>
/// 解析出的 Registry 凭据（<b>明文</b>，只在单次调用内存在）。
/// </summary>
/// <param name="ServerAddress">规范化后的服务地址。</param>
/// <param name="Username">登录账号；为空表示匿名访问。</param>
/// <param name="Password">登录口令；为空表示匿名访问。</param>
public sealed record RegistryCredentialValue(
    string ServerAddress,
    string Username,
    string Password);
