namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 容器事件总线（进程内发布 / 订阅）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：容器事件的<b>来源</b>（读 Docker 的 events 流）与
/// <b>去处</b>（推给浏览器）都在服务端，但两者之间不该有直接依赖：
/// 来源在 Infrastructure，去处在 Api。让 Infrastructure 直接引用 Api 的 Hub 上下文，
/// 就把依赖方向反了（Api → Infrastructure 是本项目的方向）。</para>
/// <para><b>为什么是进程内而不是持久化</b>：事件是「此刻正在发生」的事实，
/// 它的价值在于<b>及时</b>。错过的事件没有补看的必要（要看的是状态，去查 inspect 即可），
/// 而落库会带来「事件表 + 保留策略 + 迁移」这一整套与本迭代无关的成本。</para>
/// <para><b>为什么订阅方可以多个</b>：派发器之外还可能有别的消费者
/// （写审计是 0.9.0 的事）。总线做成多订阅者，免得到那时再改结构。</para>
/// </remarks>
public interface IContainerEventBus
{
    /// <summary>
    /// 发布一条容器事件。
    /// </summary>
    /// <param name="notification">事件内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>不阻塞</b>：订阅方处理慢时不能拖住发布方（发布方是 Docker 事件流的读取回调）。
    /// </remarks>
    ValueTask PublishAsync(ContainerEventNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅容器事件。
    /// </summary>
    /// <param name="cancellationToken">取消令牌；触发后枚举结束。</param>
    /// <returns>逐条产出的事件。</returns>
    /// <remarks>
    /// 每个订阅方拿到<b>独立</b>的流：一方取消不影响其它方。
    /// </remarks>
    IAsyncEnumerable<ContainerEventNotification> SubscribeAsync(CancellationToken cancellationToken = default);
}
