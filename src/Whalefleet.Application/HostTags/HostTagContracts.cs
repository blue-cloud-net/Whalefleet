namespace Whalefleet.Application.HostTags;

/// <summary>
/// 主机标签的对外契约（管理页用）。
/// </summary>
/// <param name="Id">标签主键（只用于接口路径，不进主机 URL）。</param>
/// <param name="Name">标签名。</param>
/// <param name="Description">描述；未填写为 <see langword="null"/>。</param>
/// <param name="Color">颜色（<c>#RRGGBB</c>）；未设置为 <see langword="null"/>（界面用默认色）。</param>
/// <param name="HostCount">挂载该标签的主机数。</param>
/// <remarks>
/// <b>字段一律可空是请求侧的事</b>：响应侧的 <see cref="Name"/> 恒有值——
/// 库里的标签名有 NOT NULL 约束，给 <see langword="null"/> 只会让前端多写一个分支。
/// </remarks>
public sealed record HostTagSummary(
    Guid Id,
    string Name,
    string? Description,
    string? Color,
    int HostCount);

/// <summary>
/// 挂在某台主机身上的标签（主机列表 / 概览用）。
/// </summary>
/// <param name="Id">标签主键。</param>
/// <param name="Name">标签名。</param>
/// <param name="Color">颜色（<c>#RRGGBB</c>）；未设置为 <see langword="null"/>。</param>
/// <remarks>
/// <para><b>为什么不复用 <see cref="HostTagSummary"/></b>：主机列表每次刷新都要带上标签，
/// 而描述与主机数在列表里没有用处——多传两段文本 × N 台主机 × 每几秒一次，
/// 换来的只是「类型统一」这一个好处。</para>
/// </remarks>
public sealed record HostTagRef(Guid Id, string Name, string? Color);

/// <summary>
/// 创建标签的请求契约。
/// </summary>
public sealed record CreateHostTagRequest
{
    /// <summary>标签名。</summary>
    public string? Name { get; init; }

    /// <summary>描述；省略或留空表示不填。</summary>
    public string? Description { get; init; }

    /// <summary>颜色（<c>#RRGGBB</c>）；省略或留空表示用默认色。</summary>
    public string? Color { get; init; }
}

/// <summary>
/// 更新标签的请求契约。
/// </summary>
/// <remarks>
/// <para><b>未提供表示不改</b>：<see cref="Name"/> 为 <see langword="null"/> 时保持原值。</para>
/// <para><b>描述与颜色传空串表示清空</b>：与分组那边同一套口径
/// （见 <c>UpdateHostGroupRequest</c> 的说明）——二者为空都是无害的，
/// 不必为此再加一个 <c>clearColor</c> 开关。</para>
/// </remarks>
public sealed record UpdateHostTagRequest
{
    /// <summary>新的标签名；未提供表示不改。</summary>
    public string? Name { get; init; }

    /// <summary>新的描述；未提供表示不改，空串表示清空。</summary>
    public string? Description { get; init; }

    /// <summary>新的颜色；未提供表示不改，空串表示清空。</summary>
    public string? Color { get; init; }
}

/// <summary>
/// 批量打标 / 清标的请求契约。
/// </summary>
/// <remarks>
/// <para><b><see cref="HostIds"/> 是主机名而不是主键</b>：与 <c>hosts/{hostId}</c>
/// 路径里的 <c>hostId</c> 同口径——前端手里只有主机名，主键只在服务端内部流转。</para>
/// <para><b>两个端点共用同一个形状</b>：打标与清标只差一个动词，
/// 分开定义两个几乎相同的 DTO 只会让前端多写一次映射。</para>
/// <para><b>幂等</b>：重复打同一个标签、重复清同一个标签都不报错——
/// 「全选 + 批量打标」必然包含已经打过的那些。</para>
/// </remarks>
public sealed record HostTagAssignmentRequest
{
    /// <summary>主机名列表。</summary>
    public IReadOnlyList<string>? HostIds { get; init; }

    /// <summary>标签主键列表。</summary>
    public IReadOnlyList<string>? TagIds { get; init; }
}
