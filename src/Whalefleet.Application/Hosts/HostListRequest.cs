namespace Whalefleet.Application.Hosts;

/// <summary>
/// 主机列表的过滤条件（<c>POST /api/v1/hosts/list</c> 的可选请求体）。
/// </summary>
/// <remarks>
/// <para><b>为什么过滤放在服务端</b>：分组与标签是「选择与过滤的边界」（0.6.0 把分组从
/// 「展示信息」升级到这一层），而 <c>0.7.0</c> 的「按分组批量操作」必须复用同一套边界判定。
/// 若前端本地过滤，后端就没有任何可复用的过滤实现，到头来要写两份。</para>
/// <para><b>「未分组」只能靠空串表达</b>：<see cref="GroupId"/> 为 <see langword="null"/> 与
/// 「字段缺失」在 JSON 里<b>不可区分</b>（都是 <c>null</c>），因此约定：
/// 字段缺失 / <c>null</c> = <b>不过滤</b>，空串 = <b>只看未分组</b>。
/// 前端「未分组」那一项因此必须提交 <c>""</c> 而不是 <c>null</c>。
/// ——这与 <c>update</c> 端点需要显式的 <c>clearCredentials</c> 是同一个坑。</para>
/// <para><b>标签是「任一命中」</b>：勾两个标签时是 <c>A ∪ B</c>（而不是 <c>A ∩ B</c>）——
/// 多选过滤的直觉就是「这些都在我的关注范围内」；与 <see cref="GroupId"/> 之间才是 AND。</para>
/// <para><b>非法值一律 <c>1000</c></b>：不认识的 <c>status</c>、非 GUID 的 <c>groupId</c> 都直接报错，
/// 而不是「静默忽略、按不过滤处理」——后者会让用户以为过滤生效了，实际看到的是全量。</para>
/// </remarks>
public sealed record HostListRequest
{
    /// <summary>
    /// 分组过滤：缺失 / <see langword="null"/> = 不过滤；空串 = 只看未分组；否则按分组主键过滤。
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// 标签过滤（任一命中即可）；缺失 / 空数组 = 不过滤。
    /// </summary>
    public IReadOnlyList<string>? TagIds { get; init; }

    /// <summary>
    /// 连通状态过滤：<c>online</c> / <c>offline</c> / <c>unknown</c>；缺失 = 不过滤。
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// 名称 / 端点模糊匹配（不区分大小写）；缺失或空白 = 不过滤。
    /// </summary>
    public string? Search { get; init; }
}
