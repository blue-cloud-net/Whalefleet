using Docker.Manager.Application.Common;
using Docker.Manager.Application.HostGroups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docker.Manager.Api.Controllers;

/// <summary>
/// 主机分组端点（**全局作用域**）。
/// </summary>
/// <remarks>
/// <para><b>为什么列表端点对所有已登录用户开放</b>：总览页按分组聚合展示，
/// 而总览页对**所有角色**可见（非管理员只是看不到主机，而不是看不到页面）。
/// 若把它也塞进 admin 端点，非 admin 的总览页会整块报错——与
/// 「主机查询 / 主机管理」分成两组端点是同一个原因。</para>
/// <para><b>分组不承担授权</b>：这里只返回展示信息，「谁能操作哪台主机」由 0.3.0 的
/// 资源权限表回答。让分组承担授权会使「把主机挪进某组」变成一次隐式提权。</para>
/// </remarks>
[ApiController]
[Route("api/v1/host-groups")]
[Authorize]
public sealed class HostGroupsController : ControllerBase
{
    private readonly HostGroupService _groups;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    /// <param name="groups">分组用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> 为 <see langword="null"/>。</exception>
    public HostGroupsController(HostGroupService groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        _groups = groups;
    }

    /// <summary>
    /// 列出全部分组（含每组主机数）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为分组列表。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<HostGroupSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var groups = await _groups.ListAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(groups));
    }
}
