using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Common;

namespace Whalefleet.Application.Files;

/// <summary>
/// 一次下载的凭证（票据）。
/// </summary>
/// <remarks>
/// <para><b>它是什么</b>：一张「凭这个串就能取走某个临时文件」的短时凭证。
/// 票据本身不是 access token，也不带任何权限判定——
/// 它只证明「这个下载请求来自一次已被授权的导出操作」。</para>
/// <para><b>为什么不用 access token 直接下载</b>：大文件下载走的是浏览器原生导航
/// （<c>&lt;a download&gt;</c> / <c>window.open</c>），而原生导航<b>带不了 Authorization 头</b>。
/// 把 access token 拼进 URL 又会把它写进浏览历史、反代访问日志与 Referer 头。
/// 因此改成「先换一张一次性的短票」——它只能取走那一个文件，取完即失效。</para>
/// </remarks>
/// <param name="Token">票据本体（随机串）。</param>
/// <param name="FilePath">临时文件的绝对路径。</param>
/// <param name="FileName">下载时呈现给用户的文件名。</param>
/// <param name="SizeBytes">文件大小（字节）。</param>
/// <param name="ExpiresAt">过期时刻（UTC）。</param>
/// <param name="HostId">所属主机名。</param>
/// <param name="ResourceKind">资源类型（<c>image</c> / <c>container</c>）。</param>
/// <param name="ResourceId">资源标识。</param>
public sealed record DownloadTicket(
    string Token,
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTime ExpiresAt,
    string HostId,
    string ResourceKind,
    string ResourceId);

/// <summary>
/// 下载票据登记表（<b>Singleton</b>，纯内存）。
/// </summary>
/// <remarks>
/// <para><b>为什么放内存而不是建表</b>：票据的有效期只有十分钟，
/// 它的全部价值是「让刚才那次导出能被下载」；
/// 落库会带来两个新问题：过期数据要清理、重启后要恢复半截状态。
/// 而「重启后票据失效」恰恰是<b>想要</b>的行为——进程重启后临时文件已被清空，
/// 留着票据只会让用户拿到一个指向不存在文件的下载链接。</para>
/// <para><b>线程安全</b>：写来自 HTTP 请求线程，读来自下载请求线程与后台扫描线程，
/// 因此用 <see cref="ConcurrentDictionary{TKey, TValue}"/>。</para>
/// </remarks>
public sealed class DownloadTicketStore
{
    /// <summary>
    /// 登记表里最多保留的票据数。
    /// </summary>
    /// <remarks>
    /// 超出时淘汰最旧的过期票据；未过期的<b>不</b>淘汰——
    /// 淘汰掉等于让用户点了下载却拿到「票据无效」。
    /// </remarks>
    public const int MaxTickets = 200;

    private readonly ConcurrentDictionary<string, DownloadTicket> _tickets = new(StringComparer.Ordinal);
    private readonly FilesOptions _options;
    private readonly ILogger<DownloadTicketStore> _logger;

    /// <summary>
    /// 初始化票据登记表。
    /// </summary>
    /// <param name="options">文件传输配置。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public DownloadTicketStore(IOptions<FilesOptions> options, ILogger<DownloadTicketStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 签发一张下载票据。
    /// </summary>
    /// <param name="filePath">临时文件的绝对路径。</param>
    /// <param name="fileName">下载时呈现给用户的文件名。</param>
    /// <param name="sizeBytes">文件大小（字节）。</param>
    /// <param name="hostId">所属主机名。</param>
    /// <param name="resourceKind">资源类型。</param>
    /// <param name="resourceId">资源标识。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    /// <returns>新签发的票据。</returns>
    /// <exception cref="BusinessException">
    /// 临时文件不存在（<c>1406</c>）。
    /// </exception>
    /// <remarks>
    /// 票据用 <see cref="RandomNumberGenerator"/> 生成 32 字节随机值并以 Base64Url 编码——
    /// 足够长（256 位熵）且不含 URL 需要转义的字符。
    /// </remarks>
    public DownloadTicket Issue(
        string filePath,
        string fileName,
        long sizeBytes,
        string hostId,
        string resourceKind,
        string resourceId,
        DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!File.Exists(filePath))
        {
            throw new BusinessException(
                ErrorCodes.FilePathNotFound,
                "导出产物不存在，无法签发下载票据");
        }

        this.Sweep(now);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var ticket = new DownloadTicket(
            token,
            filePath,
            fileName,
            sizeBytes,
            now.Add(_options.TicketTtl),
            hostId,
            resourceKind,
            resourceId);

