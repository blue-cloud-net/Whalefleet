namespace Whalefleet.Application.Volumes;

/// <summary>
/// 数据卷列表请求。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：请求体由客户端决定，缺字段是常态（旧前端、手写 <c>curl</c> 都可能只发 <c>{}</c>）。
/// 校验与夹紧统一在 <see cref="VolumeService.ListAsync"/> 里做。
/// </remarks>
public sealed record ListVolumesRequest
{
    /// <summary>
    /// 关键字搜索：按卷名 / 驱动 / 宿主挂载路径模糊匹配（不区分大小写）。
    /// </summary>
    /// <remarks>
    /// 在<b>服务端内存</b>里过滤：分页也在服务端做，前端过滤只能筛到当前页，
    /// 规则会与后端漂移。与镜像列表的 <c>search</c> 同口径。
    /// </remarks>
    public string? Search { get; init; }

    /// <summary>页码（从 0 开始）；省略时按 0 处理，负数同样按 0 处理。</summary>
    public int? PageIndex { get; init; }

    /// <summary>每页条数；省略时用默认值（20），超出上限（100）会被夹紧。</summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// 数据卷详情请求。
/// </summary>
public sealed record InspectVolumeRequest
{
    /// <summary>数据卷名（大小写敏感）；必填。</summary>
    public string? Name { get; init; }
}

/// <summary>
/// 创建数据卷请求。
/// </summary>
public sealed record CreateVolumeRequest
{
    /// <summary>数据卷名；必填。须匹配 <c>^[a-zA-Z0-9][a-zA-Z0-9_.-]*$</c>。</summary>
    /// <remarks>
    /// 省略时<b>不</b>自动生成随机名——那会造出一个用户没预期的卷。
    /// Docker 自己支持省略名字生成匿名卷，但那条路径只应由「按需挂载」触发，
    /// 而本端点的存在意义就是「明确创建一个有名字的卷」。
    /// </remarks>
    public string? Name { get; init; }

    /// <summary>存储驱动；省略时由守护进程按主机默认选择（通常是 <c>local</c>）。</summary>
    public string? Driver { get; init; }

    /// <summary>驱动选项（如 <c>local</c> 的 <c>device</c> = <c>/data/xx</c>、<c>o</c> = <c>bind</c>）。</summary>
    public IReadOnlyDictionary<string, string>? DriverOptions { get; init; }

    /// <summary>标签。</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }
}

/// <summary>
/// 删除数据卷请求。
/// </summary>
public sealed record RemoveVolumeRequest
{
    /// <summary>数据卷名；必填。</summary>
    public string? Name { get; init; }

    /// <summary>是否忽略「卷不存在」；省略为 <see langword="false"/>。</summary>
    /// <remarks>
    /// <para>⚠️ <b>它不能绕过「卷被引用」，实测如此</b>：只要还有容器挂着这个卷，
    /// 守护进程就会拒绝删除，<b>带不带 <c>force</c> 都一样</b>
    /// （容器在运行或已停止都一样，必须先把容器删掉）。
    /// 把 <c>force</c> 做成「名字还在就删」的开关会让人以为能强行删掉在用的卷——
    /// 结果是点了没反应、还不知道原因。</para>
    /// <para><b>它真正的作用是幂等</b>：卷不存在时带 <c>force</c> 返回 200，
    /// 不带则返回 404。批量删除的前端会把上一轮已经删掉的项再提交一次，
    /// 这时 <c>force</c> 能避免整批因为「其中一条已经没了」而报错。</para>
    /// <para><b>主机不可达不会被当成「不存在」</b>：那种情况仍然报 1301，
    /// 否则用户会以为删干净了，实际一个没删。</para>
    /// </remarks>
    public bool? Force { get; init; }
}

/// <summary>
/// 批量删除数据卷请求。
/// </summary>
public sealed record BatchRemoveVolumesRequest
{
    /// <summary>要删除的数据卷名列表；必填且不能为空，最多 100 个。</summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary>是否忽略「卷不存在」；省略为 <see langword="false"/>。</summary>
    /// <remarks>语义与 <see cref="RemoveVolumeRequest.Force"/> 完全相同，同样<b>不能</b>删掉仍被引用的卷。</remarks>
    public bool? Force { get; init; }
}

/// <summary>
/// 清理数据卷请求（对应 <c>docker volume prune</c>）。
/// </summary>
/// <remarks>
/// <para><b>没有「只清匿名卷」的开关</b>：守护进程自 Engine API 1.42 起默认只清
/// 「匿名卷」（那些没有名字的），而本项目的预览（<c>GET /system/df</c>）列的是
/// <b>全部</b>未被引用的卷。两者对不上时，用户会看到「清单里有 5 个，删完还在」。
/// 因此服务端固定按「连具名卷一起清」下发（<c>all=true</c>），
/// 让预览与实际清理是同一套口径。</para>
/// </remarks>
public sealed record PruneVolumesRequest
{
    /// <summary>
    /// 标签过滤表达式（<c>key</c> 或 <c>key=value</c>）；省略表示不按标签过滤。
    /// </summary>
    /// <remarks>
    /// 既能用于「只清某类卷」，也让集成测试可以把范围限制在自己建的卷上，
    /// 而不必真的清掉开发机上所有闲置的卷。
    /// </remarks>
    public string? Label { get; init; }
}
