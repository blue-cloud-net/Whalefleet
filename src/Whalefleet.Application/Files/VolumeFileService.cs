using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Application.Files;

/// <summary>
/// 卷文件浏览用例（0.14.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么必须借道一个容器</b>：Docker <b>没有</b>「读卷内容」的接口——
/// 卷只是宿主机上的一个目录，而本平台跑在自己的容器里、看不到宿主机文件系统。
/// 唯一可行的路是把卷挂进一个容器，再用 <c>/containers/{id}/archive</c> 读写它。</para>
/// <para><b>为什么是「一次性」辅助容器</b>：这个容器只为这一次浏览存在，
/// 用完立刻强制删除。它与用户自己的容器完全无关——不重启、不改配置、不占端口。</para>
/// <para><b>辅助镜像可以不含任何可执行文件</b>：实测 <c>/archive</c> 在
/// <b>已创建但未启动</b>的容器上就能工作，因此辅助容器<b>永远不需要启动</b>，
/// 镜像里不必有 shell、不必有 busybox（连 GPL 合规问题都不存在）。</para>
/// <para><b>只读 / 读写两种挂载</b>：浏览与下载用 <c>:ro</c>，写入用 rw。
/// 这是权限最小化——「看一眼」不该顺手拿到写挂载。</para>
/// </remarks>
public sealed class VolumeFileService
{
    /// <summary>
    /// 辅助容器的角色标签（用于识别与清理孤儿）。
    /// </summary>
    public const string RoleLabel = "whalefleet.role";

    /// <summary>
    /// 辅助容器的角色标签值。
    /// </summary>
    public const string RoleValue = "file-browser";

    /// <summary>
    /// 卷在辅助容器里的挂载点。
    /// </summary>
    public const string MountPath = "/whalefleet-volume";

    private readonly IDockerHostAccessor _accessor;
    private readonly IPermissionChecker _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly ContainerFileService _files;
    private readonly FilesOptions _options;
    private readonly ILogger<VolumeFileService> _logger;

    /// <summary>
    /// 初始化卷文件浏览用例。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="permissions">权限判定。</param>
    /// <param name="currentUser">当前登录用户。</param>
    /// <param name="files">文件系统操作（复用容器的 archive 读写）。</param>
    /// <param name="options">文件传输配置。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public VolumeFileService(
        IDockerHostAccessor accessor,
        IPermissionChecker permissions,
        ICurrentUser currentUser,
        ContainerFileService files,
        IOptions<FilesOptions> options,
        ILogger<VolumeFileService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _permissions = permissions;
        _currentUser = currentUser;
        _files = files;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 列举卷内的一个目录。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="volumeName">卷名。</param>
    /// <param name="path">目录路径（相对卷根）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>目录清单（路径已换算成容器内的挂载路径）。</returns>
    /// <exception cref="BusinessException">
    /// 路径非法（<c>1405</c>）、无权限（<c>1502</c>）、卷不存在（<c>1401</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<DirectoryListing> ListAsync(
        string hostId,
        string volumeName,
        string? path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        var normalized = ArchivePathGuard.Normalize(path);

        await this.EnsurePermissionAsync(hostId, Permission.VolumeRead, "浏览卷内文件", cancellationToken)
            .ConfigureAwait(false);

