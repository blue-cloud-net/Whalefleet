using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Hosts;

/// <summary>
/// 主机管理用例（对应 <c>/api/v1/admin/hosts/*</c>，仅管理员可用）。
/// </summary>
/// <remarks>
/// <para><b>为什么主机名不可改</b>：它是对外标识——路径里的 <c>hostId</c> 就是主机名。改名会让
/// 已发出的深链、书签与前端缓存全部失效，因此只允许新建时定名。</para>
/// <para><b>为什么改配置后必须让客户端失效</b>：客户端池按「规范化端点 + 凭据指纹」复用连接，
/// 改了端点或凭据却不清旧条目，后续请求仍会用旧连接连过去——表现为「改了没生效」。</para>
/// <para><b>日志纪律</b>：只记主机名、端点与认证方式，<b>绝不</b>记凭据内容。</para>
/// </remarks>
public sealed class HostService
{
    /// <summary>
    /// 测试未保存配置时使用的占位主机标识。
    /// </summary>
    /// <remarks>只出现在日志与错误消息里；此时主机还没有名字，用占位符比编造一个名字更诚实。</remarks>
    internal const string UnsavedHostId = "（未保存）";

    private readonly IHostRepository _hosts;
    private readonly IDockerHostAccessor _accessor;
    private readonly ILogger<HostService> _logger;

