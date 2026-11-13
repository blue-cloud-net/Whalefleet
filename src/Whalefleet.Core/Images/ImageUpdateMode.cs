namespace Whalefleet.Core.Images;

/// <summary>
/// 镜像自动更新策略的执行模式。
/// </summary>
public enum ImageUpdateMode
{
    /// <summary>
    /// 只报告：发现仓库有新摘要时写一条通知，<b>不动容器</b>。
    /// </summary>
    Notify,

    /// <summary>
    /// 自动应用：发现新摘要时拉取镜像并重建引用该镜像的容器。
    /// </summary>
    Auto,
}

/// <summary>
/// <see cref="ImageUpdateMode"/> 的存储 / 接口文本形态（小写）。
/// </summary>
/// <remarks>
/// <b>不用枚举数值落库</b>：以后插入新档位时数值会整体错位，
/// 而旧数据不会跟着改——那是一次静默的「策略含义全变」。
/// </remarks>
public static class ImageUpdateModeText
{
    /// <summary>
    /// 枚举 → 文本。
    /// </summary>
    /// <param name="mode">执行模式。</param>
    /// <returns>文本形态。</returns>
    public static string ToText(ImageUpdateMode mode) => mode switch
    {
        ImageUpdateMode.Notify => "notify",
        ImageUpdateMode.Auto => "auto",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的镜像更新模式"),
    };

    /// <summary>
    /// 文本 → 枚举（大小写与首尾空白不敏感）。
    /// </summary>
    /// <param name="text">文本形态。</param>
    /// <returns>执行模式。</returns>
    /// <exception cref="FormatException">文本不是已知的执行模式。</exception>
    public static ImageUpdateMode Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return text.Trim().ToLowerInvariant() switch
        {
            "notify" => ImageUpdateMode.Notify,
            "auto" => ImageUpdateMode.Auto,
            _ => throw new FormatException($"未知的镜像更新模式：{text}"),
        };
    }
}
