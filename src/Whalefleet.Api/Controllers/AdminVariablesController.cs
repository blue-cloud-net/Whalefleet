using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Variables;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 全局环境变量端点（<b>全局作用域</b>，需要 <c>variable.read</c> / <c>variable.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么是独立的全局端点，而不是挂在 Compose 项目下面</b>：
/// 全局变量对所有主机、所有项目生效，它的作用面是<b>整个实例</b>——
/// 挂在某个主机路径下会让「它到底影响多大」这件事变得含糊。</para>
/// <para><b>值永不返回</b>：响应里只有掩码与「有没有值」。
/// 管理接口能读到明文，就意味着明文会进浏览器的网络面板、
/// 会进前端的状态管理库、会进浏览器的历史记录。</para>
/// <para><b>写操作单列 <c>variable.write</c></b>：只读角色可以看清单，但不能改。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/variables")]
[Authorize]
public sealed class AdminVariablesController : ControllerBase
{
    private readonly EnvVariableService _variables;

    /// <summary>
    /// 初始化全局变量控制器。
    /// </summary>
    /// <param name="variables">变量用例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="variables"/> 为 <see langword="null"/>。</exception>
    public AdminVariablesController(EnvVariableService variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        _variables = variables;
    }

    /// <summary>
    /// 列出全部全局变量。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为变量清单（<b>不含明文</b>）。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">缺少 <c>variable.read</c>（<c>1502</c>）。</response>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.VariablesRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<EnvVariableDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(CancellationToken cancellationToken)
    {
        var variables = await _variables.ListGlobalAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(variables));
    }

    /// <summary>
    /// 新建全局变量。
    /// </summary>
    /// <param name="request">请求（名称、值、密钥标记、Webhook 覆盖许可、备注）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新建的变量（<b>不含明文</b>）。</returns>
    /// <response code="200">新建成功。</response>
    /// <response code="400">变量名非法（<c>1909</c>）或名称已占用（<c>1914</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">缺少 <c>variable.write</c>（<c>1502</c>）。</response>
    [HttpPost("create")]
    [Authorize(Policy = PermissionPolicies.VariablesWrite)]
    [ProducesResponseType(typeof(ApiResult<EnvVariableDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateEnvVariableRequest request,
        CancellationToken cancellationToken)
    {
        var variable = await _variables.CreateGlobalAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(variable));
    }

    /// <summary>
    /// 更新全局变量。
    /// </summary>
    /// <param name="request">请求（名称、可选的新值与元数据、是否清空值）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为更新后的变量（<b>不含明文</b>）。</returns>
    /// <response code="200">更新成功。</response>
    /// <response code="400">变量名非法（<c>1909</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">缺少 <c>variable.write</c>（<c>1502</c>）。</response>
    /// <response code="404">变量不存在（<c>1913</c>）。</response>
    [HttpPost("update")]
    [Authorize(Policy = PermissionPolicies.VariablesWrite)]
    [ProducesResponseType(typeof(ApiResult<EnvVariableDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] UpdateEnvVariableRequest request,
        CancellationToken cancellationToken)
    {
        var variable = await _variables.UpdateGlobalAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(variable));
    }

    /// <summary>
    /// 删除全局变量（软删除）。
    /// </summary>
    /// <param name="request">请求（变量名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">变量名非法（<c>1909</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">缺少 <c>variable.write</c>（<c>1502</c>）。</response>
    /// <response code="404">变量不存在（<c>1913</c>）。</response>
    [HttpPost("remove")]
    [Authorize(Policy = PermissionPolicies.VariablesWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        [FromBody] RemoveEnvVariableRequest request,
        CancellationToken cancellationToken)
    {
        await _variables.RemoveGlobalAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
