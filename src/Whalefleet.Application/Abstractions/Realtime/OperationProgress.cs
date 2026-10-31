namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 一次后台操作（镜像拉取 / 构建 / 推送、编排部署 / 更新 / 停止 / 删除）的进度。
/// </summary>
/// <remarks>
/// <para><b>为什么 <c>Percent</c> 可空</b>：Docker 的进度流只给出<b>分层</b>的
/// <c>current</c> / <c>total</c>，而<b>总层数事先不知道</b>——它随镜像仓库返回的内容变化。
/// 因此在拿到全部分层之前，算不出总体百分比。这种时候必须显式说「不知道」，
/// 而不能给 0（那是「确实是 0」）或 50（那是编造的数字）。
/// 编排操作的进度同理：算不出总体百分比时给 <see langword="null"/>。</para>
/// <para><b>为什么 <c>Error</c> 与 <c>Done</c> 都要显式给出</b>：
/// Docker 的操作失败时 HTTP 层往往仍是成功，错误只出现在进度流里。
/// 「流结束了但既没成功也没失败」是必须能表达的状态，否则失败会被报成成功。</para>
/// <para><b>为什么叫「操作进度」而不是「镜像进度」</b>（0.10.0 起）：
/// 编排 Stack 的部署 / 更新也走这条通道。若沿用「Pull」命名，
/// 「订阅一个叫 Pull 的频道看部署进度」会长期误导后来者。</para>
/// </remarks>
/// <param name="Token">进度令牌（客户端生成，回传给服务端以定位订阅者）。</param>
/// <param name="Operation">操作类型（<c>pull</c> / <c>build</c> / <c>push</c> / <c>deploy</c> / <c>update</c> / <c>stop</c> / <c>remove</c>）。</param>
/// <param name="Percent">总体进度百分比（0–100）；无法确定时为 <see langword="null"/>。</param>
/// <param name="Status">最近一条状态原文（守护进程给的，可直接展示）。</param>
/// <param name="Done">操作是否已成功结束。</param>
/// <param name="Error">失败原因；成功结束时为 <see langword="null"/>。</param>
public sealed record OperationProgress(
    string Token,
    string Operation,
    double? Percent,
    string Status,
    bool Done,
    string? Error);
