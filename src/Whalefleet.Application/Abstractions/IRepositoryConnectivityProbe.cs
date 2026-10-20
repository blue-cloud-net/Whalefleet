namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 仓库连通性探测（外部资源抽象）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要独立于 Docker 客户端</b>：Docker 客户端只能连<b>守护进程</b>，
/// 而「这个仓库地址能不能用」是另一件事——它由 仓库自己的 HTTP 接口回答。
/// 用 Docker 客户端去「试探」仓库既做不到，也说不清失败原因。</para>
/// <para><b>为什么测试要区分「连不上」与「认证被拒」</b>：两者要开的药方完全不同
/// （查网络 / 换凭据）。压成一个布尔值的话，界面只能显示「失败」。</para>
/// </remarks>
public interface IRepositoryConnectivityProbe
{
    /// <summary>
    /// 探测 仓库地址的连通性与认证状态。
    /// </summary>
    /// <param name="serverAddress">服务地址（<c>host</c> 或 <c>host:port</c>）。</param>
    /// <param name="username">登录账号；为空表示匿名探测。</param>
    /// <param name="password">登录口令；为空表示匿名探测。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结果（连通、认证状态与说明）。</returns>
    /// <remarks>
    /// <b>不抛异常</b>：网络不通、证书错误、认证失败都是<b>探测结果</b>而不是程序错误。
    /// 抛异常会让「测一下这个地址」变成一次 500。
    /// </remarks>
    Task<RepositoryProbeOutcome> ProbeAsync(
        string serverAddress,
        string? username,
        string? password,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 仓库探测结果。
/// </summary>
/// <param name="Reachable">网络层是否可达。</param>
/// <param name="Authenticated">凭据是否被接受。</param>
/// <param name="Message">结果说明（可直接展示给用户）。</param>
/// <param name="RequiresAuthentication">服务端是否要求认证（<c>401</c> 挑战）。</param>
public sealed record RepositoryProbeOutcome(
    bool Reachable,
    bool Authenticated,
    bool RequiresAuthentication,
    string Message);
