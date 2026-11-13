using System.Formats.Tar;

using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.Docker;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Application.Files;

/// <summary>
/// 文件系统里的一个条目（目录 / 文件 / 符号链接）。
/// </summary>
/// <param name="Name">名称（不含路径）。</param>
/// <param name="Path">完整路径（以 <c>/</c> 开头）。</param>
/// <param name="Kind">类型：<c>directory</c> / <c>file</c> / <c>symlink</c> / <c>other</c>。</param>
/// <param name="SizeBytes">大小（字节）；目录为 0。</param>
/// <param name="ModifiedAt">修改时间（UTC）；未知为 <see langword="null"/>。</param>
/// <param name="LinkTarget">符号链接指向的目标；非链接为 <see langword="null"/>。</param>
/// <param name="Mode">权限位（八进制字符串，如 <c>644</c>）；未知为 <see langword="null"/>。</param>
public sealed record FileEntry(
    string Name,
    string Path,
    string Kind,
    long SizeBytes,
    DateTime? ModifiedAt,
    string? LinkTarget,
    string? Mode);

/// <summary>
/// 目录列举结果。
/// </summary>
/// <param name="Path">被列举的目录路径。</param>
/// <param name="ParentPath">父目录路径；根目录为 <see langword="null"/>。</param>
/// <param name="Entries">条目（目录优先，其余按名称排序）。</param>
/// <param name="Truncated">是否因为超过上限被截断。</param>
public sealed record DirectoryListing(
    string Path,
    string? ParentPath,
    IReadOnlyList<FileEntry> Entries,
    bool Truncated);

/// <summary>
/// 容器文件浏览用例（0.14.0）。
/// </summary>
/// <remarks>
/// <para><b>怎么列举目录</b>：Docker 没有「列目录」的接口。
/// <c>/containers/{id}/archive?path=...</c> 给的是<b>该路径的 tar 流</b>——
/// 对目录而言就是这个目录连同它的直接子项。因此列举 = <b>读一遍 tar 的条目头</b>
/// （不读内容），取第一层。实测确认 <c>statOnly</c> <b>不能</b>用来列目录
/// （它返回单个 stat，不是子项列表）。</para>
/// <para><b>为什么能对未启动的容器工作</b>：archive 端点操作的是容器的文件系统层，
/// 与进程是否在跑无关（实测对 created 与 paused 容器都可用）。</para>
/// <para><b>授权在请求期做</b>：本类的公开方法都在请求里被调用，因此可以（也必须）判权限。</para>
/// </remarks>
public sealed class ContainerFileService
{
    private readonly IDockerHostAccessor _accessor;
    private readonly ContainerAuthorizationService _authorization;
    private readonly IOptions<FilesOptions> _options;
    private readonly ILogger<ContainerFileService> _logger;

    /// <summary>
    /// 初始化容器文件浏览用例。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="authorization">容器授权判定。</param>
    /// <param name="options">文件传输配置。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerFileService(
        IDockerHostAccessor accessor,
        ContainerAuthorizationService authorization,
        IOptions<FilesOptions> options,
        ILogger<ContainerFileService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _authorization = authorization;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 列举容器内的一个目录。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="path">目录路径（可空 / 相对 / 带 <c>.</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>目录清单。</returns>
    /// <exception cref="BusinessException">
    /// 路径非法（<c>1405</c>）、路径不存在（<c>1406</c>）、容器不存在（<c>1401</c>）、
    /// 无权限（<c>1502</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<DirectoryListing> ListAsync(
        string hostId,
        string containerId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var normalized = ArchivePathGuard.Normalize(path);

        var canonical = await _authorization
            .EnsureAsync(hostId, containerId, Permission.ContainerRead, cancellationToken)
            .ConfigureAwait(false);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var entries = await ReadEntriesAsync(client, canonical, normalized, cancellationToken)
            .ConfigureAwait(false);

        var limit = _options.Value.MaxListEntries;

        // tar 里的首条通常是「这个目录本身」，跳过它；
        // 其余条目按第一层取（条目名是完整路径，取相对被列举目录的那一段）。
        var listing = new List<FileEntry>();

