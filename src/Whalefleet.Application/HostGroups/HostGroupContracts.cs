namespace Whalefleet.Application.HostGroups;

/// <summary>
/// 主机分组的对外契约（请求与响应）。
/// </summary>
/// <remarks>
/// <b>字段一律可空</b>：请求体由客户端决定，缺字段是常态而非异常。若声明为非空类型，
/// 「没传 name」会表现为空引用异常（500），而正确语义是参数校验失败（1000）。
/// 校验统一在 <see cref="HostGroupService"/> 里做。
/// </remarks>
public sealed record HostGroupSummary(
    Guid Id,
    string Name,
    string? Description,
    int SortOrder,
    int HostCount);

/// <summary>
/// 创建分组的请求契约。
/// </summary>
public sealed record CreateHostGroupRequest
{
    /// <summary>分组名。</summary>
    public string? Name { get; init; }

    /// <summary>描述；省略或留空表示不填。</summary>
    public string? Description { get; init; }

    /// <summary>排序权重；省略时按 0 处理（与内置分组 10/20/30 相比会排在最前）。</summary>
    public int? SortOrder { get; init; }
}

/// <summary>
/// 更新分组的请求契约。
/// </summary>
/// <remarks>
/// <para><b>未提供表示不改</b>：<see cref="Name"/> / <see cref="SortOrder"/> 为 <see langword="null"/>
/// 时保持原值。</para>
/// <para><b>描述是个例外</b>：<see cref="Description"/> 传空串表示**清空**。
/// 凭据那边需要额外的 <c>clearCredentials</c> 开关（JSON 分不清「没给」与「给了 null」），
/// 这里不需要——描述为空是无害的，用一个空串表达「清空」既够用又少一个字段。</para>
/// </remarks>
public sealed record UpdateHostGroupRequest
{
    /// <summary>新的分组名；未提供表示不改。</summary>
    public string? Name { get; init; }

    /// <summary>新的描述；未提供表示不改，空串表示清空。</summary>
    public string? Description { get; init; }

    /// <summary>新的排序权重；未提供表示不改。</summary>
    public int? SortOrder { get; init; }
}

/// <summary>
/// 调整主机归属的请求契约。
/// </summary>
/// <remarks>
/// 单设端点而不是塞进 <c>UpdateHostRequest</c>：那边的语义是「未提供表示不改」，
/// 于是「移到未分组」就没有表达方式。这里 <see cref="GroupId"/> 显式支持 <see langword="null"/>，
/// 语义因此只有一个：「把主机设为这个组（或不属于任何组）」。
/// </remarks>
public sealed record AssignHostGroupRequest
{
    /// <summary>目标分组主键；<see langword="null"/> 表示移出分组。</summary>
    public Guid? GroupId { get; init; }
}
