namespace Whalefleet.Api.Configuration;

/// <summary>
/// 解析 SPA 产物目录的绝对路径。
/// </summary>
/// <remarks>
/// <para><b>为什么不用默认的 wwwroot 约定</b>：本项目的前端产物由 Dockerfile
/// 的 builder 阶段产出，再复制进发布目录。用显式配置项而非隐式约定，
/// 是为了让「产物到底在哪个目录」在配置与日志里都看得见——
/// 这类「目录对不上」的问题排查起来通常要翻三层镜像层。</para>
/// <para><b>为什么必须校验目录里有入口文件</b>：目录存在但里面是空的
/// （例如 <c>pnpm build</c> 失败却因 <c>-e</c> 未中断构建）同样不可用。
/// 只判目录存在会得到一个「启动成功但打开是 404」的容器。</para>
/// </remarks>
internal static class SpaRootResolver
{
    /// <summary>
    /// 解析产物目录。
    /// </summary>
    /// <param name="options">SPA 配置。</param>
    /// <param name="contentRoot">应用内容根目录（用于解析相对路径）。</param>
    /// <returns>绝对路径；目录或入口文件不存在时返回 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static string? Resolve(SpaOptions options, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var root = Path.IsPathRooted(options.RootPath)
            ? options.RootPath
            : Path.Combine(contentRoot, options.RootPath);

        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            return null;
        }

        var index = Path.Combine(full, options.IndexFile);
        return File.Exists(index) ? full : null;
    }
}