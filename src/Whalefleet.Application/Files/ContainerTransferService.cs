using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.Docker;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Application.Files;

/// <summary>
/// 容器的导入 / 导出 / 提交用例（0.14.0）。
/// </summary>
/// <remarks>
/// <para><b>容器侧是相对 Portainer 的差异化能力</b>：Portainer 只提供镜像的导入 / 导出，
/// 容器的导出 / 导入 / 提交都没有。</para>
/// <para><b>导出（<c>docker export</c>）不含挂载卷的内容</b>——实测：
/// 卷在导出产物里是个空目录。这是 Docker 的语义（export 导出的是容器的
/// 可写层与挂载点的骨架），不是缺陷。界面必须写明，否则用户会以为「备份全了」。</para>
/// <para><b>导入（<c>docker import</c>）只导入文件系统</b>：它不会带回 CMD / ENTRYPOINT /
/// ENV / 端口映射 / 挂载——那些是「运行参数」而不是「文件内容」。
/// 想要一个能直接跑的镜像请用 commit（它保留原容器的配置）。</para>
/// </remarks>
public sealed class ContainerTransferService
{
    private readonly IDockerHostAccessor _accessor;
    private readonly ContainerAuthorizationService _authorization;
    private readonly ExportArtifactStore _artifacts;
    private readonly IPermissionChecker _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ContainerTransferService> _logger;

