using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 仓库连通性探测的实现：走 Docker Registry HTTP API V2 的 <c>GET /v2/</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么用 <c>GET /v2/</c></b>：这是 Registry V2 规范定义的<b>版本检查端点</b>。
/// 它的语义恰好就是我们要问的问题——「这个地址存在吗？允许我访问吗？」：
/// <list type="bullet">
///   <item><description><c>200</c>：地址可达，且当前身份被允许访问；</description></item>
///   <item><description><c>401</c>：地址可达，但需要认证（响应带 <c>WWW-Authenticate</c> 挑战）；</description></item>
///   <item><description><c>403</c>：地址可达，但当前身份被拒绝；</description></item>
///   <item><description>连接失败 / 超时：地址不可达。</description></item>
/// </list></para>
/// <para><b>为什么不用 Docker 客户端去试</b>：Docker 客户端只会跟守护进程说话。
/// 而「这个仓库地址能不能用」只有仓库自己能回答——
/// 用「拉一个镜像」来试探会把「网络不通」「没权限」「镜像不存在」三种原因混成一个。</para>
/// <para><b>为什么先试 HTTPS 再退回 HTTP</b>：自建仓库绝大多数跑在明文 HTTP 上
/// （<c>registry:2</c> 默认如此），而公网 Registry 走 HTTPS。
/// 端口号不同时两者都试；端口为 443 时只试 HTTPS（那几乎必然是 HTTPS 站点）。
/// 报告里会写明最终用的是哪一种，免得用户对着「明明是 http 站点」困惑。</para>
/// <para><b>为什么用 Basic 认证</b>：私有仓库（Harbor、registry:2 + htpasswd）
/// 默认支持 Basic；Bearer 令牌流是 Docker Hub 那类「先换 token 再访问」的形态，
/// 属于另一个协议层次。这里刻意不实现它——真要支持 Harbor 的 token 端点，
/// 应当直接用 Docker 守护进程去认证（<c>docker login</c> 的语义），
/// 而不是在这里另造一套半吊子实现。</para>
/// <para><b>异常一律吞成结果</b>：探测失败是<b>结果</b>而不是程序错误。</para>
/// </remarks>
internal sealed class RepositoryConnectivityProbe : IRepositoryConnectivityProbe
{
    /// <summary>
    /// 探测超时（秒）。
    /// </summary>
    /// <remarks>
    /// 取 10 秒：自建仓库多在内网，连接建立慢是常态；
    /// 而这个值直接决定「点一下测试按钮要等多久」，不能太长。
    /// </remarks>
    private const int TimeoutSeconds = 10;

    /// <summary>
    /// Registry V2 版本检查端点。
    /// </summary>
    private const string VersionCheckPath = "/v2/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RepositoryConnectivityProbe> _logger;

    /// <summary>
    /// 初始化探测器。
    /// </summary>
    /// <param name="httpClientFactory">HTTP 客户端工厂。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public RepositoryConnectivityProbe(
        IHttpClientFactory httpClientFactory,
        ILogger<RepositoryConnectivityProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RepositoryProbeOutcome> ProbeAsync(
        string serverAddress,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);

        if (!RepositoryServerAddress.TryParse(serverAddress, out var address) || address is null)
        {
            return new RepositoryProbeOutcome(false, false, false, "仓库地址格式非法");
        }

        var schemes = ResolveSchemes(address);

        foreach (var scheme in schemes)
        {
            var outcome = await this.ProbeOnceAsync(address, scheme, username, password, cancellationToken)
                .ConfigureAwait(false);

            if (outcome is not null)
            {
                _logger.LogDebug(
                    "仓库 {ServerAddress} 探测完成（协议 {Scheme}）：可达={Reachable}，已认证={Authenticated}",
                    address.Value,
                    scheme,
                    outcome.Reachable,
                    outcome.Authenticated);

                return outcome;
            }
        }

        // 所有协议都试过且都失败：给出「不可达」。
        return new RepositoryProbeOutcome(
            false,
            false,
            false,
            $"无法连接 {address.Value}（试过 {String.Join('、', schemes)}）");
    }

    /// <summary>
    /// 按给定协议探测一次；返回 <see langword="null"/> 表示该协议下连接层就失败了（可换协议再试）。
    /// </summary>
    private async Task<RepositoryProbeOutcome?> ProbeOnceAsync(
        RepositoryServerAddress address,
        string scheme,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var url = $"{scheme}://{address.Value}{VersionCheckPath}";

        var client = _httpClientFactory.CreateClient(nameof(RepositoryConnectivityProbe));
        client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrEmpty(username))
        {
            var raw = $"{username}:{password}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        HttpResponseMessage response;

        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug(
                "仓库 {ServerAddress} 以 {Scheme} 协议连接失败：{Message}",
                address.Value,
                scheme,
                exception.Message);

            return null;
        }

        using (response)
        {
            var status = response.StatusCode;

            if (status == HttpStatusCode.OK)
            {
                return new RepositoryProbeOutcome(
                    true,
                    true,
                    false,
                    $"连接成功（{scheme}），凭据有效");
            }

            if (status == HttpStatusCode.Unauthorized)
            {
                var hasCredentials = !string.IsNullOrEmpty(username);
                var challenged = response.Headers.WwwAuthenticate.Count > 0;

                return new RepositoryProbeOutcome(
                    true,
                    false,
                    true,
                    hasCredentials
                        ? "连接成功，但 仓库拒绝了该凭据（账号或口令错误）"
                        : challenged
                            ? "连接成功，但该仓库要求认证（未提供凭据）"
                            : "连接成功，但该仓库要求认证");
            }

            if (status == HttpStatusCode.Forbidden)
            {
                return new RepositoryProbeOutcome(
                    true,
                    false,
                    true,
                    "连接成功，但当前身份被 仓库拒绝（无权访问）");
            }

            // 其它状态码：地址是活的，但它不是个可用的 Registry V2 服务。
            return new RepositoryProbeOutcome(
                true,
                false,
                false,
                $"连接成功，但返回了 HTTP {(int)status}（该地址可能不是 Registry V2 服务）");
        }
    }

    /// <summary>
    /// 决定要试哪些协议。
    /// </summary>
    /// <param name="address">仓库地址。</param>
    /// <returns>协议列表（按尝试顺序）。</returns>
    private static IReadOnlyList<string> ResolveSchemes(RepositoryServerAddress address)
    {
        // 显式指定了端口：既可能是明文自建仓库（5000），也可能是 HTTPS 自建仓库（8443）。
        // 两种都试，代价是一次多几百毫秒的连接失败。
        if (address.Port is { } port && port != 443)
        {
            return ["http", "https"];
        }

        return ["https", "http"];
    }
}
