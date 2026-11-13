namespace Whalefleet.Core.Images;

/// <summary>
/// 一次镜像更新检查的结论。
/// </summary>
/// <remarks>
/// <para><b><see cref="Unknown"/> 与 <see cref="UpToDate"/> 必须分开</b>：
/// 「还没查过」与「查过、确实是最新的」在界面上是两件事——
/// 前者该显示「—」，后者该显示「已是最新」。合并成一个值会让刚加上的策略
/// 立刻宣称自己是最新的，而它其实一次都没查过。</para>
/// <para><b>失败也要留档</b>：<see cref="Failed"/> 配 <c>last_message</c>
/// 记录原因（仓库不可达 / 需要授权 / 限流）。只在日志里记失败的话，
/// 用户看到的是「一直是 —」，而他无法判断是没查还是查不到。</para>
/// </remarks>
public enum ImageUpdateState
{
    /// <summary>
    /// 尚未检查过（刚创建的策略）。
    /// </summary>
    Unknown,

    /// <summary>
    /// 仓库与本地摘要一致。
    /// </summary>
    UpToDate,

    /// <summary>
    /// 仓库有新摘要（尚未应用）。
    /// </summary>
    UpdateAvailable,

    /// <summary>
    /// 已应用（自动模式重建完成）。
    /// </summary>
    Applied,

    /// <summary>
    /// 检查或应用失败（原因见 <c>last_message</c>）。
    /// </summary>
    Failed,
}

/// <summary>
/// <see cref="ImageUpdateState"/> 的存储 / 接口文本形态（kebab-case）。
/// </summary>
public static class ImageUpdateStateText
{
    /// <summary>
    /// 枚举 → 文本。
    /// </summary>
    /// <param name="state">检查结论。</param>
    /// <returns>文本形态。</returns>
    public static string ToText(ImageUpdateState state) => state switch
    {
        ImageUpdateState.Unknown => "unknown",
        ImageUpdateState.UpToDate => "up-to-date",
        ImageUpdateState.UpdateAvailable => "update-available",
        ImageUpdateState.Applied => "applied",
        ImageUpdateState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知的镜像更新状态"),
    };

    /// <summary>
    /// 文本 → 枚举（大小写与首尾空白不敏感）。
    /// </summary>
    /// <param name="text">文本形态。</param>
    /// <returns>检查结论。</returns>
    /// <exception cref="FormatException">文本不是已知的状态。</exception>
    public static ImageUpdateState Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return text.Trim().ToLowerInvariant() switch
        {
            "unknown" => ImageUpdateState.Unknown,
            "up-to-date" => ImageUpdateState.UpToDate,
            "update-available" => ImageUpdateState.UpdateAvailable,
            "applied" => ImageUpdateState.Applied,
            "failed" => ImageUpdateState.Failed,
            _ => throw new FormatException($"未知的镜像更新状态：{text}"),
        };
    }
}
