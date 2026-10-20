using Whalefleet.Api.Security;
using Whalefleet.Application.Common;
using Whalefleet.Application.Registries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 私有 Registry 配置端点（**全局作用域**，需 <c>registry.read</c> / <c>registry.write</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么是全局作用域而不是主机作用域</b>：一条 Registry 配置在所有主机上通用
/// （同一个 Harbor，推到哪台机器都用它）。挂到主机下会让同一份凭据在 N 台主机上重复登记 N 遍，
/// 而 N 份凭据迟早会漂移。</para>
/// <para><b>⚠️ 这里没有 <c>[Authorize(Roles = "admin")]</c></b>：角色声明不参与授权判定，
/// 策略由 <c>PermissionAuthorizationHandler</c> 查库实现，因此权限改动<b>立即生效</b>。</para>
/// <para><b>响应里绝不含口令</b>：连密文都不给（见 <see cref="RegistrySummary"/>）。
/// 界面要改口令时重新输入，而不是留空表示不修改。</para>
/// <para><b>失败路径不在这里处理</b>：配置不存在 <c>1800</c>、别名冲突 <c>1801</c>、
/// 地址冲突 <c>1802</c>、认证被拒 <c>1804</c>、不可达 <c>1805</c> 一律由
/// <see cref="RegistryService"/> 抛 <see cref="BusinessException"/>。</para>
/// </remarks>
[ApiController]
[Route("api/v1/admin/registries")]
[Authorize]
public sealed class AdminRegistriesController : ControllerBase
{
    private readonly RegistryService _registries;

    /// <summary>
    /// 初始化 Registry 配置控制器。
    /// </summary>
    /// <param name="registries">Registry 配置用例。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public AdminRegistriesController(RegistryService registries)
    {
        ArgumentNullException.ThrowIfNull(registries);

        _registries = registries;
    }

    /// <summary>
    /// 列出 Registry 配置（<b>不含任何口令信息</b>）。
    /// </summary>
    /// <param name="request">查询请求（可按别名 / 地址模糊过滤）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为配置列表。</returns>
    /// <response code="200">查询成功（无配置时返回空数组）。</response>
    /// <response code="401">未认证（<c>1100</c> / <c>1101</c>）。</response>
    /// <response code="403">缺少 <c>registry.read</c>（<c>1500</c>）。</response>
    [HttpPost("list")]
    [Authorize(Policy = PermissionPolicies.RegistriesRead)]
    [ProducesResponseType(typeof(ApiResult<IReadOnlyList<RegistrySummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAsync(
        [FromBody] ListRegistriesRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await _registries.ListAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 新增一条 Registry 配置（口令<b>加密后</b>落库，响应不回显）。
    /// </summary>
    /// <param name="request">新增请求（别名、地址、账号、口令）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为新建的配置（不含口令）。</returns>
    /// <response code="200">创建成功。</response>
    /// <response code="400">
    /// 参数非法（<c>1000</c>）、别名已占用（<c>1801</c>）或地址已占用（<c>1802</c>）。
    /// </response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>registry.write</c>（<c>1500</c>）。</response>
    [HttpPost("add")]
    [Authorize(Policy = PermissionPolicies.RegistriesWrite)]
    [ProducesResponseType(typeof(ApiResult<RegistrySummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddAsync(
        [FromBody] AddRegistryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _registries.AddAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }

    /// <summary>
    /// 删除一条 Registry 配置（软删除）。
    /// </summary>
    /// <param name="request">删除请求（配置主键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，无 <c>data</c>。</returns>
    /// <response code="200">删除成功。</response>
    /// <response code="400">主键为空（<c>1000</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>registry.write</c>（<c>1500</c>）。</response>
    /// <response code="404">配置不存在（<c>1800</c>）。</response>
    [HttpPost("remove")]
    [Authorize(Policy = PermissionPolicies.RegistriesWrite)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(
        [FromBody] RemoveRegistryRequest request,
        CancellationToken cancellationToken)
    {
        await _registries.DeleteAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok());
    }

    /// <summary>
    /// 测试 Registry 的连通性与凭据。
    /// </summary>
    /// <param name="request">
    /// 测试请求：给 <c>id</c> 则测试已保存的配置（口令自动解密），
    /// 否则测试请求体里的地址 + 凭据（测试尚未保存的配置）。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一响应，<c>data</c> 为测试结果。</returns>
    /// <response code="200">测试成功（已连通）。</response>
    /// <response code="400">参数非法（<c>1000</c>）或凭据被拒（<c>1804</c>）。</response>
    /// <response code="401">未认证。</response>
    /// <response code="403">缺少 <c>registry.write</c>（<c>1500</c>）。</response>
    /// <response code="404">指定的配置不存在（<c>1800</c>）。</response>
    /// <response code="502">Registry 不可达（<c>1805</c>）。</response>
    /// <remarks>
    /// <b>为什么用 <c>registry.write</c> 而不是 <c>registry.read</c></b>：
    /// 测试传的是<b>凭据</b>（账号与口令），能用凭据意味着能读出它。
    /// 把这道门开给 <c>registry.read</c> 等于让只读角色拿到凭据的「试读」能力——
    /// 而探测接口恰恰能把口令送到任意地址上去（攻击者把地址改成自己的服务器，
    /// 就能把口令截走）。
    /// </remarks>
    [HttpPost("test")]
    [Authorize(Policy = PermissionPolicies.RegistriesWrite)]
    [ProducesResponseType(typeof(ApiResult<RegistryTestResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResult), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> TestAsync(
        [FromBody] TestRegistryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _registries.TestAsync(request, cancellationToken).ConfigureAwait(false);

        return this.Ok(ApiResults.Ok(result));
    }
}
