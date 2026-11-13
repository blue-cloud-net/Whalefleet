namespace Whalefleet.Application.Operations;

public sealed record ResourceTaskAccepted(
    string TaskId,
    string Status,
    int QueuePosition)
{
    /// <summary>
    /// 本次真正生效的覆盖变量名（Webhook 传参场景）。
    /// </summary>
    /// <remarks>
    /// <b>只回名字，不回值</b>：值会进反代的 access log，而它可能是配置甚至密钥。
    /// </remarks>
    public IReadOnlyList<string> AppliedVariables { get; init; } = [];

    /// <summary>
    /// 本次被忽略的覆盖变量名（未声明 / 未勾选可覆盖）。
    /// </summary>
    /// <remarks>
    /// <b>回带它们是必要的</b>：静默忽略会让 CI 以为「变量已经改了」，
    /// 而实际跑的还是旧值——那是最糟的组合（看起来成功，实际没生效）。
    /// </remarks>
    public IReadOnlyList<string> IgnoredVariables { get; init; } = [];
}
