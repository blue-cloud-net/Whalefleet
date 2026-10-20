namespace Whalefleet.Application.Repositories;

/// <summary>
/// 列出 仓库配置的请求。
/// </summary>
public sealed record ListRepositoriesRequest
{
    /// <summary>按别名或地址模糊过滤（不区分大小写）；省略不过滤。</summary>
    public string? Search { get; init; }
}

/// <summary>
/// 新增一条 仓库配置的请求。
/// </summary>
public sealed record AddRepositoryRequest
{
    /// <summary>别名（管理员可读的名字）；必填。</summary>
    public string? Name { get; init; }

    /// <summary>服务地址（<c>host</c> 或 <c>host:port</c>）；必填。</summary>
    public string? ServerAddress { get; init; }

    /// <summary>登录账号；为空表示匿名访问。</summary>
    public string? Username { get; init; }

    /// <summary>登录口令；与 <see cref="Username"/> 要么都给、要么都不给。</summary>
    public string? Password { get; init; }

    /// <summary>备注；可为空。</summary>
    public string? Description { get; init; }
}

/// <summary>
/// 删除一条 仓库配置的请求。
/// </summary>
public sealed record RemoveRepositoryRequest
{
    /// <summary>配置主键；必填。</summary>
    public Guid? Id { get; init; }
}

/// <summary>
/// 测试 仓库连通性的请求。
/// </summary>
public sealed record TestRepositoryRequest
{
    /// <summary>已保存的配置主键；给了就测试这条已保存的配置。</summary>
    public Guid? Id { get; init; }

    /// <summary>服务地址；未给 <see cref="Id"/> 时必填（测试一条尚未保存的配置）。</summary>
    public string? ServerAddress { get; init; }

    /// <summary>登录账号；可为空（匿名测试）。</summary>
    public string? Username { get; init; }

    /// <summary>登录口令；可为空。</summary>
    public string? Password { get; init; }
}
