namespace Whalefleet.Application.Volumes;

/// <summary>
/// 数据卷列表项。
/// </summary>
/// <param name="Name">数据卷名（单机守护进程内唯一，也是删除时使用的标识）。</param>
/// <param name="Driver">存储驱动（<c>local</c> / <c>nfs</c> / <c>cephfs</c> 等）。</param>
/// <param name="Mountpoint">宿主机上的挂载路径；仅 <c>local</c> 驱动有值。</param>
/// <param name="Scope">作用域（<c>local</c> / <c>global</c>；Swarm 全局卷才会是后者）。</param>
/// <param name="Created">创建时间（UTC）；守护进程未提供时为 <see langword="null"/>。</param>
/// <param name="Size">占用字节数；守护进程未提供用量时为 <c>0</c>。</param>
/// <param name="RefCount">引用它的容器数；守护进程未提供时为 <c>0</c>。</param>
/// <param name="Labels">标签。</param>
/// <remarks>
/// <para><b>为什么逐字段挑选而不是直接序列化 SDK 的 <c>VolumeResponse</c></b>：
/// 那个对象带着 <c>ClusterVolume</c> / <c>Status</c>（<c>IDictionary&lt;string, object&gt;</c>）
/// 等列表页用不到的东西，其中 <c>Status</c> 的形状**随驱动而变**——原样透传等于让
/// 「响应里有什么」取决于守护进程上装了哪些驱动。</para>
/// <para><b><see cref="Size"/> 与 <see cref="RefCount"/> 标注了「可能为 0」</b>：
/// 这两个值来自守护进程的 <c>UsageData</c>，只在 Docker API 1.42+ 的
/// <c>GET /volumes</c> 里出现。更老的守护进程（以及部分精简实现）不返回该字段，
/// 此时 JSON 里根本没有这个键，反序列化得到默认值 <c>0</c>。
/// 界面<b>不能</b>把 <c>0</c> 当成「空卷」——它与「真的没用」无法区分，
/// 正确做法是显示成「—」而不是「0 B」。</para>
/// <para><b>没有 <c>Id</c></b>：数据卷用名字标识，没有独立 ID。删除、挂载、
/// 容器配置里引用的都是名字。这与镜像（按 ID 或标签）不同，别照搬镜像那套解析逻辑。</para>
/// </remarks>
public sealed record VolumeSummary(
    string Name,
    string Driver,
    string Mountpoint,
    string Scope,
    DateTimeOffset? Created,
    long Size,
    long RefCount,
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
/// <param name="Items">全部数据卷（<b>不分页</b>）。</param>
/// <param name="Warnings">守护进程在列举过程中给出的告警（如驱动缺失）。</param>
/// <remarks>
/// <para><b>为什么列表不分页，而容器与镜像分页</b>：容器与镜像的条目里带
/// 端口映射、标签数组、宿主路径，单条就有几百字节，上千条会让响应体失控；
/// 而数据卷的列表项在去掉驱动选项后只有几十字节，且单台主机上的数据卷
/// 通常是个位数到几十条（绝大多数是 <c>docker compose</c> 顺带建的）。
/// 真到了成百上千个卷的场景，该做的是让界面加筛选，而不是先在服务端切一刀。</para>
/// <para><b>分页与筛选统一归 <c>0.16.0</c>（列表与查询）</b>：
/// <c>sortBy</c> / <c>sortDir</c> / <c>search</c> 是四类资源列表的<b>统一</b>改动，
/// 分两次做会让前端先适配一套半成品接口、之后再改一遍。
/// 因此本迭代的列表端点<b>不收请求体</b>。</para>
/// <para><b><see cref="Warnings"/> 必须回带</b>：<c>GET /volumes</c> 会在响应里
/// 单独给出 <c>warnings</c> 数组（如「已挂载卷的驱动 xxx 未安装」）。
/// 只看 <c>volumes</c> 会让这些告警静默丢失，用户看到的列表是「少了几个卷」却不知道为什么。</para>
/// </remarks>
public sealed record VolumeListResult(
    IReadOnlyList<VolumeSummary> Items,
    IReadOnlyList<string> Warnings);
