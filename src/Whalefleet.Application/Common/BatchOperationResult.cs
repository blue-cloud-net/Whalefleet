namespace Whalefleet.Application.Common;

/// <summary>
/// 批量操作里单个资源的失败详情。
/// </summary>
/// <param name="ResourceId">失败的资源标识（容器 ID / 镜像引用）。</param>
/// <param name="Code">业务错误码（与单条端点抛出的完全一致，如 <c>1404</c>）。</param>
/// <param name="Message">失败原因（与单条端点的消息一致）。</param>
/// <remarks>
/// <para><b>为什么批量操作要逐条汇报失败</b>：批量删除 20 个容器，失败 1 个很常见
/// （那个正在运行的没带 <c>force</c>）。整批报一个错误的话，界面只能显示
/// 「操作失败」——用户既不知道是哪 20 个里失败了 1 个，也不知道其余 19 个到底删没删。
/// 而如果整批都失败了，多半是主机连不上（<c>1301</c>）或没带 <c>force</c>；
/// 这类「全体失败」会走整批的异常路径，仍然是一个明确的 HTTP 状态码。</para>
/// <para><b>错误码必须与单条端点一致</b>：只有这样界面才能复用同一套提示文案，
/// 而不是为批量场景另写一套「批量错误码表」。</para>
/// </remarks>
public sealed record BatchOperationFailure(string ResourceId, int Code, string Message);

/// <summary>
/// 批量操作的汇总结果（**部分成功也是成功**）。
/// </summary>
/// <param name="Requested">请求里给出的资源个数（去重前）。</param>
/// <param name="Succeeded">成功处理的个数。</param>
/// <param name="Failures">逐条失败详情（可能为空）。</param>
/// <remarks>
/// <para><b>部分成功返回 200 而不是 4xx</b>：HTTP 状态码表达不了「10 个里成了 7 个」。
/// 报 207（Multi-Status）之类的码在这个 RPC 风格的契约里只会让所有客户端都当成未知情况，
/// 而真正需要的信息就在 <see cref="Failures"/> 里，由界面决定怎么呈现。</para>
/// <para><b>全批失败仍返回 200</b>：逐条失败的原因已经写在 <see cref="Failures"/> 里，
/// 而这些原因通常彼此不同（有的 1404、有的 1401），没有哪一个能代表整批的状态码。</para>
/// </remarks>
public sealed record BatchOperationResult(
    int Requested,
    int Succeeded,
    IReadOnlyList<BatchOperationFailure> Failures)
{
    /// <summary>
    /// 构造「全部成功」的结果。
    /// </summary>
    /// <param name="requested">请求里的资源个数。</param>
    /// <returns>没有失败明细的结果。</returns>
    public static BatchOperationResult AllSucceeded(int requested) => new(requested, requested, []);

    /// <summary>
    /// 是否为「一条都没成」。
    /// </summary>
    public bool IsTotalFailure => Succeeded == 0 && Failures.Count > 0;
}
