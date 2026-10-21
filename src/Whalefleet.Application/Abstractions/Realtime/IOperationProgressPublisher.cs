namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 镜像操作进度发布器。
/// </summary>
/// <remarks>
/// <para><b>为什么接口在应用层、实现却在 Api 层</b>：进度要推到客户端，
/// 而「怎么推」是入站适配器（Api 层）的职责——应用层不该知道 SignalR 的存在。
/// 但「拉取 / 构建过程中要把进度报出去」是<b>业务能力</b>，
/// 因此接口留在应用层，实现放在 Api 层并注册成同一个服务。</para>
/// <para><b>为什么不用 <c>IProgress&lt;T&gt;</c></b>：<c>IProgress</c> 只有一个
/// <c>Report</c> 回调，<b>没有完成与失败的表达</b>——
/// 而「操作失败」正是必须能明确表达的状态（Docker 的失败常常只出现在进度流里）。</para>
/// </remarks>
public interface IOperationProgressPublisher
{
    /// <summary>
    /// 发布一次操作进度。
    /// </summary>
    /// <param name="progress">进度内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>实现不得抛异常打断业务操作</b>：进度推送失败（例如客户端已断开）
    /// 不应该让拉取 / 构建本身失败。实现应当吞掉推送异常并记录日志。
    /// </remarks>
    ValueTask PublishAsync(ImageOperationProgress progress, CancellationToken cancellationToken = default);
}
