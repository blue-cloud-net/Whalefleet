using Whalefleet.Application.Common;

namespace Whalefleet.Application.Files;

/// <summary>
/// 容器 / 卷内部路径的规范化与合法性校验。
/// </summary>
/// <remarks>
/// <para><b>为什么必须在这里挡</b>：文件浏览与写入的路径是<b>用户给的</b>，
/// 而守护进程的 <c>/containers/{id}/archive</c> 端点会把它直接交给容器内的文件系统去解析。
/// 一个 <c>..</c> 就可能写到容器根之外，一个绝对路径也可能指向挂载点之外的地方。
/// 守护进程<b>不会</b>替我们做这件事——它只负责「按这个路径取/放」。</para>
/// <para><b>这里只管「语法合法性」</b>：路径里含 <c>..</c>、绝对路径前缀、NUL 字符、
/// 空段、过长路径段，一律拒绝。真正防「经由符号链接逃逸」的那一层在
/// <c>FileBrowserService</c>——它逐级列出父目录、确认中间段不是符号链接。</para>
/// <para><b>规范化结果一律以 <c>/</c> 开头且不含尾斜杠</b>（根目录除外），
/// 这样调用方可以直接把结果与守护进程返回的条目名对齐。</para>
/// </remarks>
public static class ArchivePathGuard
{
    /// <summary>
    /// 路径的最大长度（字符数）。
    /// </summary>
    /// <remarks>
    /// Linux 的 <c>PATH_MAX</c> 是 4096，留出余量取 4000。
    /// 超长路径在容器内本就创建不出来，早点拒绝比让守护进程报错更清楚。
    /// </remarks>
    public const int MaxPathLength = 4000;

    /// <summary>
    /// 单个路径段的最大长度（字符数）。
    /// </summary>
    /// <remarks>Linux 的 <c>NAME_MAX</c> 是 255。</remarks>
    public const int MaxSegmentLength = 255;

    /// <summary>
    /// 规范化并校验一个容器 / 卷内部路径。
    /// </summary>
    /// <param name="path">原始路径（可空、可相对、可带 <c>.</c> 与 <c>..</c>）。</param>
    /// <returns>规范化后的绝对路径（以 <c>/</c> 开头，根目录为 <c>/</c>）。</returns>
    /// <exception cref="BusinessException">
    /// 路径非法（<c>1405</c>）——含 <c>..</c> 上跳、NUL 字符、超长、或空路径。
    /// </exception>
    /// <remarks>
    /// <para><b>为什么「空路径」算非法而不是「当前目录」</b>：界面上「当前目录」是显式的 <c>/</c>，
    /// 而空串更可能是前端漏传字段。把它当成「当前目录」会静默返回根目录的列表——
    /// 用户以为自己在一个子目录里，实际看到的是根目录的内容。</para>
    /// <para><b><c>.</c> 段会被去掉</b>（<c>/a/./b</c> → <c>/a/b</c>），
    /// 而 <c>..</c> 段<b>一律拒绝</b>而不是「往上跳」——
    /// 往上跳的合法用法（从子目录回到父目录）由界面上的面包屑负责，
    /// 路径参数里出现它只说明调用方在拼路径时算错了。</para>
    /// </remarks>
    public static string Normalize(string? path)
    {
        if (path is null || String.IsNullOrWhiteSpace(path))
        {
            throw new BusinessException(
                ErrorCodes.InvalidFilePath,
                "文件路径不能为空");
        }

        if (path.Length > MaxPathLength)
        {
            throw new BusinessException(
                ErrorCodes.InvalidFilePath,
                $"文件路径过长（上限 {MaxPathLength} 字符）");
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new BusinessException(
                ErrorCodes.InvalidFilePath,
                "文件路径不能包含 NUL 字符");
        }

        // 统一分隔符：Windows 风格的反斜杠在容器内没有意义，
        // 但它出现时几乎总是调用方拼错了。直接当作非法路径比「猜」更安全。
        var unified = path.Replace('\\', '/');

        var segments = new List<string>();

        foreach (var raw in unified.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw is "." or "")
            {
                // 当前目录段：直接跳过
                continue;
            }

            if (raw is "..")
            {
                throw new BusinessException(
                    ErrorCodes.InvalidFilePath,
                    "文件路径不能包含上级引用（..）");
            }

            if (raw.Length > MaxSegmentLength)
            {
                throw new BusinessException(
                    ErrorCodes.InvalidFilePath,
                    $"文件路径段过长（上限 {MaxSegmentLength} 字符）");
            }

            segments.Add(raw);
        }

        return segments.Count == 0 ? "/" : "/" + String.Join('/', segments);
    }

    /// <summary>
    /// 判断一个路径是否为根目录。
    /// </summary>
    /// <param name="normalizedPath">已规范化的路径。</param>
    /// <returns>是根目录返回 <see langword="true"/>。</returns>
    public static bool IsRoot(string normalizedPath) =>
        String.Equals(normalizedPath, "/", StringComparison.Ordinal);

    /// <summary>
    /// 取路径的父目录。
    /// </summary>
    /// <param name="normalizedPath">已规范化的路径。</param>
    /// <returns>父目录路径；根目录返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 根目录没有父目录——这是「不能再往上」的事实，
    /// 而不是「父目录就是它自己」（那样会让面包屑永远停不下来）。
    /// </remarks>
    public static string? GetParent(string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);

        if (IsRoot(normalizedPath))
        {
            return null;
        }

        var index = normalizedPath.LastIndexOf('/');

        return index <= 0 ? "/" : normalizedPath[..index];
    }

    /// <summary>
    /// 取路径的最后一段（文件名 / 目录名）。
    /// </summary>
    /// <param name="normalizedPath">已规范化的路径。</param>
    /// <returns>最后一段；根目录返回空串。</returns>
    public static string GetName(string normalizedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);

        if (IsRoot(normalizedPath))
        {
            return String.Empty;
        }

        var index = normalizedPath.LastIndexOf('/');

        return normalizedPath[(index + 1)..];
    }

    /// <summary>
    /// 把路径与一个条目名拼起来（用于遍历目录时逐级下行）。
    /// </summary>
    /// <param name="directoryPath">目录路径（已规范化）。</param>
    /// <param name="name">条目名。</param>
    /// <returns>拼接后的路径。</returns>
    /// <exception cref="BusinessException">
    /// 条目名非法（<c>1405</c>）——含路径分隔符或上跳引用。
    /// </exception>
    /// <remarks>
    /// 条目名来自守护进程返回的 tar 条目，理论上不含分隔符；
    /// 但它是<b>外部数据</b>，拼接前仍要校验——
    /// 否则一个形如 <c>..</c> 的条目名就能把路径拼到目录之外。
    /// </remarks>
    public static string Combine(string directoryPath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new BusinessException(
                ErrorCodes.InvalidFilePath,
                $"条目名「{name}」不能包含路径分隔符");
        }

        if (name is "." or "..")
        {
            throw new BusinessException(
                ErrorCodes.InvalidFilePath,
                $"条目名「{name}」不是合法的目录项");
        }

        return Normalize(directoryPath + "/" + name);
    }
}
