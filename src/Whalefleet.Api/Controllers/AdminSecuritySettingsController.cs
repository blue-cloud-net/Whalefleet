using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Api.Security;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 实例级安全策略端点（0.13.0）：密码强度、有效期与首次登录强制改密。
/// </summary>
/// <remarks>
/// <para><b>权限</b>：<c>user.write</c>。它是<b>全局</b>设置（一份管所有人），
/// 挂在用户管理面上是因为它直接决定「谁能建账号、账号好不好用」。</para>
/// <para><b>为什么单独一个控制器而不是塞进 <c>AdminUsersController</c></b>：
/// 它管的不是某个用户，而是<b>实例</b>。与 <c>AuthorizationSettingsController</c>（容器授权策略）
/// 保持同一形态——两者都是「全局策略」，各占一个控制器。</para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v1/admin/settings/security")]
public sealed class AdminSecuritySettingsController : ControllerBase
{
    private readonly ISecuritySettingsStore _settings;
    private readonly IAuditLogger _audit;

    /// <summary>
    /// 初始化安全策略控制器。
    /// </summary>
    /// <param name="settings">安全策略存储。</param>
    /// <param name="audit">审计日志（策略变更必须留痕）。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public AdminSecuritySettingsController(
        ISecuritySettingsStore settings,
        IAuditLogger audit)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(audit);

        _settings = settings;
        _audit = audit;
    }

    /// <summary>
    /// 读取安全策略。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为当前策略。</returns>
    /// <response code="200">查询成功。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>user.write</c>（<c>1500</c>）。</response>
    [HttpPost("get")]
    [Authorize(Policy = PermissionPolicies.UsersWrite)]
    [ProducesResponseType(typeof(ApiResult<SecurityPolicyOptions>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        var options = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(options));
    }

    /// <summary>
    /// 保存安全策略。
    /// </summary>
    /// <param name="request">策略参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为保存后的策略。</returns>
    /// <response code="200">保存成功。</response>
    /// <response code="400">参数非法（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>user.write</c>（<c>1500</c>）。</response>
    /// <remarks>
    /// <b>只影响之后发生的动作</b>：已有账号的密码不会被追溯校验，
    /// 已有账号的「必须改密」标记也不会被批量置位——
    /// 否则一次调参就会把所有人锁在门外。
    /// </remarks>
    [HttpPost("update")]
    [Authorize(Policy = PermissionPolicies.UsersWrite)]
    [ProducesResponseType(typeof(ApiResult<SecurityPolicyOptions>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] SecurityPolicyOptions request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IsValid())
        {
            throw BusinessException.Validation("安全策略参数非法：最小长度需在 1-1024 之间，有效期不能为负");
        }

        await _settings.SetAsync(request, cancellationToken).ConfigureAwait(false);

        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.SecuritySettingsUpdate,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.SecuritySettings,
                TargetId = "1",
                Detail = $"最小长度={request.MinLength};有效期={request.PasswordExpiryDays}天;首次改密={request.MustChangeOnFirstLogin}",
            },
            cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(request));
    }
}