        foreach (var entry in entries)
        {
            var relative = GetRelativeName(entry.Name, normalized);

            if (relative is null || relative.Length == 0 || relative.Contains('/', StringComparison.Ordinal))
            {
                // 空串 = 目录自身；含 '/' = 更深层，不属于本次列举
                continue;
            }

            listing.Add(MapEntry(entry, normalized, relative));
        }

        // 目录优先、再按名称排序：与任何文件管理器一致，
        // 也避免「文件和目录混在一起」这种读起来费劲的顺序。
        var ordered = listing
            .OrderBy(item => item.Kind == "directory" ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var truncated = ordered.Length > limit;

        if (truncated)
        {
            ordered = [.. ordered.Take(limit)];
        }

        _logger.LogDebug(
            "主机 {HostId} 列举容器 {ContainerId} 的目录 {Path}：{Count} 项（截断={Truncated}）",
            hostId,
            canonical,
            normalized,
            ordered.Length,
            truncated);

        return new DirectoryListing(
            normalized,
            ArchivePathGuard.GetParent(normalized),
            ordered,
            truncated);
    }

    /// <summary>
    /// 打开一个文件的读取流（下载用）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="path">文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件的读取流（tar 条目已解包）与文件名。</returns>
    /// <exception cref="BusinessException">
    /// 路径非法（<c>1405</c>）、文件不存在或不是文件（<c>1406</c>）、
    /// 无权限（<c>1502</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>为什么不把整个 tar 交给浏览器</b>：用户点的是「下载这个文件」，
    /// 给他一个只含一个条目的 tar 会让人再解一次包。这里把条目数据流读出来直接转发。</para>
    /// <para><b>为什么用内存缓冲</b>：tar 是<b>顺序</b>格式，要拿到某个条目的数据，
    /// 必须先读完它前面的条目头。这里把整个响应读进内存再定位——
    /// 对「单个文件」这个场景是可控的（而目录下载不走这条路）。</para>
    /// </remarks>
    public async Task<(Stream Stream, string FileName, long Size)> OpenFileAsync(
        string hostId,
        string containerId,
        string? path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        var normalized = ArchivePathGuard.Normalize(path);

        if (ArchivePathGuard.IsRoot(normalized))
        {
            throw new BusinessException(
                ErrorCodes.FilePathNotFound,
                "根目录不是一个可下载的文件");
        }

        var canonical = await _authorization
            .EnsureAsync(hostId, containerId, Permission.ContainerRead, cancellationToken)
            .ConfigureAwait(false);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var response = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.GetArchiveFromContainerAsync(
                canonical,
                new ContainerPathStatParameters { Path = normalized },
                false,
                cancellationToken),
            $"容器 {canonical} 的路径 {normalized}",
            cancellationToken).ConfigureAwait(false);

        var archive = response.Stream;

        if (archive is null)
        {
            throw new BusinessException(
                ErrorCodes.FilePathNotFound,
                $"路径「{normalized}」不是一个可下载的文件");
        }

        // 单文件的 archive 只会有一个文件条目。
        //
        // ⚠️ 不要先把整份 tar 读进内存：容器里一个 10 GB 的文件会把进程直接撑爆。
        // 而 tar 是**顺序**格式，要拿到条目数据必须先读掉它前面的条目头——
        // 因此这里返回 DataStream（一个被限定在条目长度内的子流），
        // 并把 reader 与底层流一起交给调用方，由它在写完后释放。
        var reader = new TarReader(archive);

        try
        {
            TarEntry? entry;

            while ((entry = await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue;
                }

                if (entry.DataStream is null)
                {
                    break;
                }

                var name = ArchivePathGuard.GetName(normalized);

                return (new ArchiveEntryStream(reader, entry.DataStream), name, entry.Length);
            }
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            await archive.DisposeAsync().ConfigureAwait(false);

            throw;
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await archive.DisposeAsync().ConfigureAwait(false);

        throw new BusinessException(
            ErrorCodes.FilePathNotFound,
            $"路径「{normalized}」不是一个可下载的文件");
    }

