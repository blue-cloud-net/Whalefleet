using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Api.Security;
using Whalefleet.Application.Alerting;
using Whalefleet.Application.Common;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 告警规则端点（<b>全局作用域</b>，需要 <c>alert.read</c> / <c>alert.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>请求体里没有 hostId</b>：主机范围用主机名 <c>host</c> 表达，
/// 与权限分配请求同一口径（主机主键不进请求体）。</para>
/// <para><b>写操作单列 <c>alert.write</c></b>：只读角色可以看阈值，但不能改。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/alert-rules")]
[Authorize]
public sealed class AlertRulesController : ControllerBase
{
    private readonly AlertRuleService _rules;

    /// <summary>
    /// 初始化控制器。
    /// </summary>
    public AlertRulesController(AlertRuleService rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        _rules = rules;
    }

    /// <summary>
    /// 列出规则（全局 + 指定主机的规则）。
    /// </summary>
    /// <param name="host">主机名；省略表示只看全局规则。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.AlertsRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<AlertRuleDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(
        [FromQuery] string? host,
        CancellationToken cancellationToken)
    {
        var result = await _rules.ListAsync(host, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 新建规则。
    /// </summary>
    [HttpPost("create")]
    [Authorize(Policy = PermissionPolicies.AlertsWrite)]
    [ProducesResponseType(typeof(ApiResult<AlertRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateAlertRuleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _rules.CreateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 更新规则。
    /// </summary>
    [HttpPost("update")]
    [Authorize(Policy = PermissionPolicies.AlertsWrite)]
    [ProducesResponseType(typeof(ApiResult<AlertRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] UpdateAlertRuleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _rules.UpdateAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 启用 / 停用规则。
    /// </summary>
    [HttpPost("set-enabled")]
    [Authorize(Policy = PermissionPolicies.AlertsWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetEnabledAsync(
        [FromBody] SetAlertRuleEnabledRequest request,
        CancellationToken cancellationToken)
    {
        await _rules.SetEnabledAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 删除规则。
    /// </summary>
    [HttpPost("remove")]
    [Authorize(Policy = PermissionPolicies.AlertsWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        [FromBody] RemoveAlertRuleRequest request,
        CancellationToken cancellationToken)
    {
        await _rules.RemoveAsync(request.Id, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }
}