    /// <summary>
    /// 初始化容器传输用例。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="authorization">容器授权判定。</param>
    /// <param name="artifacts">导出产物存储。</param>
    /// <param name="permissions">权限判定。</param>
    /// <param name="currentUser">当前登录用户。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerTransferService(
        IDockerHostAccessor accessor,
        ContainerAuthorizationService authorization,
        ExportArtifactStore artifacts,
        IPermissionChecker permissions,
        ICurrentUser currentUser,
        ILogger<ContainerTransferService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _authorization = authorization;
        _artifacts = artifacts;
        _permissions = permissions;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// 导出容器文件系统为 tar（等价 <c>docker export</c>）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="progress">进度回报；可为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出产物（文件路径与大小）。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识为空（<c>1000</c>）、容器不存在（<c>1401</c>）、
    /// 无权限（<c>1502</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>本方法不做授权判定</b>——调用方必须在<b>请求期</b>用它自己的凭证把
    /// 「这个人能不能读这个容器」判定完，再把<b>规范 ID</b> 传进来。</para>
    /// <para><b>为什么不能在后台线程里判</b>：导出跑在后台工作器的自有作用域里，
    /// 那里<b>没有任何登录用户</b>（<c>ICurrentUser.UserId</c> 为 null）。
    /// 在那里调 <c>ContainerAuthorizationService.EnsureAsync</c> 会一律被判为无权限，
    /// 症状是「一发起导出就失败」（而权限其实完全够）。</para>
    /// </remarks>
    public async Task<TransferArtifact> ExportAsync(
        string hostId,
        string containerId,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        progress?.Report(new TransferProgress(null, "正在导出容器"));

        var stream = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.ExportContainerAsync(containerId, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        await using (stream)
        {
            var fileName = ExportArtifactStore.SanitizeFileName(containerId) + ".tar";
            var path = _artifacts.AllocatePath(Guid.NewGuid().ToString("N"));

            var size = await WriteToFileAsync(stream, path, progress, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "主机 {HostId} 导出容器 {ContainerId} → {Path}（{Size} 字节）",
                hostId,
                containerId,
                path,
                size);

            return new TransferArtifact(path, fileName, size);
        }
    }

    /// <summary>
    /// 把 tar 导入为镜像（等价 <c>docker import</c>）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="tarStream">tar 流（可不可 seek 均可）。</param>
    /// <param name="repository">镜像仓库名（可空）。</param>
    /// <param name="tag">镜像标签（可空）。</param>
    /// <param name="message">提交说明（可空）。</param>
    /// <param name="changes">额外的 Dockerfile 指令（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导入结果。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>只导入文件系统</b>：tar 里没有 CMD / ENTRYPOINT / ENV / 端口 / 挂载，
    /// 因此导入出来的镜像<b>不能直接按原样运行</b>——这一点必须由界面讲清楚。
    /// 想保留运行参数请用 <c>commit</c>。
    /// </remarks>
    public async Task<ContainerImportResult> ImportAsync(
        string hostId,
        Stream tarStream,
        string? repository,
        string? tag,
        string? message,
        IReadOnlyList<string>? changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(tarStream);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var parameters = new ImagesCreateParameters
        {
            // "-" 表示「从请求体导入 tar」（Docker API 的约定）
            FromSrc = "-",
            Repo = repository,
            Tag = tag,
            Message = message,
            Changes = changes is { Count: > 0 } ? [.. changes] : null,
        };

        var messages = new List<JSONMessage>();
        var collector = new SyncMessageCollector(messages);

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.CreateImageAsync(parameters, tarStream, null, collector, cancellationToken),
            "容器导入",
            cancellationToken).ConfigureAwait(false);

        JSONMessage? failure = null;
        string[] loaded;

        lock (messages)
        {
            failure = messages.FirstOrDefault(item => item.Error is not null);
            loaded = messages
                .Where(item => item.Status is not null && item.ID is not null)
                .Select(item => item.Status!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        if (failure is not null)
        {
            throw new BusinessException(
                ErrorCodes.DockerApiFailed,
                $"容器导入失败：{failure.Error!.Message}");
        }

        _logger.LogInformation(
            "主机 {HostId} 导入容器文件系统为镜像 {Images}",
            hostId,
            loaded.Length == 0 ? "（未返回镜像标识）" : String.Join(", ", loaded));

        return new ContainerImportResult(
            loaded.Length > 0 ? loaded[0] : String.Empty,
            repository,
            tag,
            []);
    }

    /// <summary>
    /// 把容器当前状态提交为镜像（等价 <c>docker commit</c>）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="repository">镜像仓库名。</param>
    /// <param name="tag">镜像标签。</param>
    /// <param name="comment">提交说明（可空）。</param>
    /// <param name="author">作者（可空）。</param>
    /// <param name="pause">提交前是否暂停容器。</param>
    /// <param name="changes">额外的 Dockerfile 指令（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交得到的镜像 ID。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识或镜像名非法（<c>1000</c>）、容器不存在（<c>1401</c>）、
    /// 无权限（<c>1502</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>它会固化临时改动</b>：容器里临时装的东西、临时改的配置都会被写进镜像。
    /// 因此界面上的确认必须说明这一点——这是不可逆的。</para>
    /// <para><b>它保留运行参数</b>：commit 出来的镜像带着原容器的 CMD / ENTRYPOINT / ENV，
    /// 因此可以直接运行；而 import 不行。</para>
    /// </remarks>
    public async Task<string> CommitAsync(
        string hostId,
        string containerId,
        string repository,
        string? tag,
        string? comment,
        string? author,
        bool pause,
        IReadOnlyList<string>? changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        // commit 既是「读容器」也是「写镜像」——两个权限都要。
        var resolved = await _authorization
            .EnsureAsync(hostId, containerId, Permission.ContainerWrite, cancellationToken)
            .ConfigureAwait(false);

        await this.EnsureImageWriteAsync(hostId, cancellationToken).ConfigureAwait(false);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var parameters = new CommitContainerChangesParameters
        {
            ContainerID = resolved,
            RepositoryName = repository,
            Tag = tag,
            Comment = comment,
            Author = author,
            Pause = pause,
            Changes = changes is { Count: > 0 } ? [.. changes] : null,
        };

        var response = await DockerErrorTranslator.ExecuteAsync(
            () => client.Images.CommitContainerChangesAsync(parameters, cancellationToken),
            $"容器 {resolved}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 提交容器 {ContainerId} 为镜像 {Repository}:{Tag}（{ImageId}）",
            hostId,
            resolved,
            repository,
            tag,
            response.ID);

        return response.ID ?? String.Empty;
    }

    /// <summary>
    /// 校验当前用户在该主机上有镜像写权限。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 无权限（<c>1502</c>）。
    /// </exception>
    /// <remarks>
    /// commit 会**新建一个镜像**，因此除了容器的写权限外还需要镜像的写权限。
    /// 只查容器权限会让「只能改容器」的用户凭空多出「造镜像」的能力。
    /// </remarks>
    private async Task EnsureImageWriteAsync(string hostId, CancellationToken cancellationToken)
    {
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);
        var userId = _currentUser.UserId;

        if (userId is null)
        {
            // 无登录用户：调用方本不该走到这里（端点有 [Authorize]），
            // 但这里不能「当作有权限」——fail-closed。
            throw new BusinessException(
                ErrorCodes.OperationForbidden,
                "提交容器为镜像需要镜像写权限");
        }

        if (!await _permissions
                .HasPermissionAsync(userId.Value, host.Id, Permission.ImageWrite, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.OperationForbidden,
                "提交容器为镜像需要镜像写权限");
        }
    }

    /// <summary>
    /// 把流写入文件并回报进度。
    /// </summary>
    /// <param name="source">源流。</param>
    /// <param name="path">目标文件路径。</param>
    /// <param name="progress">进度回报。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的字节数。</returns>
    /// <remarks>
    /// <b>为什么容器导出没有百分比</b>：<c>docker export</c> 不给总长度，
    /// 容器可写层的大小事先也不知道。给一个编造的百分比比给「不知道」更糟。
    /// </remarks>
    private static async Task<long> WriteToFileAsync(
        Stream source,
        string path,
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

            var now = DateTimeOffset.UtcNow;

            if (progress is not null && now - lastReport >= TimeSpan.FromMilliseconds(500))
            {
                lastReport = now;
                progress.Report(new TransferProgress(null, $"已导出 {FormatBytes(total)}"));
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
    /// 同步收集 Docker 的 JSON 消息流。
    /// </summary>
    /// <remarks>
    /// <b>必须同步实现</b>：<see cref="Progress{T}"/> 会把回调扔到线程池，
    /// 那样消息顺序会乱，而这里的错误消息必须能被稳定地读出来。
    /// </remarks>
    private sealed class SyncMessageCollector : IProgress<JSONMessage>
    {
        private readonly List<JSONMessage> _messages;

        /// <summary>
        /// 初始化收集器。
        /// </summary>
        /// <param name="messages">收集到的消息（由调用方持有）。</param>
        public SyncMessageCollector(List<JSONMessage> messages)
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
