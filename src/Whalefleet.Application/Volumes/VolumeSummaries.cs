namespace Whalefleet.Application.Volumes;

/// <summary>
/// 数据卷列表项。
/// </summary>
/// <param name="Name">数据卷名（单机守护进程内唯一，也是删除时使用的标识）。</param>
/// <param name="Driver">存储驱动（<c>local</c> / <c>nfs</c> / <c>cephfs</c> 等）。</param>
/// <param name="Mountpoint">宿主机上的挂载路径；仅 <c>local</c> 驱动有值。</param>
/// <param name="Scope">作用域（<c>local</c> / <c>global</c>；Swarm 全局卷才会是后者）。</param>
/// <param name="Created">创建时间（UTC）；守护进程未提供时为 <see langword="null"/>。</param>
/// <param name="Size">占用字节数；守护进程未提供用量时为 <see langword="null"/>。</param>
/// <param name="RefCount">引用它的容器数；守护进程未提供用量时为 <see langword="null"/>。</param>
/// <param name="Labels">标签。</param>
/// <remarks>
/// <para><b>为什么逐字段挑选而不是直接序列化 SDK 的 <c>VolumeResponse</c></b>：
/// 那个对象带着 <c>ClusterVolume</c> / <c>Status</c>（<c>IDictionary&lt;string, object&gt;</c>）
/// 等列表页用不到的东西，其中 <c>Status</c> 的形状**随驱动而变**——原样透传等于让
/// 「响应里有什么」取决于守护进程上装了哪些驱动。</para>
/// <para><b><see cref="Size"/> 与 <see cref="RefCount"/> 可空，且 <c>null</c> 与 <c>0</c> 含义不同</b>：
/// 用量来自 <c>GET /system/df</c>，<b>不是</b>列表接口——<c>GET /volumes</c> 的每一项里
/// **没有** <c>UsageData</c> 键（实测：列表接口返回 216 项，全部无此键；
/// 换成 <c>/system/df?type=volume</c> 才有）。用量查询失败（老守护进程、精简实现、
/// 第三方驱动不报）时给 <c>null</c>，表示「不知道」；<c>0</c> 则是一个确切的答案
/// （空卷、没人引用）。界面据此把 <c>null</c> 显示成「—」、把 <c>0</c> 如实显示成
/// 「0 B」/「0」。</para>
/// <para><b>没有 <c>Id</c></b>：数据卷用名字标识，没有独立 ID。删除、挂载、
/// 容器配置里引用的都是名字。这与镜像（按 ID 或标签）不同，别照搬镜像那套解析逻辑。</para>
/// </remarks>
public sealed record VolumeSummary(
    string Name,
    string Driver,
    string Mountpoint,
    string Scope,
    DateTimeOffset? Created,
    long? Size,
    long? RefCount,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// 数据卷详情（inspect 的<b>精选</b>投影）。
/// </summary>
/// <param name="Name">数据卷名。</param>
/// <param name="Driver">存储驱动。</param>
/// <param name="Mountpoint">宿主机上的挂载路径。</param>
/// <param name="Scope">作用域。</param>
/// <param name="Created">创建时间（UTC）；守护进程未提供时为 <see langword="null"/>。</param>
/// <param name="Options">驱动选项（如 <c>local</c> 驱动的 <c>device</c> / <c>o</c>）。</param>
/// <param name="Labels">标签。</param>
/// <remarks>
/// <para><b>为什么没有 <c>Size</c> / <c>RefCount</c></b>：inspect 响应里**不含**用量信息——
/// 它来自列表接口的 <c>UsageData</c>。硬塞两个恒为 0 的字段进去，
/// 只会让界面显示出「0 B」这种看着精确、实则伪造的数据。</para>
/// <para><b><see cref="Options"/> 是详情页的主要价值</b>：<c>local</c> 驱动的
/// <c>o=bind</c> / <c>device=/data/xx</c> 决定了数据实际落在哪，是排障时最常被问到的信息，
/// 而列表页不返回它。</para>
/// </remarks>
public sealed record VolumeDetail(
    string Name,
    string Driver,
    string Mountpoint,
    string Scope,
    DateTimeOffset? Created,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// 数据卷列表结果。
/// </summary>
/// <param name="Items">当前页的数据卷。</param>
/// <param name="Total">分页前的总条数。</param>
/// <param name="PageIndex">服务端实际使用的页码（从 0 开始，负数已按 0 处理）。</param>
/// <param name="PageSize">服务端实际使用的每页条数（**已夹紧**：请求 5000 会回 100）。</param>
/// <param name="Warnings">守护进程在列举过程中给出的告警（如驱动缺失）。</param>
/// <remarks>
/// <para><b>分页与容器 / 镜像同一套口径</b>：Docker 的 <c>GET /volumes</c> 没有分页参数，
/// 切片在服务端内存完成；<c>pageIndex</c> 从 0 开始，<c>pageSize</c> 默认 20、上限 100，
/// 超出**夹紧**而不是报错。</para>
/// <para><b><see cref="Total"/> 不能由界面用 <c>items.length</c> 推</b>：那样只在「只有一页」时正确，
/// 一旦翻页，页脚就会显示成「共 20 个」。</para>
/// <para><b><see cref="Warnings"/> 必须回带</b>：<c>GET /volumes</c> 会在响应里
/// 单独给出 <c>warnings</c> 数组（如「已挂载卷的驱动 xxx 未安装」）。
/// 只看 <c>volumes</c> 会让这些告警静默丢失，用户看到的列表是「少了几个卷」却不知道为什么。</para>
/// </remarks>
public sealed record VolumeListResult(
    IReadOnlyList<VolumeSummary> Items,
    int Total,
    int PageIndex,
    int PageSize,
    IReadOnlyList<string> Warnings);
