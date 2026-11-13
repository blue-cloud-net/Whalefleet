using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Whalefleet.Application.Common;

namespace Whalefleet.Application.Files;

/// <summary>
/// 导出产物的存放位置管理（<b>Singleton</b>）。
/// </summary>
/// <remarks>
/// <para><b>为什么产物落盘而不直接流给浏览器</b>：导出走的是<b>浏览器原生导航</b>
/// （<c>&lt;a download&gt;</c>），而原生导航只能发起一个 GET 请求——
/// 它没法带请求体、没法带自定义头、也没法接收「边下边显示进度」的事件。
/// 因此服务端必须先把产物准备好（写成一个文件），再给浏览器一个直链。</para>
/// <para><b>为什么目录可配</b>：容器部署时导出产物可能很大，
/// 运维会希望把它放在单独的卷上（而不是跟数据库挤在一个卷里）。
/// 默认与数据库同卷（<c>/app/data/exports</c>），因为那通常是唯一可写的卷。</para>
/// </remarks>
public sealed class ExportArtifactStore
{
    private readonly FilesOptions _options;
    private readonly ILogger<ExportArtifactStore> _logger;
    private readonly object _gate = new();

    private string? _resolvedDirectory;

    /// <summary>
    /// 初始化产物存储。
    /// </summary>
    /// <param name="options">文件传输配置。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ExportArtifactStore(IOptions<FilesOptions> options, ILogger<ExportArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 解析并确保导出目录存在。
    /// </summary>
    /// <returns>导出目录的绝对路径。</returns>
    /// <exception cref="BusinessException">
    /// 目录无法创建（<c>1402</c>）。
    /// </exception>
    public string EnsureDirectory()
    {
        lock (_gate)
        {
            if (_resolvedDirectory is not null)
            {
                return _resolvedDirectory;
            }

            var configured = String.IsNullOrWhiteSpace(_options.TempDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "exports")
                : _options.TempDirectory;

            var full = Path.GetFullPath(configured);

            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception exception)
            {
                throw new BusinessException(
                    ErrorCodes.DockerApiFailed,
                    $"无法创建导出目录「{full}」：{exception.Message}");
            }

            _resolvedDirectory = full;
            _logger.LogInformation("导出产物目录：{Directory}", full);

            return full;
        }
    }

    /// <summary>
    /// 为一次导出分配产物路径。
    /// </summary>
    /// <param name="jobId">任务标识（用于文件名唯一化）。</param>
    /// <returns>产物文件的绝对路径（文件尚未创建）。</returns>
    /// <remarks>
    /// <para><b>落盘名与下载名是两回事</b>：落盘用「任务标识.tar」，
    /// 下载时呈现给用户的则是友好名字（如 <c>nginx-20261113-101530.tar</c>），
    /// 后者由票据携带。</para>
    /// <para><b>为什么落盘名要这么规整</b>：启动清理要能精确识别
    /// 「哪些文件是自己留下的」。如果落盘名是任意拼出来的，
    /// 清理就只能「删掉目录里的所有文件」——而那会误伤运维放在同一目录里的东西。</para>
    /// </remarks>
    public string AllocatePath(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var directory = this.EnsureDirectory();
        var safeId = SanitizeFileName(jobId);

        return Path.Combine(directory, $"{safeId}.tar");
    }

    /// <summary>
    /// 取文件大小（字节）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>文件大小；文件不存在时返回 0。</returns>
    public static long GetSize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);

        return info.Exists ? info.Length : 0;
    }

    /// <summary>
    /// 把文件名清洗成安全的 ASCII 名（不含路径分隔符与控制字符）。
    /// </summary>
    /// <param name="fileName">原始文件名。</param>
    /// <returns>清洗后的文件名。</returns>
    /// <remarks>
    /// 产物文件名会出现在 HTTP 响应头与磁盘路径里，
    /// 因此只保留字母、数字、<c>-</c>、<c>_</c> 与 <c>.</c>。
    /// 非 ASCII 字符会被替换成下划线——文件名里的中文没有实际价值，
    /// 而它会让「文件名非法」这类问题在跨平台时变得难以复现。
    /// </remarks>
    public static string SanitizeFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var builder = new System.Text.StringBuilder(fileName.Length);

        foreach (var character in fileName)
        {
            builder.Append(Char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        var result = builder.ToString().Trim('.');

        if (result.Length == 0)
        {
            return "export";
        }

        return result.Length > 120 ? result[..120] : result;
    }

    /// <summary>
    /// 判断一个文件是不是本应用的导出产物（用于启动清理）。
    /// </summary>
    /// <param name="fileName">文件名（不含目录）。</param>
    /// <returns>是导出产物返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>判据</b>：扩展名是 <c>.tar</c>，且主文件名是 32 位十六进制（Guid 的 "N" 形态）。
    /// 只有这种命名约定的文件才是本应用落的产物——
    /// 运维放在同一目录里的其它东西（备份、日志、手工下载的文件）不该被启动清理删掉。
    /// </remarks>
    public static bool IsArtifactFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^4];

        return stem.Length == 32 && stem.All(character => Char.IsAsciiHexDigitLower(character));
    }
}
