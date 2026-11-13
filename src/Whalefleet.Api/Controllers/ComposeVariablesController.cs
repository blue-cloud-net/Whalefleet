using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.Variables;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// Compose 项目级环境变量端点（<b>主机作用域</b>，读用 <c>compose.read</c>、写用 <c>compose.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>与全局变量的分工</b>：全局变量在 <c>/api/v1/admin/variables/*</c>，
/// 项目级变量在这里。两者同名时<b>项目级覆盖全局级</b>——
/// 与 Compose 自身「项目 .env 覆盖进程环境」的同一口径。</para>
/// <para><b>主机是否存在（<c>1300</c>）与主机可见性（<c>1501</c>）由
/// <c>CurrentHostMiddleware</c> 统一处理</b>，端点内部不再重复校验。</para>
/// <para><b>值永不返回</b>：同全局变量端点。</para>
/// </remarks>
[ApiController]
[Route("api/v1/hosts/{hostId}/composes/variables")]
[Authorize]
public sealed class ComposeVariablesController : ControllerBase
{
    private readonly EnvVariableService _variables;
    private readonly IDockerHostAccessor _accessor;
    private readonly ContainerAuthorizationService _authorization;

    /// <summary>
    /// 初始化项目级变量控制器。
    /// </summary>
    /// <param name="variables">变量用例。</param>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="authorization">权限判定。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ComposeVariablesController(
        EnvVariableService variables,
        IDockerHostAccessor accessor,
        ContainerAuthorizationService authorization)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(authorization);

        _variables = variables;
        _accessor = accessor;
        _authorization = authorization;
    }

    /// <summary>
    /// 列出某个 Compose 项目可见的变量。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（Compose 项目名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 统一响应，<c>data</c> 为项目级变量、继承的全局变量与未定义变量名。
    /// </returns>
    /// <response code="200">查询成功。</response>
    /// <response code="400">Compose 项目名为空（<c>1000</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.read</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("list")]
    [ProducesResponseType(typeof(ApiResult<ComposeVariablesResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListAsync(
        string hostId,
        [FromBody] ListComposeEnvVariablesRequest request,
        CancellationToken cancellationToken)
    {
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await _authorization
            .EnsureUnrestrictedAsync(hostId, "compose.read", cancellationToken)
            .ConfigureAwait(false);

        var variables = await _variables
            .ListForComposeAsync(host.Id, host.Name, RequireComposeName(request?.ComposeName), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(variables));
    }

    /// <summary>
    /// 新建某个 Compose 项目自己的变量。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（项目名、变量名、值与元数据）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新建的变量（<b>不含明文</b>）。</returns>
    /// <response code="200">新建成功。</response>
    /// <response code="400">变量名非法（<c>1909</c>）或名称已占用（<c>1914</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机不存在（<c>1300</c>）。</response>
    [HttpPost("create")]
    [ProducesResponseType(typeof(ApiResult<EnvVariableDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateAsync(
        string hostId,
        [FromBody] ComposeEnvVariableRequest request,
        CancellationToken cancellationToken)
    {
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await _authorization
            .EnsureUnrestrictedAsync(hostId, "compose.write", cancellationToken)
            .ConfigureAwait(false);

        var variable = await _variables
            .CreateForComposeAsync(host.Id, host.Name, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(variable));
    }

    /// <summary>
    /// 更新某个 Compose 项目自己的变量。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（项目名、变量名、可选的新值与元数据）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为更新后的变量（<b>不含明文</b>）。</returns>
    /// <response code="200">更新成功。</response>
    /// <response code="400">变量名非法（<c>1909</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或变量（<c>1913</c>）不存在。</response>
    [HttpPost("update")]
    [ProducesResponseType(typeof(ApiResult<EnvVariableDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(
        string hostId,
        [FromBody] ComposeEnvVariableRequest request,
        CancellationToken cancellationToken)
    {
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await _authorization
            .EnsureUnrestrictedAsync(hostId, "compose.write", cancellationToken)
            .ConfigureAwait(false);

        var variable = await _variables
            .UpdateForComposeAsync(host.Id, host.Name, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(variable));
    }

    /// <summary>
    /// 删除某个 Compose 项目自己的变量（软删除）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">请求（项目名、变量名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">变量名非法（<c>1909</c>）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">无权访问该主机（<c>1501</c>）或缺少 <c>compose.write</c>（<c>1502</c>）。</response>
    /// <response code="404">主机（<c>1300</c>）或变量（<c>1913</c>）不存在。</response>
    [HttpPost("remove")]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        string hostId,
        [FromBody] ComposeEnvVariableRequest request,
        CancellationToken cancellationToken)
    {
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await _authorization
            .EnsureUnrestrictedAsync(hostId, "compose.write", cancellationToken)
            .ConfigureAwait(false);

        await _variables
            .RemoveForComposeAsync(host.Id, host.Name, request, cancellationToken)
            .ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 校验并规范化 Compose 项目名。
    /// </summary>
    /// <param name="composeName">原始项目名。</param>
    /// <returns>规范化后的项目名。</returns>
    /// <exception cref="BusinessException">项目名为空（<c>1000</c>）。</exception>
    private static string RequireComposeName(string? composeName)
    {
        if (String.IsNullOrWhiteSpace(composeName))
        {
            throw BusinessException.Validation("Compose 项目名不能为空");
        }

        return composeName.Trim();
    }
}
