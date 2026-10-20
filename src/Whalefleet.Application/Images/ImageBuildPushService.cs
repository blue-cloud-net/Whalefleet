using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Whalefleet.Application.Registries;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Images;

/// <summary>
/// 镜像构建与推送用例，对应 <c>/api/v1/hosts/{hostId}/images/{build,push}</c>。
/// </summary>
/// <remarks>
/// <para><b>为什么与 <see cref="ImageService"/> 分开</b>：本类的两个用例各自要拖一堆
/// 额外依赖——构建要读上传流、推送要查 Registry 凭据。合进 <see cref="ImageService"/>
/// 会把它的依赖从 2 个（主机访问器 + 日志）变成 5 个，而列表 / 拉取 / 删除一个都用不上。</para>
/// <para><b>构建失败只能从进度流里读出来</b>：与拉取同理，Docker 构建失败时
/// HTTP 层仍是 200，错误只在进度消息里。因此 <see cref="StreamCollector"/> 与
/// <c>PullCollector</c> 同构，都是<b>必须检查的数据源</b>而非日志素材。</para>
/// <para><b>日志纪律</b>：构建参数（<c>ARG</c>）与 <c>LABEL</c> 里常有令牌与密码，
/// 因此它们<b>只进 Docker 请求</b>，<b>绝不</b>进日志。构建日志回带在响应里是给用户看的，
/// 那与「服务端日志」是两回事。</para>
/// </remarks>
public sealed class ImageBuildPushService
{
    /// <summary>
    /// 单次构建 / 推送保留的日志行数上限。
    /// </summary>
    /// <remarks>
    /// 一次 <c>apt-get update</c> 能刷出几千行，全塞进响应体会让浏览器卡住。
    /// 上限取一个「够看清失败原因」的值：构建失败的原因总在最后几十行。</remarks>
    private const int MaxLogLines = 500;

    /// <summary>默认 Registry（Docker Hub）。</summary>
    private const string DockerHubServerAddress = "registry-1.docker.io";

    private readonly IDockerHostAccessor _accessor;
    private readonly IRegistryCredentialResolver _registries;
    private readonly ILogger<ImageBuildPushService> _logger;