    /// <summary>
    /// 初始化主机管理用例。
    /// </summary>
    /// <param name="hosts">主机仓储。</param>
    /// <param name="accessor">主机访问器（探活与客户端失效）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public HostService(
        IHostRepository hosts,
        IDockerHostAccessor accessor,
        ILogger<HostService> logger)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);

        _hosts = hosts;
        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>
    /// 列出全部主机（含连通状态）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主机列表，顺序为创建时间升序（同时间按名称），与仓储约定一致。</returns>
    /// <remarks>
    /// 逐台探活是<b>并行</b>的：串行时 N 台离线主机各自等一次超时，列表页会慢到不可用。
    /// 0.4.0 起改用后台健康轮询 + 实时推送后，这里应当只读缓存状态。
    /// </remarks>
    public async Task<IReadOnlyList<HostSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var hosts = await _hosts.QueryAllListAsync(cancellationToken).ConfigureAwait(false);

        var reachable = await Task
            .WhenAll(hosts.Select(host => this.ProbeAsync(host, cancellationToken)))
            .ConfigureAwait(false);

        return [.. hosts.Select((host, index) => Map(host, reachable[index]))];
    }

    /// <summary>
    /// 新建主机。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新主机（<c>Reachable</c> 为 <see langword="null"/>：未探测）。</returns>
    /// <exception cref="BusinessException">
    /// 字段非法（<c>1000</c>）或主机名已被占用（<c>1303</c>）。
    /// </exception>
    public async Task<HostSummary> CreateAsync(
        CreateHostRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = Require(request.Name, "name");
        var type = ParseType(request.Type);
        var endpoint = Require(request.Endpoint, "endpoint");
        var credentials = HostCredentialsFactory.From(request.TlsConfig, request.BasicAuth);

        // 唯一索引才是最终保证；这里只是为了给出可读错误而不是把 500 抛给用户。
        if (await _hosts.ExistsByNameAsync(name, cancellationToken).ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.HostNameAlreadyExists,
                $"主机名「{name}」已被使用（不区分大小写）");
        }

        // 端点合法性由 DockerEndpointNormalizer 在连接时判定（那才是唯一权威），
        // 这里只挡住"空端点"这类请求体问题，避免两处各写一套规则而漂移。
        var host = DockerHost.Register(Guid.NewGuid(), name, type, new HostEndpoint(endpoint), credentials);

        await _hosts.AddAsync(host, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "已新建主机 {HostName}（类型={Type}，端点={Endpoint}，认证={AuthKind}）",
            host.Name,
            host.Type,
            // 走脱敏而非 Value：端点是凭据载体（tcp://user:secret@host），
            // 而这条日志在连接校验之前就已经落盘了（见 HostEndpoint.Redact）
            HostEndpoint.Redact(host.Endpoint.Value),
            host.Credentials.Kind);

        // 不在创建时探活：写操作不该被网络超时拖住，连通性交给 test 接口或列表状态。
        return Map(host, reachable: null);
    }

    /// <summary>
    /// 更新主机的端点 / 凭据 / 启用状态（主机名不可改）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名，不区分大小写）。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新后的主机（<c>Reachable</c> 为 <see langword="null"/>：未探测）。</returns>
    /// <exception cref="BusinessException">
    /// 主机不存在（<c>1300</c>）或字段非法（<c>1000</c>）。
    /// </exception>
    public async Task<HostSummary> UpdateAsync(
        string hostId,
        UpdateHostRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        // 失效要用**变更前**的连接键：改完再算，算出来的是新键，旧条目会留在池里。
        var previousConnection = this.TryBuildConnection(host);

        var endpoint = request.Endpoint is null
            ? host.Endpoint
            : new HostEndpoint(Require(request.Endpoint, "endpoint"));

        var credentials = ResolveCredentials(host, request);

        var changed = false;

        if (endpoint != host.Endpoint || credentials != host.Credentials)
        {
            host.UpdateConnection(host.Type, endpoint, credentials);
            changed = true;
        }

        if (request.Enabled is { } enabled && enabled != host.Enabled)
        {
            if (enabled)
            {
                host.Enable();
            }
            else
            {
                host.Disable();
            }

            changed = true;
        }

        if (!changed)
        {
            // 幂等：什么都没变就不写库，免得白刷一次 last_modification_time 与审计字段。
            return Map(host, reachable: null);
        }

        await _hosts.UpdateAsync(host, cancellationToken).ConfigureAwait(false);

        if (previousConnection is not null)
        {
            _accessor.Invalidate(previousConnection);
        }

        _logger.LogInformation(
            "已更新主机 {HostName}（端点={Endpoint}，认证={AuthKind}，启用={Enabled}）",
            host.Name,
            HostEndpoint.Redact(host.Endpoint.Value),
            host.Credentials.Kind,
            host.Enabled);

        return Map(host, reachable: null);
    }

    /// <summary>
    /// 删除主机（软删除）。
    /// </summary>
    /// <param name="hostId">主机标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">主机不存在（<c>1300</c>）。</exception>
    public async Task DeleteAsync(string hostId, CancellationToken cancellationToken = default)
    {
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        // 先失效再删：删除后按名字就解析不到了，池里的客户端会一直挂着。
        var connection = this.TryBuildConnection(host);

        if (connection is not null)
        {
            _accessor.Invalidate(connection);
        }

        await _hosts.RemoveByIdAsync(host.Id, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("已删除主机 {HostName}", host.Name);
    }

    /// <summary>
    /// 测试一份**尚未保存**的主机配置。
    /// </summary>
    /// <param name="request">测试请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>能连通返回 <see langword="true"/>；否则返回 <see langword="false"/>（含端点非法、证书读不到、守护进程不可达）。</returns>
    /// <exception cref="BusinessException">请求字段非法（<c>1000</c>）。</exception>
    /// <remarks>
    /// 「连不上」为什么不细分原因：探活本身把各类失败统一收敛成 <c>false</c>
    /// （见 <see cref="IDockerHostAccessor.TestConnectionAsync"/>），
    /// 界面需要的是「能不能用」，而具体原因写在服务端日志里。
    /// </remarks>
    public async Task<bool> TestAsync(TestHostRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = new DockerHostConnection
        {
            HostId = UnsavedHostId,
            Type = ParseType(request.Type),
            Endpoint = Require(request.Endpoint, "endpoint"),
            Auth = DockerHostAuth.From(HostCredentialsFactory.From(request.TlsConfig, request.BasicAuth)),
        };

        return await _accessor.TestConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static HostSummary Map(DockerHost host, bool? reachable) => new(
        host.Id,
        host.Name,
        host.Type,
        host.Endpoint.Value,
        host.Enabled,
        reachable,
        host.Credentials.Kind,
        host.GroupId);

    private static HostCredentials ResolveCredentials(DockerHost host, UpdateHostRequest request)
    {
        if (request.ClearCredentials == true)
        {
            if (request.TlsConfig is not null || request.BasicAuth is not null)
            {
                throw BusinessException.Validation("clearCredentials 与 tlsConfig / basicAuth 互斥");
            }

            return HostCredentials.None;
        }

        return request.TlsConfig is null && request.BasicAuth is null
            ? host.Credentials
            : HostCredentialsFactory.From(request.TlsConfig, request.BasicAuth);
    }

    private static DockerHostType ParseType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "socket" => DockerHostType.Socket,
        "tcp" => DockerHostType.Tcp,
        _ => throw BusinessException.Validation("type 必须是 socket 或 tcp"),
    };

    private static string Require(string? value, string field)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            throw BusinessException.Validation($"{field} 不能为空");
        }

        return value.Trim();
    }

    private DockerHostConnection? TryBuildConnection(DockerHost host)
    {
        try
        {
            return DockerHostConnection.From(host);
        }
        catch (BusinessException exception)
        {
            // 旧凭据本身已损坏（例如手工改库）：连"要失效的连接键"都算不出来，
            // 池里也不会存在它的条目，记一条日志继续走删除 / 更新流程。
            _logger.LogWarning(exception, "主机 {HostName} 的凭据无法解析，跳过客户端失效", host.Name);

            return null;
        }
    }

    private async Task<bool> ProbeAsync(DockerHost host, CancellationToken cancellationToken)
    {
        // 停用的主机不去连：连不上不代表它坏了，只是管理员主动停用了。
        if (!host.Enabled)
        {
            return false;
        }

        try
        {
            return await _accessor
                .TestConnectionAsync(DockerHostConnection.From(host), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BusinessException exception)
        {
            // 配置不可用（凭据坏、端点非法）在列表里同样呈现为"不可达"，原因写日志。
            _logger.LogWarning(exception, "主机 {HostName} 的配置不可用，按不可达处理", host.Name);

            return false;
        }
    }
}
