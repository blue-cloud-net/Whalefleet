using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;

namespace Whalefleet.Application.Files;

/// <summary>
/// 镜像的导入 / 导出用例（0.14.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个类而不是并进 <c>ImageService</c></b>：导入导出是
/// 「<b>文件流</b>型」用例——输入与输出都是 GB 级的字节流，
/// 与 <c>ImageService</c> 的「请求—响应」型用例（一次调用返回一份小结果）形态完全不同。
/// 混在一起会让同一个类同时有两种截然不同的生命周期语义。</para>
/// <para><b>为什么导出按「引用」而不是「ID」</b>：<c>docker save</c> 按 ID 导出的 tar
/// 里 <c>manifest.json</c> 的 <c>RepoTags</c> 是空的——load 回来之后镜像就没有名字了
/// （实测：按引用 save 时 <c>RepoTags</c> 完整保留）。
/// 因此这里先 inspect 拿到 <c>RepoTags</c>，有标签就用标签（一次 save 带上全部标签），
/// 没有标签（悬空镜像）才退回 ID。</para>
/// <para><b>日志纪律</b>：只记主机标识与镜像引用，<b>不</b>记镜像的 <c>ENV</c> / <c>LABEL</c>。</para>
/// </remarks>
public sealed class ImageTransferService
{
    private readonly IDockerHostAccessor _accessor;
    private readonly ExportArtifactStore _artifacts;
    private readonly ILogger<ImageTransferService> _logger;

