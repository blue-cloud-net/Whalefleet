namespace Whalefleet.Core.Alerting;

/// <summary>
/// 告警严重度（三级）。
/// </summary>
/// <remarks>
/// 存储形态是小写文本（<c>info</c> / <c>warning</c> / <c>critical</c>），
/// 由 Dapper 的枚举文本处理器读写；写入侧另有显式转换（见 <c>StorageTextExtensions</c>）。
/// </remarks>
public enum AlertSeverity
{
    /// <summary>
    /// 提示：值得知道，但不影响使用。
    /// </summary>
    Info,

    /// <summary>
    /// 警告：需要关注，可能正在劣化。
    /// </summary>
    Warning,

    /// <summary>
    /// 严重：已经不可用或即将不可用。
    /// </summary>
    Critical,
}