    /// <summary>
    /// 初始化用例服务。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="registries">Registry 凭据解析器。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ImageBuildPushService(
        IDockerHostAccessor accessor,
        IRegistryCredentialResolver registries,
        ILogger<ImageBuildPushService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _registries = registries;
        _logger = logger;
    }

    /// <summary>
    /// 从上传的构建上下文构建镜像。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">构建请求（上下文流、标签、构建参数等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>构建产物的镜像 ID、大小与构建日志。</returns>
    /// <exception cref="BusinessException">
    /// 字段非法（<c>1000</c>）、构建失败（<c>1806</c>，消息里带守护进程给的原因）、
    /// 主机不可达（<c>1301</c>）或超时（<c>1403</c>）。
    /// </exception>
    /// <remarks>
    /// <b>构建过程不可取消到一半</b>：<paramref name="cancellationToken"/> 来自 HTTP 请求，
    /// 用户关掉页面时它会触发。此时构建会被中断，守护进程那边的临时层留在中间态——
    /// 但这是可接受的（<c>docker build</c> 被 Ctrl-C 也是这个结果），
    /// 而强行忽略取消会让请求线程一直占着。</remarks>
    public async Task<ImageBuildResult> BuildAsync(
        string hostId,
        BuildImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Context is null)
        {
            throw BusinessException.Validation("构建上下文不能为空（请上传 .tar 包）");
        }

        var (reference, tag) = SplitReference(ImageService.RequireImageId(request.Tag));
        var dockerfile = string.IsNullOrWhiteSpace(request.Dockerfile)
            ? "Dockerfile"
            : request.Dockerfile.Trim();

        ValidateDictionary(request.BuildArgs, "buildArgs");
        ValidateDictionary(request.Labels, "labels");

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var parameters = new ImageBuildParameters
        {
            // ⚠️ Tags 要的是**完整的**「仓库:标签」引用，不是光仓库名。
            // 只给仓库名时守护进程会打上 <none> 标签，于是构建「成功」了，
            // 随后的 inspect 按「仓库:标签」去查却查不到（1401）。
            Tags = [$"{reference}:{tag}"],
            Dockerfile = dockerfile,
            NoCache = request.NoCache ?? false,
            Remove = true,
            BuildArgs = request.BuildArgs is { Count: > 0 } buildArgs
                ? new Dictionary<string, string>(buildArgs)
                : null,
            Labels = request.Labels is { Count: > 0 } labels
                ? new Dictionary<string, string>(labels)
                : null,
            Target = string.IsNullOrWhiteSpace(request.Target) ? null : request.Target.Trim(),
            Platform = string.IsNullOrWhiteSpace(request.Platform) ? null : request.Platform.Trim(),
        };

        var collector = new StreamCollector(MaxLogLines);

        _logger.LogInformation(
            "主机 {HostId} 开始构建镜像 {Reference}（Dockerfile={Dockerfile}，构建参数 {BuildArgCount} 个，标签 {LabelCount} 个，不用缓存={NoCache}）",
            hostId,
            $"{reference}:{tag}",
            dockerfile,
            request.BuildArgs?.Count ?? 0,
            request.Labels?.Count ?? 0,
            parameters.NoCache);

        // 认证参数传 null：本版构建不登录私有仓库（基础镜像只能来自公开仓库）。
        // 与 images/pull 同一口径——传 new AuthConfig() 会被当成「空用户名 + 空密码」
        // 去认证公开仓库并失败。
        try
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Images.BuildImageFromDockerfileAsync(
                    parameters,
                    request.Context,
                    authConfigs: null,
                    headers: null,
                    collector,
                    cancellationToken),
                $"构建镜像 {reference}:{tag}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessException exception) when (exception.Code == ErrorCodes.DockerApiFailed)
        {
            // 守护进程在**请求阶段**就失败时（Dockerfile 不存在、语法错到无法解析、
            // 基础镜像拉不动），SDK 会直接抛异常而不是把错误放进进度流。
            // 翻译器把它归成 1402（Docker API 调用失败），但对用户而言
            // 「构建失败」比「Docker API 调用失败」有用得多——后者会让人怀疑后端挂了。
            throw new BusinessException(
                ErrorCodes.ImageBuildFailed,
                $"构建镜像 {reference}:{tag} 失败：{exception.Message}",
                exception);
        }

        // 必须查进度消息：构建失败时 HTTP 层仍可能是 200，错误只在流里。
        // 不检查这一条，「Dockerfile 语法错」会被报成「构建成功但什么也没产出」。
        collector.ThrowIfFailed(ErrorCodes.ImageBuildFailed, $"构建镜像 {reference}:{tag}");

        var fullReference = $"{reference}:{tag}";
        var inspected = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.InspectImageAsync(fullReference, cancellationToken),
            $"镜像 {fullReference}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 构建完成：{Reference}（{Size} 字节，{LineCount} 行日志）",
            hostId,
            fullReference,
            inspected.Size,
            collector.Count);

        return new ImageBuildResult(
            inspected.ID ?? string.Empty,
            fullReference,
            inspected.Size,
            ImageService.MapReferences(inspected.RepoTags),
            collector.Lines);
    }

    /// <summary>
    /// 把本地镜像推送到 Registry。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">推送请求（镜像标识、可选 Registry 配置主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>推送结果（镜像 ID、目标地址、是否带凭据、推送日志）。</returns>
    /// <exception cref="BusinessException">
    /// 镜像标识为空（<c>1000</c>）、镜像无标签（<c>1000</c>）、
    /// Registry 配置不存在（<c>1800</c>）、凭据被拒（<c>1804</c>）、
    /// 推送失败（<c>1807</c>，消息里带守护进程给的原因）或主机不可达（<c>1301</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>为什么必须带标签</b>：<c>docker push</c> 推的是「仓库:标签」，
    /// 而悬挂镜像（<c>&lt;none&gt;</lt;none&gt;</c>）没有可推的名字。
    /// 这里提前报错，而不是让守护进程回一句看不懂的 <c>refusing to push unqualified name</c>。</para>
    /// <para><b>Registry 凭据怎么选</b>：显式给了主键就用它（配置不存在报 <c>1800</c>）；
    /// 否则从镜像引用里切出地址段（如 <c>harbor.local:5000/app:v1</c> → <c>harbor.local:5000</c>）
    /// 自动匹配。两者都找不到就<b>匿名推</b>——公开仓库本来就不需要凭据，
    /// 而对着一个私有仓库匿名推，报错会由守护进程给出（<c>1807</c>）。</para>
    /// </remarks>
    public async Task<ImagePushResult> PushAsync(
        string hostId,
        PushImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var imageId = ImageService.RequireImageId(request.Image);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        // 先 inspect 拿到实际标签与仓库名：用户可能只给了镜像 ID，
        // 而推送必须用「仓库:标签」这个形态。
        var inspected = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.InspectImageAsync(imageId, cancellationToken),
            $"镜像 {imageId}",
            cancellationToken).ConfigureAwait(false);

        var tags = ImageService.MapReferences(inspected.RepoTags);

        if (tags.Count == 0)
        {
            throw BusinessException.Validation(
                $"镜像 {imageId} 没有标签，无法推送。请先为它打标签（如 harbor.local:5000/app:v1）");
        }

        // 多个标签时推第一个：让用户先去打标签、消掉歧义，比在这里随便挑一个更有价值。
        var reference = tags[0];
        var (repository, tag) = SplitReference(reference);
        var serverAddress = ExtractServerAddress(repository);

        var credential = await this.ResolveCredentialAsync(request.RegistryId, serverAddress, cancellationToken)
            .ConfigureAwait(false);

        var authConfig = credential is null
            ? null
            : new AuthConfig
            {
                Username = credential.Username,
                Password = credential.Password,
                ServerAddress = credential.ServerAddress,
            };

        var collector = new StreamCollector(MaxLogLines);

        _logger.LogInformation(
            "主机 {HostId} 推送镜像 {Reference} 到 {ServerAddress}（带凭据={Authenticated}）",
            hostId,
            reference,
            serverAddress,
            authConfig is not null);

        var parameters = new ImagePushParameters
        {
            Tag = tag,
            Platform = string.IsNullOrWhiteSpace(request.Platform) ? null : request.Platform.Trim(),
        };

        try
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Images.PushImageAsync(repository, parameters, authConfig, collector, cancellationToken),
                $"推送镜像 {reference}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessException exception) when (exception.Code == ErrorCodes.DockerApiFailed)
        {
            // 与构建同理：守护进程在请求阶段就拒绝（仓库不存在、凭据不对）时，
            // SDK 抛异常而不是把错误放进进度流。归成「推送失败」比「Docker API 调用失败」
            // 对用户有用得多。
            throw new BusinessException(
                ErrorCodes.ImagePushFailed,
                $"推送镜像 {reference} 到 {serverAddress} 失败：{exception.Message}",
                exception);
        }

        collector.ThrowIfFailed(ErrorCodes.ImagePushFailed, $"推送镜像 {reference} 到 {serverAddress}");

        _logger.LogInformation(
            "主机 {HostId} 推送完成：{Reference} → {ServerAddress}（{LineCount} 行日志）",
            hostId,
            reference,
            serverAddress,
            collector.Count);

        return new ImagePushResult(
            inspected.ID ?? string.Empty,
            reference,
            serverAddress,
            authConfig is not null,
            collector.Lines);
    }

    /// <summary>
    /// 解析推送要用的凭据：显式主键优先，其次按地址自动匹配。
    /// </summary>
    /// <param name="registryId">显式指定的配置主键；为空则自动匹配。</param>
    /// <param name="serverAddress">从镜像引用里切出的地址段。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>凭据；没有可用凭据时返回 <see langword="null"/>（匿名推）。</returns>
    /// <exception cref="BusinessException">指定的主键对应的配置不存在（<c>1800</c>）。</exception>
    private async Task<RegistryCredentialValue?> ResolveCredentialAsync(
        Guid? registryId,
        string serverAddress,
        CancellationToken cancellationToken)
    {
        if (registryId is { } id)
        {
            if (id == Guid.Empty)
            {
                throw BusinessException.Validation("registryId 不能是全零 GUID");
            }

            // 显式指定时地址可以不一致（例如本地标签不带仓库前缀，但要推到内网 Harbor），
            // 因此这里把主键当成唯一的真相来源。
            var resolved = await _registries.ResolveCredentialAsync(serverAddress, cancellationToken)
                .ConfigureAwait(false);

            if (resolved is not null)
            {
                return resolved;
            }

            // 自动匹配没命中：把主键转成「必须存在」的一次显式查找。
            throw new BusinessException(
                ErrorCodes.RegistryNotFound,
                $"未找到 Registry 配置 {id}，或该地址（{serverAddress}）尚未登记凭据");
        }

        return await _registries.ResolveCredentialAsync(serverAddress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把「仓库名:标签」拆成两段；没有标签时补 <c>latest</c>。
    /// </summary>
    /// <param name="reference">镜像引用。</param>
    /// <returns>（仓库名，标签）。</returns>
    /// <remarks>
    /// 转发给 <see cref="ImageReference.Split"/>：判据必须与 Docker 一致，
    /// 而那个类已经有单测钉住。**不</b>在这里重写一份——
    /// 两处各写一份必然漂移，而漂移的后果是「推到一个不存在的仓库」。
    /// </remarks>
    internal static (string Repository, string Tag) SplitReference(string reference) =>
        ImageReference.Split(reference);

    /// <summary>
    /// 从仓库名里切出 Registry 地址段。
    /// </summary>
    /// <param name="repository">仓库名（如 <c>nginx</c>、<c>harbor.local:5000/app</c>）。</param>
    /// <returns>地址段；<c>nginx</c> 这类无前缀的仓库返回 Docker Hub。</returns>
    /// <remarks>
    /// 转发给 <see cref="ImageReference.ExtractServerAddress"/>，理由同
    /// <see cref="SplitReference"/>。
    /// </remarks>
    internal static string ExtractServerAddress(string repository) =>
        ImageReference.ExtractServerAddress(repository);

    /// <summary>
    /// 校验字典型输入（构建参数 / 标签）。
    /// </summary>
    private static void ValidateDictionary(IReadOnlyDictionary<string, string>? values, string field)
    {
        if (values is null)
        {
            return;
        }

        foreach (var pair in values)
        {
            if (String.IsNullOrWhiteSpace(pair.Key))
            {
                throw BusinessException.Validation($"{field} 含空的键");
            }

            if (pair.Value is null)
            {
                throw BusinessException.Validation($"{field}[{pair.Key}] 的值不能为 null");
            }
        }
    }

    /// <summary>
    /// 同步收集构建 / 推送进度消息的 <see cref="IProgress{T}"/> 实现。
    /// </summary>
    /// <remarks>
    /// <para><b>同步而非 <c>System.Progress&lt;T&gt;</c></b>：后者把回调 Post 到线程池，
    /// 于是 SDK 调用返回时队列里可能还有没执行的回调——错误消息会被漏掉，
    /// 而错误检查恰恰在 await 之后。同步实现没有这个时间窗。</para>
    /// <para><b>日志上限</b>：只保留<b>最后</b> <c>maxLines</c> 行——
    /// 构建失败的原因总在末尾，而开头几百行 <c>Step 1/N</c> 对排错毫无价值。</para>
    /// </remarks>
    private sealed class StreamCollector : IProgress<JSONMessage>
    {
        private readonly int _maxLines;
        private readonly List<string> _lines;
        private string? _failure;

        /// <summary>
        /// 初始化收集器。
        /// </summary>
        /// <param name="maxLines">保留的日志行数上限。</param>
        public StreamCollector(int maxLines)
        {
            _maxLines = maxLines;
            _lines = new List<string>(Math.Min(maxLines, 64));
        }

        /// <summary>已收集的日志行（最多 <c>maxLines</c> 行，失败时含原因行）。</summary>
        public IReadOnlyList<string> Lines => _lines;

        /// <summary>已收集的日志行数。</summary>
        public int Count => _lines.Count;

        /// <inheritdoc />
        public void Report(JSONMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.Error?.Message is { Length: > 0 } error)
            {
                this.RecordFailure(error);
                return;
            }

            var status = message.Status ?? string.Empty;

            if (status.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                this.RecordFailure(status);
                return;
            }

            this.Append(status);
        }

        /// <summary>
        /// 记录了错误时抛出业务异常。
        /// </summary>
        /// <param name="errorCode">错误码（构建用 <c>1806</c>，推送用 <c>1807</c>）。</param>
        /// <param name="description">错误描述（含镜像引用）。</param>
        /// <exception cref="BusinessException">记录过错误时抛出。</exception>
        public void ThrowIfFailed(int errorCode, string description)
        {
            if (_failure is not null)
            {
                throw new BusinessException(errorCode, $"{description} 失败：{_failure}");
            }
        }

        /// <summary>
        /// 追加一行日志（超上限时丢最旧的）。
        /// </summary>
        private void Append(string line)
        {
            if (String.IsNullOrWhiteSpace(line))
            {
                return;
            }

            if (_lines.Count >= _maxLines)
            {
                _lines.RemoveAt(0);
            }

            _lines.Add(line);
        }

        /// <summary>
        /// 记录第一条错误（后续错误不再覆盖——第一条通常最接近根因）。
        /// </summary>
        private void RecordFailure(string reason)
        {
            _failure ??= reason;

            this.Append($"ERROR: {reason}");
        }
    }
}