    /// <summary>
    /// 初始化镜像传输用例。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="artifacts">导出产物存储。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ImageTransferService(
        IDockerHostAccessor accessor,
        ExportArtifactStore artifacts,
        ILogger<ImageTransferService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _artifacts = artifacts;
        _logger = logger;
    }

    /// <summary>
    /// 导出镜像为 tar（等价 <c>docker save</c>）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="imageId">镜像标识（完整 ID、short ID、仓库名或 <c>仓库:标签</c>）。</param>
    /// <param name="progress">进度回报；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出产物（文件路径与大小）。</returns>
    /// <exception cref="BusinessException">
    /// 镜像标识为空（<c>1000</c>）、镜像不存在（<c>1401</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<TransferArtifact> ExportAsync(
        string hostId,
        string imageId,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var image = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.InspectImageAsync(imageId, cancellationToken),
            $"镜像 {imageId}",
            cancellationToken).ConfigureAwait(false);

        // 有标签就按标签导出：docker save 一次带上全部 RepoTags，load 回来名字还在。
        // 没有标签（悬空镜像）只能退回 ID —— 此时 load 回来是无名的，这是事实而不是缺陷。
        var names = ResolveSaveNames(image);

        progress?.Report(new TransferProgress(null, "正在导出镜像"));

        var stream = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.SaveImagesAsync(names, cancellationToken),
            $"镜像 {imageId}",
            cancellationToken).ConfigureAwait(false);

        await using (stream)
        {
            var fileName = BuildFileName(image);
            var path = _artifacts.AllocatePath(Guid.NewGuid().ToString("N"));

            var size = await WriteToFileAsync(stream, path, image.Size, progress, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "主机 {HostId} 导出镜像 {ImageId}（引用 {References}）→ {Path}（{Size} 字节）",
                hostId,
                image.ID ?? imageId,
                String.Join(", ", names),
                path,
                size);

            return new TransferArtifact(path, fileName, size);
        }
    }

    /// <summary>
    /// 导入镜像 tar（等价 <c>docker load</c>）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="tarStream">tar 流（可<b>不</b>可 seek——实测守护进程接受 chunked 传输）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入结果（镜像 ID 与提示）。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>失败藏在流里</b>：<c>docker load</c> 的 HTTP 响应是 <b>200</b>，
    /// 错误只出现在 JSON 消息流里（<c>error</c> 字段）。
    /// 只等调用返回的话，「镜像格式不对」会被报成「成功但什么也没导入」——
    /// 那比报错难查得多。因此这里必须逐条读消息流。</para>
    /// <para><b>不需要可 seek 的流</b>：2026-11-13 实测，
    /// <c>LoadImageAsync</c> 接受<b>不可 seek</b> 的流（走 chunked 传输），
    /// 因此 20 GB 的上传可以<b>纯流式直传</b>，不必先落临时文件。</para>
    /// </remarks>
    public async Task<ImageImportResult> ImportAsync(
        string hostId,
        Stream tarStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(tarStream);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);
        var messages = new List<JSONMessage>();

        var collector = new ImageLoadCollector(messages);

        try
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Images.LoadImageAsync(
                    new ImageLoadParameters { Quiet = true },
                    tarStream,
                    collector,
                    cancellationToken),
                "镜像导入",
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessException exception) when (LimitedReadStream.IsUploadTooLarge(exception))
        {
            // 上传超限的原始异常被 HttpClient 包成了「主机不可达」——
            // 这里把它还原回去，否则用户看到的是「连不上 Docker」。
            throw new BusinessException(
                ErrorCodes.UploadTooLarge,
                exception.InnerException is BusinessException inner
                    ? inner.Message
                    : "上传体积超过上限",
                exception);
        }

        // 流里的 error 才是真正的失败信号
        JSONMessage? failure = null;

        lock (messages)
        {
            failure = messages.FirstOrDefault(message => message.Error is not null);
        }

        if (failure is not null)
        {
            throw new BusinessException(
                ErrorCodes.DockerApiFailed,
                $"镜像导入失败：{failure.Error!.Message}");
        }

        string[] loaded;

        lock (messages)
        {
            loaded = messages
                .Where(message => message.Status is not null && message.ID is not null)
                .Select(message => message.Status!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        _logger.LogInformation(
            "主机 {HostId} 导入镜像完成：{Images}",
            hostId,
            loaded.Length == 0 ? "（未返回镜像标识）" : String.Join(", ", loaded));

        return new ImageImportResult(loaded, messages.Count);
    }

    /// <summary>
    /// 解析导出时使用的镜像引用列表。
    /// </summary>
    /// <param name="image">守护进程返回的镜像信息。</param>
    /// <returns>引用列表。</returns>
    private static string[] ResolveSaveNames(ImageInspectResponse image)
    {
        var tags = image.RepoTags?
            .Where(tag => !String.IsNullOrWhiteSpace(tag) && tag != "<none>")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tags is { Length: > 0 })
        {
            return tags;
        }

        // 悬空镜像：只能按 ID 导出，load 回来将没有标签。
        return [image.ID ?? "<none>"];
    }

    /// <summary>
    /// 构造导出文件名。
    /// </summary>
    /// <param name="image">守护进程返回的镜像信息。</param>
    /// <returns>文件名。</returns>
    private static string BuildFileName(ImageInspectResponse image)
    {
        var first = image.RepoTags?
            .FirstOrDefault(tag => !String.IsNullOrWhiteSpace(tag) && tag != "<none>");

        if (first is not null)
        {
            var slash = first.LastIndexOf('/');
            var name = slash >= 0 ? first[(slash + 1)..] : first;
            var colon = name.LastIndexOf(':');

            if (colon > 0)
            {
                name = name[..colon];
            }

            return ExportArtifactStore.SanitizeFileName(name) + ".tar";
        }

        var id = image.ID ?? "image";
        var shortId = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;

        if (shortId.Length > 12)
        {
            shortId = shortId[..12];
        }

        return ExportArtifactStore.SanitizeFileName(shortId) + ".tar";
    }

    /// <summary>
    /// 把流写入文件并回报进度。
    /// </summary>
    /// <param name="source">源流。</param>
    /// <param name="path">目标文件路径。</param>
    /// <param name="expectedBytes">预估总大小（未知时传 0）。</param>
    /// <param name="progress">进度回报。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的字节数。</returns>
    private static async Task<long> WriteToFileAsync(
        Stream source,
        string path,
        long expectedBytes,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        var lastReport = DateTimeOffset.MinValue;

        await using var target = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous);

        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;

            // 进度回报按「时间」节流而不是「每块」：GB 级导出会有上百万次读，
            // 每块都回报一次会把进度通道本身变成瓶颈。
            var now = DateTimeOffset.UtcNow;

            if (progress is not null && now - lastReport >= TimeSpan.FromMilliseconds(500))
            {
                lastReport = now;

                double? percent = null;

                if (expectedBytes > 0)
                {
                    // 估算值：docker save 的输出与镜像体积同量级但不相等，
                    // 因此百分比只作参考，夹在 99 以内。
                    percent = Math.Min(99d, total * 100d / expectedBytes);
                }

                progress.Report(new TransferProgress(percent, $"已导出 {FormatBytes(total)}"));
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);

        return total;
    }

    /// <summary>
    /// 把字节数格式化成可读文本。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>可读文本。</returns>
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    /// <summary>
    /// 收集 <c>docker load</c> 的消息流。
    /// </summary>
    /// <remarks>
    /// <b>必须同步实现</b>：<see cref="Progress{T}"/> 会把回调扔到线程池，
    /// 那样消息的顺序会乱，而这里的错误消息必须能被「第一条即为失败」地读出来。
    /// </remarks>
    private sealed class ImageLoadCollector : IProgress<JSONMessage>
    {
        private readonly List<JSONMessage> _messages;

        /// <summary>
        /// 初始化收集器。
        /// </summary>
        /// <param name="messages">收集到的消息（由调用方持有）。</param>
        public ImageLoadCollector(List<JSONMessage> messages)
        {
            _messages = messages;
        }

        /// <inheritdoc />
        public void Report(JSONMessage value)
        {
            lock (_messages)
            {
                _messages.Add(value);
            }
        }
    }
}
