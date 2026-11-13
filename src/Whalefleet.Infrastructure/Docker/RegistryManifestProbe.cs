using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Images;

namespace Whalefleet.Infrastructure.Docker;

/// <summary>
/// 仓库清单探测的实现：走 Registry HTTP API V2 的
/// <c>HEAD /v2/&lt;name&gt;/manifests/&lt;tag&gt;</c>。
/// </summary>
/// <remarks>
/// <para><b>只发 HEAD，不拉层</b>：响应头 <c>Docker-Content-Digest</c> 就是清单摘要。
/// 走 <c>docker pull</c> 检查更新会真的拉下几百 MB，而这一步只该问一个
/// 「现在指向哪个摘要」。</para>
/// <para><b>认证三态</b>：公开镜像 200 直接返回；私有镜像先 401 + <c>WWW-Authenticate</c>
/// 挑战，此时按 <c>realm</c> 换令牌（Bearer）或直接带 Basic 重试。
/// 令牌只活在一次调用内，<b>不进日志、不进响应</b>。</para>
/// <para><b>镜像加速器</b>：Docker Hub 在部分网络下直连会被限流（<c>429</c>）。
/// 这里先把请求打到守护进程自己配的镜像（<c>/info</c> 的 <c>RegistryConfig.Mirrors</c>），
/// 拿到摘要就结束——<b>从守护进程读而不是读容器里的 <c>/etc/docker/daemon.json</c></b>：
/// 那条路径在「远程主机」场景下根本读不到，而 <c>/info</c> 是跨主机的。</para>
/// <para><b>异常一律吞成结论</b>：探测失败是结果，不是程序错误——
/// 一次网络抖动不该让整个扫描中断。</para>
/// </remarks>
internal sealed class RegistryManifestProbe : IRegistryManifestProbe
{
    /// <summary>
    /// 单次探测超时（秒）。
    /// </summary>
    /// <remarks>
    /// 取 8 秒：扫描按周期跑，挂住一个请求就会拖住后面的全部策略。
    /// </remarks>
    private const int _timeoutSeconds = 8;

    /// <summary>
    /// 清单的 Accept 头：把几种主流类型都列上，否则部分仓库会退回
    /// 老式 schema 1（那种响应没有 <c>Docker-Content-Digest</c>）。
    /// </summary>
    private const string _manifestAccept =
        "application/vnd.oci.image.index.v1+json, "
        + "application/vnd.docker.distribution.manifest.list.v2+json, "
        + "application/vnd.oci.image.manifest.v1+json, "
        + "application/vnd.docker.distribution.manifest.v2+json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRepositoryCredentialResolver _credentials;
    private readonly IOptions<ImageUpdatesOptions> _options;
    private readonly ILogger<RegistryManifestProbe> _logger;

    /// <summary>
    /// 初始化探测器。
    /// </summary>
    /// <param name="httpClientFactory">HTTP 客户端工厂。</param>
    /// <param name="credentials">仓库凭据解析。</param>
    /// <param name="options">镜像更新选项。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public RegistryManifestProbe(
        IHttpClientFactory httpClientFactory,
        IRepositoryCredentialResolver credentials,
        IOptions<ImageUpdatesOptions> options,
        ILogger<RegistryManifestProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RegistryProbeResult> ProbeAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var target = RegistryRepositoryPath.From(repository);

        try
        {
            // 1) 加速器（仅 Docker Hub 有意义）：拿到摘要就结束，省一次跨境往返。
            if (String.Equals(
                    target.ServerAddress,
                    ImageReference.DockerHubServerAddress,
                    StringComparison.OrdinalIgnoreCase))
            {
                foreach (var mirror in this.ResolveMirrors())
                {
                    var mirrored = await this.ProbeHostAsync(
                        mirror,
                        target.Repository,
                        tag,
                        useCredentials: false,
                        cancellationToken).ConfigureAwait(false);

                    if (mirrored.Succeeded)
                    {
                        return mirrored;
                    }
                }
            }

            // 2) 主仓库：带凭据，走完整的挑战流程。
            return await this.ProbeHostAsync(
                $"https://{target.ServerAddress}",
                target.Repository,
                tag,
                useCredentials: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RegistryProbeResult(RegistryProbeStatus.Unreachable, Message: "探测超时");
        }
        catch (HttpRequestException error)
        {
            _logger.LogDebug(error, "探测 {Repository}:{Tag} 时仓库不可达", repository, tag);

            return new RegistryProbeResult(
                RegistryProbeStatus.Unreachable,
                Message: "无法连接仓库");
        }
    }

    /// <summary>
    /// 对某个具体主机执行一次清单探测（含挑战重试）。
    /// </summary>
    /// <param name="baseAddress">主机基址（含协议）。</param>
    /// <param name="repository">仓库路径（已归一化）。</param>
    /// <param name="tag">标签。</param>
    /// <param name="useCredentials">是否允许带仓库凭据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探测结论。</returns>
    private async Task<RegistryProbeResult> ProbeHostAsync(
        string baseAddress,
        string repository,
        string tag,
        bool useCredentials,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);

        var manifestUrl = $"{baseAddress.TrimEnd('/')}/v2/{repository}/manifests/{Uri.EscapeDataString(tag)}";

        (string? User, string? Password) credentials = (null, null);

        if (useCredentials)
        {
            var resolved = await _credentials
                .ResolveCredentialAsync(new Uri(baseAddress).Host, cancellationToken)
                .ConfigureAwait(false);

            if (resolved is not null)
            {
                credentials = (resolved.Username, resolved.Password);
            }
        }

