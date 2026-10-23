namespace Whalefleet.Application.Common;

/// <summary>
/// 资源清理（prune）的结果。
/// </summary>
/// <param name="Deleted">被删除的资源标识列表（镜像 ID / 卷名）。</param>
/// <param name="ReclaimedBytes">回收的字节数。</param>
/// <remarks>
/// <para><b>为什么不复用 <see cref="BatchOperationResult"/></b>：批量删除是「对一批已知资源逐条操作」，
/// 因此有「请求了几个 / 成功了几个 / 哪几条失败」这组数字；而清理是
/// <b>「让守护进程自己挑出该删的」</b>——请求里根本没有资源列表，
/// 于是「请求了几个」无从谈起，只有「删了哪些」与「回收了多少」。</para>
/// <para><b>回收量以守护进程为准</b>：卷的大小要遍历目录，镜像层的大小由守护进程按自己的账本算，
/// 本地拿 <c>GET /system/df</c> 的预估减一下只是<b>估算</b>。
/// 界面显示的是实际回收量，估算值留给「清理前的预览」那一步。</para>
/// <para><b>删除清单可能与预览不一致</b>：预览（<c>system/df</c>）是策略级的，
/// 守护进程在真正删除时还会跳过带挂载选项的卷、并按自己的引用计数判断。
/// 因此这个清单是<b>事实</b>，界面必须显示它而不是只显示「清理完成」。</para>
/// </remarks>
public sealed record PruneResult(
    IReadOnlyList<string> Deleted,
    long ReclaimedBytes);