        _tickets[token] = ticket;

        // 超出上限时淘汰最旧的**已过期**票据；一张都不过期说明流量异常，
        // 那时也要腾位置——否则内存会被票据撑满。
        if (_tickets.Count > MaxTickets)
        {
            this.EvictOldest(now);
        }

        return ticket;
    }

    /// <summary>
    /// 解析票据。
    /// </summary>
    /// <param name="token">票据本体。</param>
    /// <param name="now">当前时刻（UTC）。</param>
    /// <returns>有效票据；不存在或已过期返回 <see langword="null"/>。</returns>
    public DownloadTicket? Resolve(string? token, DateTime now)
    {
        if (String.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (!_tickets.TryGetValue(token, out var ticket))
        {
            return null;
        }

        if (ticket.ExpiresAt <= now)
        {
            // 顺手删掉：过期票据留着只会占位置，且下次解析仍会失败。
            _tickets.TryRemove(token, out _);

            return null;
        }

        return ticket;
    }

    /// <summary>
    /// 撤销一张票据（下载成功后调用，让它不能被重复使用）。
    /// </summary>
    /// <param name="token">票据本体。</param>
    /// <returns>撤销成功返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>为什么下载后要撤销</b>：票据只应取走那一个文件一次。
    /// 撤销之后同一个链接再点会得到 <c>1408</c>——
    /// 对「下载按钮被连点两次」这种场景更安全（不会并发读同一个文件）。
    /// </remarks>
    public bool Revoke(string? token)
    {
        if (String.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return _tickets.TryRemove(token, out _);
    }

    /// <summary>
    /// 清理过期票据（并删除它们指向的临时文件）。
    /// </summary>
    /// <param name="now">当前时刻（UTC）。</param>
    /// <returns>清理掉的票据数。</returns>
    public int Sweep(DateTime now)
    {
        var removed = 0;

        foreach (var pair in _tickets)
        {
            if (pair.Value.ExpiresAt > now)
            {
                continue;
            }

            if (_tickets.TryRemove(pair.Key, out var ticket))
            {
                TryDeleteFile(ticket.FilePath);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// 清空登记表并删除**本应用的**遗留产物（进程启动时调用）。
    /// </summary>
    /// <param name="directory">临时文件目录。</param>
    /// <returns>删除的文件数。</returns>
    /// <remarks>
    /// <para><b>为什么启动时要清</b>：上一次进程崩溃 / 被 kill 时留下的 tar 可能有好几个 GB，
    /// 而它们对应的票据早已随进程消失——没人会来取走它们，
    /// 放着就是白白占着磁盘。</para>
    /// <para><b>为什么只删「像我们落的」文件</b>：临时目录未必是应用独占的
    /// （运维可能把它指向一个共享卷）。若一律删除，会连别人的东西一起删掉——
    /// 而那种事一旦发生就不可挽回。因此只删符合本应用命名约定的产物
    /// （见 <see cref="ExportArtifactStore.IsArtifactFile"/>）。</para>
    /// </remarks>
    public int ClearDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _tickets.Clear();

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var count = 0;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!ExportArtifactStore.IsArtifactFile(Path.GetFileName(file)))
            {
                continue;
            }

            TryDeleteFile(file);
            count++;
        }

        if (count > 0)
        {
            _logger.LogInformation("已清理导出临时目录 {Directory} 中的 {Count} 个遗留文件", directory, count);
        }

        return count;
    }

    /// <summary>
    /// 删除临时文件（失败只记日志，不抛异常）。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception)
        {
            // 删不掉不是致命问题：文件最多留在磁盘上到下次启动清理。
            // 抛出去反而会让「票据过期」这件事变成一次 500。
            _logger.LogWarning(exception, "删除导出临时文件 {FilePath} 失败", filePath);
        }
    }

    /// <summary>
    /// 登记表超限时淘汰最旧的票据。
    /// </summary>
    /// <param name="now">当前时刻（UTC）。</param>
    private void EvictOldest(DateTime now)
    {
        var candidates = _tickets.Values
            .OrderBy(ticket => ticket.ExpiresAt)
            .Take(_tickets.Count - MaxTickets + 1)
            .ToArray();

        foreach (var ticket in candidates)
        {
            if (_tickets.TryRemove(ticket.Token, out _))
            {
                TryDeleteFile(ticket.FilePath);
            }
        }

        _logger.LogWarning(
            "下载票据超过上限 {Max}，已淘汰 {Count} 张（当前 {Now:O}）",
            MaxTickets,
            candidates.Length,
            now);
    }
}
