using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;

namespace Whalefleet.Api.Controllers;

/// <summary>仅内置全局管理员可管理实例级授权策略。</summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/settings/authorization")]
public sealed class AuthorizationSettingsController(
    IAuthorizationSettingsStore settings,
    IPermissionChecker permissions,
    IAuditLogger audit) : ControllerBase
{
    /// <summary>读取实例级授权模式。</summary>
    [HttpPost("get")]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        await this.EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        return this.Ok(ApiResults.Ok(new AuthorizationSettingsRequest(
            await settings.GetModeAsync(cancellationToken).ConfigureAwait(false))));
    }

    /// <summary>保存模式并记录安全审计。</summary>
    [HttpPost("update")]
    public async Task<IActionResult> UpdateAsync([FromBody] AuthorizationSettingsRequest request, CancellationToken cancellationToken)
    {
        await this.EnsureAdminAsync(cancellationToken).ConfigureAwait(false);
        if (request.ContainerPermissionMode is not ("inheritHost" or "requireAssignment"))
            throw BusinessException.Validation("未知的容器授权模式");
        await settings.SetModeAsync(request.ContainerPermissionMode, cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync(new AuditLogDraft
        {
            Category = AuditCategory.Activity,
            Action = "authorization.settings.update",
            Outcome = AuditOutcome.Success,
            TargetType = "authorizationSettings",
            TargetId = "1",
            Detail = request.ContainerPermissionMode,
        }, cancellationToken).ConfigureAwait(false);
        return this.Ok(ApiResults.Ok(request));
    }

    private async Task EnsureAdminAsync(CancellationToken cancellationToken)
    {
        if (!await permissions.IsAdministratorAsync(cancellationToken).ConfigureAwait(false))
            throw new BusinessException(ErrorCodes.OperationForbidden, "仅全局管理员可管理授权策略");
    }
}

/// <summary>实例级容器授权策略。</summary>
/// <param name="ContainerPermissionMode">inheritHost 或 requireAssignment。</param>
public sealed record AuthorizationSettingsRequest(string ContainerPermissionMode);
