namespace Whalefleet.Application.Abstractions.Realtime;

/// <summary>
/// 容器生命周期事件（目前只推送「异常退出」）。
/// </summary>
/// <remarks>
/// <para><b>为什么不把整个 Docker 事件透传出去</b>：Docker 的 events 流里有几十种事件
/// （create / start / die / oom / exec_create / health_status …），逐条透传会让前端
/// 不得不为每一种都写处理逻辑，而其中绝大多数没有展示价值。
/// 本项目只把<b>需要人介入</b>的那一类挑出来，语义在服务端就收敛好。</para>
/// <para><b>为什么带主机标识</b>：通知 Hub 是全局的（不带 <c>hostId</c> 的路径），
/// 而「哪个容器出事了」必须连主机一起说——否则用户在多主机部署里看到一条通知，
/// 不知道该去看哪台机器。</para>
/// <para><b>为什么不带 <c>exitCode</c> 之外的完整 inspect</b>：为每条事件去查一次 inspect
/// 会在容器频繁重启时打爆守护进程。</para>
/// </remarks>
/// <param name="HostId">主机标识（主机名）。</param>
/// <param name="ContainerId">容器 ID。</param>
/// <param name="ContainerName">容器名（来自事件的 actor 属性）。</param>
/// <param name="ExitCode">退出码。</param>
/// <param name="OutOfMemory">是否因内存不足被杀死（OOM）。</param>
/// <param name="OccurredAt">事件发生时刻（UTC）。</param>
public sealed record ContainerEventNotification(
    string HostId,
    string ContainerId,
    string ContainerName,
    long ExitCode,
    bool OutOfMemory,
    DateTimeOffset OccurredAt);
