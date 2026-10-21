namespace Whalefleet.Application.Hosts;

/// <summary>
/// 主机的磁盘用量（<c>/system/df</c> 的精选投影）。
/// </summary>
/// <param name="Images">镜像层用量；未查询该类时为 <see langword="null"/>。</param>
/// <param name="Containers">容器可写层用量；未查询该类时为 <see langword="null"/>。</param>
/// <param name="Volumes">数据卷用量；未查询该类时为 <see langword="null"/>。</param>
/// <param name="BuildCache">构建缓存用量；未查询该类时为 <see langword="null"/>。</param>
/// <remarks>
/// <para><b>为什么「未查询」是 <see langword="null"/> 而不是空桶</b>：
/// 带 <c>type</c> 过滤时守护进程只返回那一类，其余三类<b>根本没统计</b>。
/// 给一个「0 字节」的空桶会让人以为「这台机器上没有镜像」，
/// 而真相是「这趟没查」——与 <c>imagesSizeBytes</c> 查不到时给 <see langword="null"/> 同源。</para>
/// <para><b>为什么桶里既有汇总也有条目</b>：界面既要显示「可回收多少」（汇总），
/// 也要在 0.7.0 的清理入口里列出「将要删除什么」（条目）。分两个端点等于让前端把
/// 同一个 <c>/system/df</c> 调两次。</para>
/// </remarks>
public sealed record HostDiskUsage(
    DiskUsageBucket? Images,
    DiskUsageBucket? Containers,
    DiskUsageBucket? Volumes,
    DiskUsageBucket? BuildCache);

/// <summary>
/// 某一类资源的用量汇总。
/// </summary>
/// <param name="TotalCount">条目总数；守护进程未给出时为 <see langword="null"/>。</param>
/// <param name="ActiveCount">其中仍在使用的条目数。</param>
/// <param name="TotalSize">占用字节总数。</param>
/// <param name="Reclaimable">可回收字节数。</param>
/// <param name="Items">逐条明细。</param>
/// <remarks>
/// <b>可空而不是 0</b>：老版本守护进程、第三方存储驱动都可能不给用量。
/// 「不知道」与「确实是 0」在界面上必须能分开（前者显示「—」，后者显示 0 B）。
/// </remarks>
public sealed record DiskUsageBucket(
    long? TotalCount,
    long? ActiveCount,
    long? TotalSize,
    long? Reclaimable,
    IReadOnlyList<DiskUsageItem> Items);

/// <summary>
/// 一条用量明细。
/// </summary>
/// <param name="Id">
/// 条目标识：镜像 ID（<c>sha256:…</c>）/ 容器 ID / 卷名 / 构建缓存记录 ID。
/// </param>
/// <param name="Name">
/// 可读名称：镜像的第一个仓库标签、容器名、卷名、构建缓存描述；取不到时为 <see langword="null"/>。
/// </param>
/// <param name="Size">占用字节数；守护进程未给出时为 <see langword="null"/>。</param>
/// <param name="InUse">是否仍被引用（镜像被容器使用 / 缓存记录在用）；未知时为 <see langword="null"/>。</param>
public sealed record DiskUsageItem(
    string Id,
    string? Name,
    long? Size,
    bool? InUse);