    /// <summary>
    /// 把内容写入容器内的一个文件。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="directoryPath">目标目录路径。</param>
    /// <param name="fileName">文件名。</param>
    /// <param name="content">文件内容流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的字节数。</returns>
    /// <exception cref="BusinessException">
    /// 路径非法（<c>1405</c>）、写入已被禁用（<c>1409</c>）、
    /// 无权限（<c>1502</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>为什么由服务端打 tar</b>：Docker 的 archive 写入接口只收 tar。
    /// 让前端自己打包意味着浏览器里必须有一个 tar 实现（额外依赖 + 又一次内存拷贝），
    /// 而服务端已经引用了 <c>System.Formats.Tar</c>。</para>
    /// <para><b>为什么文件名要单独校验</b>：它来自用户输入，一个 <c>..</c>
    /// 就能把文件写到目标目录之外。<see cref="ArchivePathGuard.Combine"/> 会拒绝它。</para>
    /// </remarks>
    public async Task<long> WriteFileAsync(
        string hostId,
        string containerId,
        string? directoryPath,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        if (!_options.Value.AllowWrite)
        {
            throw new BusinessException(
                ErrorCodes.FileWriteDisabled,
                "文件写入已被管理员禁用");
        }

        var directory = ArchivePathGuard.Normalize(directoryPath);

        // 校验文件名（不含分隔符、不是 . / ..）
        _ = ArchivePathGuard.Combine(directory, fileName);

        var canonical = await _authorization
            .EnsureAsync(hostId, containerId, Permission.ContainerWrite, cancellationToken)
            .ConfigureAwait(false);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        using var tar = new MemoryStream();

        using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, leaveOpen: true))
        {
            await writer.WriteEntryAsync(
                new UstarTarEntry(TarEntryType.RegularFile, fileName) { DataStream = content },
                cancellationToken).ConfigureAwait(false);
        }

        tar.Position = 0;

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.ExtractArchiveToContainerAsync(
                canonical,
                new CopyToContainerParameters { Path = directory },
                tar,
                cancellationToken),
            $"容器 {canonical} 的目录 {directory}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 向容器 {ContainerId} 写入文件 {Directory}/{FileName}（{Size} 字节）",
            hostId,
            canonical,
            directory,
            fileName,
            content.Length);

        return content.Length;
    }

    /// <summary>
    /// 读取目录的 tar 条目头（不读条目内容）。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="path">目录路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>tar 条目列表。</returns>
    /// <exception cref="BusinessException">
    /// 路径不存在（<c>1406</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    private static async Task<List<TarEntrySnapshot>> ReadEntriesAsync(
        IDockerClient client,
        string containerId,
        string path,
        CancellationToken cancellationToken)
    {
        ContainerArchiveResponse response;

        try
        {
            response = await client.Containers.GetArchiveFromContainerAsync(
                containerId,
                new ContainerPathStatParameters { Path = path },
                false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not BusinessException)
        {
            // 守护进程对「路径不存在」回 404，而 SDK 会把它包成
            // DockerContainerNotFoundException —— 翻译成本项目的「文件不存在」，
            // 而不是笼统的「容器不存在」（两者对用户是完全不同的信息）。
            if (exception is DockerContainerNotFoundException or DockerApiException { StatusCode: System.Net.HttpStatusCode.NotFound })
            {
                throw new BusinessException(
                    ErrorCodes.FilePathNotFound,
                    $"路径「{path}」不存在",
                    exception);
            }

            throw DockerErrorTranslator.TranslateException(exception, $"容器 {containerId} 的目录 {path}");
        }

        var entries = new List<TarEntrySnapshot>();
        var archive = response.Stream;

        if (archive is null)
        {
            return [];
        }

        await using (archive)
        {
            using var reader = new TarReader(archive);

            TarEntry? entry;

            while ((entry = await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false)) is not null)
            {
                // ⚠️ 必须在 reader 存活期间把元数据取完：
                // reader 一旦释放，entry 上的部分属性就不再可靠（实测会抛 ObjectDisposedException，
                // 而那个异常会被兑成 9999/500——看起来完全不像「文件浏览坏了」）。
                entries.Add(new TarEntrySnapshot(
                    entry.Name,
                    entry.EntryType,
                    entry.Length,
                    entry.ModificationTime,
                    entry.LinkName,
                    entry.Mode));

                // 只关心第一层，但 tar 是顺序格式，必须把流读完才能安全释放；
                // 这里不提前 break —— 提前退出可能会让连接上的剩余数据变成「半截请求」。
            }
        }

        return entries;
    }

    /// <summary>
    /// tar 条目头的快照（脱离 <see cref="TarReader"/> 存活期后仍可用）。
    /// </summary>
    /// <param name="Name">条目名。</param>
    /// <param name="EntryType">条目类型。</param>
    /// <param name="Length">长度。</param>
    /// <param name="ModificationTime">修改时间。</param>
    /// <param name="LinkName">链接目标。</param>
    /// <param name="Mode">权限位。</param>
    private sealed record TarEntrySnapshot(
        string Name,
        TarEntryType EntryType,
        long Length,
        DateTimeOffset ModificationTime,
        string LinkName,
        UnixFileMode Mode);

    /// <summary>
    /// 包住 tar 条目数据的流：释放时一并释放 <see cref="TarReader"/> 与底层归档流。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要它</b>：<see cref="TarReader"/> 一旦被释放，它交给我们的
    /// <c>DataStream</c> 就读不动了；而底层归档流也必须在写完后关闭，
    /// 否则那条 HTTP 连接会一直挂着。把三者捆成一个流，调用方只需 <c>await using</c>。
    /// </remarks>
    private sealed class ArchiveEntryStream : Stream
    {
        private readonly TarReader _reader;
        private readonly Stream _entry;

        /// <summary>
        /// 初始化条目流。
        /// </summary>
        /// <param name="reader">tar 读取器（由本流负责释放）。</param>
        /// <param name="entry">条目数据流。</param>
        public ArchiveEntryStream(TarReader reader, Stream entry)
        {
            _reader = reader;
            _entry = entry;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => _entry.Read(buffer, offset, count);

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _entry.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _entry.ReadAsync(buffer, offset, count, cancellationToken);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把 tar 条目名换算成相对于被列举目录的名称。
    /// </summary>
    /// <param name="entryName">tar 条目名。</param>
    /// <param name="directoryPath">被列举的目录路径（已规范化）。</param>
    /// <returns>相对名称；不属于该目录时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para><b>条目名带不带前导斜杠不确定</b>：实测（Docker 29.x）请求 <c>/data</c> 时
    /// 条目名是 <c>data/</c> 与 <c>data/inside.txt</c>（<b>无</b>前导斜杠），
    /// 而文档与部分版本给的是 <c>/data/inside.txt</c>。两种都要认，
    /// 否则「列举卷根目录永远为空」——这个症状看起来像权限问题，极难定位。</para>
    /// <para><b>因此统一先去掉前导斜杠再比较前缀</b>，比较完再把绝对路径补回去。</para>
    /// </remarks>
    private static string? GetRelativeName(string entryName, string directoryPath)
    {
        var relative = entryName.Replace('\\', '/').TrimStart('/');
        var directory = directoryPath.TrimStart('/');

        if (directory.Length == 0)
        {
            return relative.TrimEnd('/');
        }

        var prefix = directory + "/";

        if (relative.StartsWith(prefix, StringComparison.Ordinal))
        {
            return relative[prefix.Length..].TrimEnd('/');
        }

        // 有些版本回的是相对被列举目录的名字（如请求 /data 时条目就叫 inside.txt）
        return relative.TrimEnd('/');
    }

    /// <summary>
    /// 把 tar 条目映射成界面用的条目投影。
    /// </summary>
    /// <param name="entry">tar 条目。</param>
    /// <param name="directoryPath">所在目录。</param>
    /// <param name="relativeName">相对名称。</param>
    /// <returns>条目投影。</returns>
    private static FileEntry MapEntry(TarEntrySnapshot entry, string directoryPath, string relativeName)
    {
        var kind = entry.EntryType switch
        {
            TarEntryType.Directory => "directory",
            TarEntryType.RegularFile or TarEntryType.V7RegularFile => "file",
            TarEntryType.SymbolicLink or TarEntryType.HardLink => "symlink",
            _ => "other",
        };

        var path = ArchivePathGuard.IsRoot(directoryPath)
            ? "/" + relativeName
            : directoryPath + "/" + relativeName;

        return new FileEntry(
            relativeName,
            path,
            kind,
            entry.EntryType == TarEntryType.Directory ? 0 : entry.Length,
            entry.ModificationTime == default ? null : entry.ModificationTime.UtcDateTime,
            entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink ? entry.LinkName : null,
            entry.Mode == default ? null : Convert.ToString((int)entry.Mode, 8));
    }
}