        return await this.WithHelperAsync(
            hostId,
            volumeName,
            readOnly: true,
            async (client, helperId) =>
            {
                var listing = await _files
                    .ListAsync(hostId, helperId, ToContainerPath(normalized), cancellationToken)
                    .ConfigureAwait(false);

                // 把挂载点前缀换成卷根的相对路径，界面不必知道辅助容器的存在。
                return listing with
                {
                    Path = normalized,
                    ParentPath = ArchivePathGuard.GetParent(normalized),
                    Entries =
                    [
                        .. listing.Entries.Select(entry => entry with
                        {
                            Path = FromContainerPath(entry.Path),
                        }),
                    ],
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开卷内一个文件的读取流。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="volumeName">卷名。</param>
    /// <param name="path">文件路径（相对卷根）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件流、文件名与大小。</returns>
    /// <remarks>
    /// <para><b>为什么要把整个文件读进内存再返回</b>：辅助容器必须在读完之前一直存在，
    /// 而「边读边删容器」做不到（删了容器文件系统就没了）。
    /// 因此这里把内容完整读出来、关掉容器，再交给调用方。</para>
    /// <para>代价是大文件会占内存——这是卷浏览用辅助容器的固有代价，
    /// 文档里要写明「超大文件建议在容器里直接取」。</para>
    /// </remarks>
    public async Task<(Stream Stream, string FileName, long Size)> OpenFileAsync(
        string hostId,
        string volumeName,
        string? path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        var normalized = ArchivePathGuard.Normalize(path);

        await this.EnsurePermissionAsync(hostId, Permission.VolumeRead, "下载卷内文件", cancellationToken)
            .ConfigureAwait(false);

        return await this.WithHelperAsync(
            hostId,
            volumeName,
            readOnly: true,
            async (client, helperId) =>
            {
                var (stream, fileName, size) = await _files
                    .OpenFileAsync(hostId, helperId, ToContainerPath(normalized), cancellationToken)
                    .ConfigureAwait(false);

                // ⚠️ 必须在这里读完：辅助容器马上会被删掉，届时流就失效了。
                using (stream)
                {
                    var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

                    // 必须倒回去：拷完之后位置在末尾，不退回来交给框架就是一个**空响应**
                    // （状态码 200、内容 0 字节——看起来像「文件是空的」，极难联想到是这里）。
                    buffer.Position = 0;

                    return ((Stream)buffer, fileName, size);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把内容写入卷内的一个文件。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="volumeName">卷名。</param>
    /// <param name="directoryPath">目标目录（相对卷根）。</param>
    /// <param name="fileName">文件名。</param>
    /// <param name="content">文件内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的字节数。</returns>
    /// <exception cref="BusinessException">
    /// 路径非法（<c>1405</c>）、写入已被禁用（<c>1409</c>）、
    /// 无权限（<c>1502</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<long> WriteFileAsync(
        string hostId,
        string volumeName,
        string? directoryPath,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeName);

        if (!_options.AllowWrite)
        {
            throw new BusinessException(
                ErrorCodes.FileWriteDisabled,
                "文件写入已被管理员禁用");
        }

        var normalized = ArchivePathGuard.Normalize(directoryPath);

        await this.EnsurePermissionAsync(hostId, Permission.VolumeWrite, "写入卷内文件", cancellationToken)
            .ConfigureAwait(false);

        return await this.WithHelperAsync(
            hostId,
            volumeName,
            readOnly: false,
            async (client, helperId) => await _files
                .WriteFileAsync(hostId, helperId, ToContainerPath(normalized), fileName, content, cancellationToken)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 确保辅助镜像存在；不存在时拉取一次。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 镜像既不存在也拉不到（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>为什么只拉一次而不缓存「已确认」状态</b>：镜像可能在运行期被
    /// <c>docker image prune -a</c> 清掉。缓存一个布尔值会让之后的浏览
    /// 全部以「No such image」失败，而重新 inspect 只有一次本地调用。
    /// </remarks>
    private async Task EnsureHelperImageAsync(IDockerClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.Images
                .InspectImageAsync(_options.HelperImage, cancellationToken)
                .ConfigureAwait(false);

            return;
        }
        catch (DockerImageNotFoundException)
        {
            _logger.LogInformation("辅助镜像 {Image} 不存在，尝试拉取", _options.HelperImage);
        }

        try
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Images.CreateImageAsync(
                    new ImagesCreateParameters
                    {
                        FromImage = _options.HelperImage.Split(':')[0],
                        Tag = _options.HelperImage.Contains(':') ? _options.HelperImage.Split(':')[1] : "latest",
                    },
                    null,
                    new Progress<JSONMessage>(),
                    cancellationToken),
                $"辅助镜像 {_options.HelperImage}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessException exception)
        {
            throw new BusinessException(
                ErrorCodes.DockerApiFailed,
                $"卷文件浏览所需的辅助镜像「{_options.HelperImage}」不可用："
                + "请先在本机构建（make volfs-helper）或让它可被拉取。"
                + $"（{exception.Message}）");
        }
    }

    /// <summary>
    /// 建一个一次性辅助容器、执行操作、再强制删除它。
    /// </summary>
    /// <typeparam name="T">操作结果类型。</typeparam>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="volumeName">卷名。</param>
    /// <param name="readOnly">是否只读挂载。</param>
    /// <param name="action">在辅助容器上执行的操作。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果。</returns>
    /// <exception cref="BusinessException">
    /// 卷不存在（<c>1401</c>）、辅助镜像不可用（<c>1402</c>）或操作本身失败。
    /// </exception>
    /// <remarks>
    /// <para><b>删除放在 <c>finally</c></b>：操作失败时辅助容器也必须被清掉，
    /// 否则每失败一次就在宿主机上留下一个垃圾容器。</para>
    /// <para><b>容器的 <c>Cmd</c> 必须给</b>：辅助镜像<b>没有</b> CMD，
    /// 而守护进程对「无命令可执行」的镜像拒绝创建容器
    /// （<c>no command specified</c>）。给一个不存在的路径即可——
    /// 容器永远不会被启动，这个命令只会被记录下来。</para>
    /// <para><b>镜像缺失时按需拉取一次</b>：辅助镜像不在 compose 里启动（它是用完即删的），
    /// 因此「本机没构建过」是<b>常态</b>而不是异常。这里先 inspect，缺失就 pull 一次；
    /// 拉不到才报错，并在提示里给出两条出路（本机构建 / 联网拉取）。
    /// 不这么做的话，用户看到的会是守护进程那句
    /// <c>No such image</c>——完全看不出和应用有什么关系。</para>
    /// </remarks>
    private async Task<T> WithHelperAsync<T>(
        string hostId,
        string volumeName,
        bool readOnly,
        Func<IDockerClient, string, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        // 先确认卷存在：不存在时创建容器会报「卷不存在」，但那条错误里混着镜像问题，
        // 分开判能把「卷名打错了」和「辅助镜像没构建」区分开。
        await DockerErrorTranslator.ExecuteAsync(
            () => client.Volumes.InspectAsync(volumeName, cancellationToken),
            $"卷 {volumeName}",
            cancellationToken).ConfigureAwait(false);

        await EnsureHelperImageAsync(client, cancellationToken).ConfigureAwait(false);

        var created = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    Image = _options.HelperImage,
                    Cmd = ["/whalefleet-file-browser"],
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [RoleLabel] = RoleValue,
                    },
                    HostConfig = new HostConfig
                    {
                        Mounts =
                        [
                            new Mount
                            {
                                Type = "volume",
                                Source = volumeName,
                                Target = MountPath,
                                ReadOnly = readOnly,
                            },
                        ],
                    },
                },
                cancellationToken),
            $"卷 {volumeName} 的辅助容器",
            cancellationToken).ConfigureAwait(false);

        try
        {
            _logger.LogDebug(
                "卷 {Volume} 使用辅助容器 {ContainerId}（只读={ReadOnly}）",
                volumeName,
                created.ID,
                readOnly);

            return await action(client, created.ID).ConfigureAwait(false);
        }
        finally
        {
            // 不用 await 等待结果没意义——但必须真的删掉。
            // 这里用 None：即便请求被取消，清理也要做完，否则留下孤儿容器。
            try
            {
                await client.Containers.RemoveContainerAsync(
                    created.ID,
                    new ContainerRemoveParameters { Force = true },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "删除卷 {Volume} 的辅助容器 {ContainerId} 失败",
                    volumeName,
                    created.ID);
            }
        }
    }

    /// <summary>
    /// 校验当前用户在该主机上的某项权限。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="permission">权限名。</param>
    /// <param name="action">动作描述（用于错误消息）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">
    /// 无权限（<c>1502</c>）。
    /// </exception>
    private async Task EnsurePermissionAsync(
        string hostId,
        string permission,
        string action,
        CancellationToken cancellationToken)
    {
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);
        var userId = _currentUser.UserId;

        if (userId is null ||
            !await _permissions
                .HasPermissionAsync(userId.Value, host.Id, permission, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new BusinessException(
                ErrorCodes.OperationForbidden,
                $"{action}需要 {permission} 权限");
        }
    }

    /// <summary>
    /// 把卷内的相对路径换算成辅助容器里的绝对路径。
    /// </summary>
    /// <param name="volumePath">卷内路径（已规范化）。</param>
    /// <returns>容器内路径。</returns>
    private static string ToContainerPath(string volumePath) =>
        ArchivePathGuard.IsRoot(volumePath) ? MountPath : MountPath + volumePath;

    /// <summary>
    /// 把辅助容器里的绝对路径换算回卷内路径。
    /// </summary>
    /// <param name="containerPath">容器内路径。</param>
    /// <returns>卷内路径。</returns>
    private static string FromContainerPath(string containerPath)
    {
        if (containerPath.StartsWith(MountPath, StringComparison.Ordinal))
        {
            var remainder = containerPath[MountPath.Length..];

            return String.IsNullOrEmpty(remainder) ? "/" : remainder;
        }

        return containerPath;
    }
}
