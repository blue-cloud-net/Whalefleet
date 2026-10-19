namespace Whalefleet.Application.Volumes;

/// <summary>
/// 数据卷列表请求。
/// </summary>
/// <remarks>
/// <b>刻意为空</b>：列表端点不接受请求体（见 <see cref="VolumeListResult"/> 的分页说明）。
/// 留一个空类型而不是直接删掉，是为了让控制器与前端的接口签名保持可读，
/// 也为 <c>0.16.0</c> 接入筛选参数时不必改路由或方法名。
/// </remarks>
public sealed record ListVolumesRequest
{
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