        using var first = await SendHeadAsync(client, manifestUrl, credentials, cancellationToken)
            .ConfigureAwait(false);

        if (first.StatusCode == HttpStatusCode.OK)
        {
            return ReadDigest(first);
        }

        if (first.StatusCode == HttpStatusCode.NotFound)
        {
            return new RegistryProbeResult(
                RegistryProbeStatus.NotFound,
                Message: "仓库里没有这个镜像或标签");
        }

        if (first.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new RegistryProbeResult(
                RegistryProbeStatus.RateLimited,
                Message: "仓库限流（请稍后重试或配置仓库凭据）");
        }

        if (first.StatusCode != HttpStatusCode.Unauthorized)
        {
            return new RegistryProbeResult(
                RegistryProbeStatus.Unreachable,
                Message: $"仓库返回 {(int)first.StatusCode}");
        }

        var challenge = RegistryAuthChallenge.TryParse(
            first.Headers.WwwAuthenticate.ToString());

        // 私有仓库：先换令牌（Bearer），换不到再考虑 Basic（部分自建仓库直接收 Basic）。
        if (challenge?.Scope is not null && challenge.Realm is not null && credentials.User is not null)
        {
            var token = await this.RequestTokenAsync(challenge, credentials, cancellationToken)
                .ConfigureAwait(false);

            if (token is not null)
            {
                using var authorized = await SendHeadAsync(
                    client,
                    manifestUrl,
                    credentials,
                    cancellationToken,
                    token).ConfigureAwait(false);

                if (authorized.StatusCode == HttpStatusCode.OK)
                {
                    return ReadDigest(authorized);
                }

                if (authorized.StatusCode == HttpStatusCode.NotFound)
                {
                    return new RegistryProbeResult(
                        RegistryProbeStatus.NotFound,
                        Message: "仓库里没有这个镜像或标签");
                }
            }
        }

        return new RegistryProbeResult(
            RegistryProbeStatus.Unauthorized,
            Message: credentials.User is null
                ? "仓库要求授权，尚未配置该仓库的凭据"
                : "仓库拒绝了当前凭据");
    }

    /// <summary>
    /// 发送 <c>HEAD</c> 请求（可带 Basic 凭据与 Bearer 令牌）。
    /// </summary>
    /// <param name="client">HTTP 客户端。</param>
    /// <param name="url">清单地址。</param>
    /// <param name="credentials">仓库凭据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="token">Bearer 令牌；<see langword="null"/> 表示不带。</param>
    /// <returns>响应（调用方负责释放）。</returns>
    private static Task<HttpResponseMessage> SendHeadAsync(
        HttpClient client,
        string url,
        (string? User, string? Password) credentials,
        CancellationToken cancellationToken,
        string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);

        request.Headers.TryAddWithoutValidation("Accept", _manifestAccept);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (credentials.User is not null && credentials.Password is not null)
        {
            var basic = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{credentials.User}:{credentials.Password}"));

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        return client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// 按挑战头换取 Bearer 令牌。
    /// </summary>
    /// <param name="challenge">挑战头解析结果。</param>
    /// <param name="credentials">仓库凭据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>令牌；换不到时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>口令只进请求，不进日志</b>：日志里只有主机与仓库路径。
    /// </remarks>
    private async Task<string?> RequestTokenAsync(
        RegistryAuthChallenge challenge,
        (string? User, string? Password) credentials,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);

        var query = new List<string> { $"scope={Uri.EscapeDataString(challenge.Scope!)}" };

        if (!String.IsNullOrWhiteSpace(challenge.Service))
        {
            query.Add($"service={Uri.EscapeDataString(challenge.Service)}");
        }

        var separator = challenge.Realm!.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{challenge.Realm}{separator}{String.Join('&', query)}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (credentials.User is not null && credentials.Password is not null)
        {
            var basic = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{credentials.User}:{credentials.Password}"));

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<TokenPayload>(cancellationToken)
                .ConfigureAwait(false);

            return String.IsNullOrWhiteSpace(payload?.Token) ? null : payload.Token;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        {
            _logger.LogDebug(error, "换取仓库令牌失败");

            return null;
        }
    }

    /// <summary>
    /// 从响应头读摘要。
    /// </summary>
    /// <param name="response">响应。</param>
    /// <returns>探测结论。</returns>
    private static RegistryProbeResult ReadDigest(HttpResponseMessage response)
    {
        var digest = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault()
            : null;

        return String.IsNullOrWhiteSpace(digest)
            ? new RegistryProbeResult(
                RegistryProbeStatus.Unreachable,
                Message: "仓库未返回清单摘要（可能是 schema 1 仓库）")
            : new RegistryProbeResult(RegistryProbeStatus.Digest, digest!.Trim());
    }

    /// <summary>
    /// 解析需要尝试的镜像加速器地址。
    /// </summary>
    /// <returns>基址列表（含协议）；未配置时为空。</returns>
    private List<string> ResolveMirrors()
    {
        var mirrors = new List<string>();

        foreach (var entry in _options.Value.RegistryMirrors)
        {
            if (String.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim().TrimEnd('/');

            mirrors.Add(
                trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? trimmed
                    : $"https://{trimmed}");
        }

        return mirrors;
    }

    /// <summary>
    /// 令牌端点的响应体（只取 <c>token</c>）。
    /// </summary>
    private sealed record TokenPayload(
        [property: JsonPropertyName("token")] string? Token);
}
