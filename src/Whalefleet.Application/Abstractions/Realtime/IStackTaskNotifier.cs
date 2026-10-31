namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 编排任务失败通知器：把「后台任务失败了」推给客户端。
/// </summary>
/// <remarks>
/// <para><b>为什么失败要通知而成功不通知</b>：成功时用户正盯着界面看着进度条走完，
/// 界面自己就会刷新；而失败可能发生在<b>用户已经离开页面</b>之后
/// （webhook 触发的 CI 部署、后台自动任务）——
/// 那种情况下没有别的途径让用户知道出事了。</para>
/// <para><b>为什么放在应用层抽象</b>：推送是入站适配器（Api 层）的事，
/// 应用层只表达「这件事发生了，该通知」这一业务意图。</para>
/// </remarks>
public interface IStackTaskNotifier
{
    /// <summary>
    /// 通知「编排任务失败」。
    /// </summary>
    /// <param name="notification">通知内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>实现不得抛异常</b>：通知失败不能让已经失败的任务再失败一次。
    /// </remarks>
    Task NotifyFailedAsync(
        StackTaskFailureNotification notification,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 编排任务失败的通知内容。
/// </summary>
/// <param name="HostName">所属主机名。</param>
/// <param name="StackName">Stack 名称。</param>
/// <param name="Operation">操作类型。</param>
/// <param name="ErrorCode">业务错误码。</param>
/// <param name="Message">失败原因。</param>
/// <param name="OccurredAt">发生时刻（UTC）。</param>
public sealed record StackTaskFailureNotification(
    string HostName,
    string StackName,
    string Operation,
    int ErrorCode,
    string Message,
    DateTime OccurredAt);
