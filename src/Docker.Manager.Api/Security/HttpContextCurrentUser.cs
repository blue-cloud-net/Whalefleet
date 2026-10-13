using System.Security.Claims;
using Docker.Manager.Application.Abstractions;

namespace Docker.Manager.Api.Security;

/// <summary>
/// 基于 <see cref="HttpContext"/> 的「当前登录用户」实现。
/// </summary>
/// <remarks>
/// <para><b>它替换掉 Infrastructure 的占位实现</b>（<c>SystemCurrentUser</c>）：占位实现恒为
/// 「未认证」，于是仓储审计字段落到默认值哨兵 <see cref="Guid.Empty"/>，表示系统操作；
/// 认证落地后，有登录用户的请求必须写入真实用户主键。</para>
/// <para><b>为什么实现在 Api 层</b>：它是「当前 HTTP 请求的登录用户」这一 Web 概念的适配器。
/// <c>Application</c> 只保留接口，<c>Infrastructure</c> 保留「无请求上下文」的占位实现，
/// 供启动引导与后台维护任务使用。</para>
/// <para><b>声明名走 <see cref="JwtClaimNames"/></b>：Api 层已关闭入站声明映射
/// （<c>MapInboundClaims = false</c>），因此这里读到的就是令牌里的原始短名，
/// 不必也不应依赖 <c>ClaimTypes.*</c> 的映射结果。</para>
/// <para><b>无请求上下文时</b>（后台任务、单元测试）<see cref="HttpContextAccessor.HttpContext"/>
/// 为 <see langword="null"/>，本实现按「未认证」返回——与占位实现语义一致。</para>
/// </remarks>
internal sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// 初始化当前用户实现。
    /// </summary>
    /// <param name="httpContextAccessor">HTTP 上下文访问器。</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpContextAccessor"/> 为 <see langword="null"/>。</exception>
    public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);

        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public bool IsAuthenticated => this.Principal?.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public Guid? UserId =>
        Guid.TryParse(this.FindClaim(JwtClaimNames.Subject), out var userId) ? userId : null;

    /// <inheritdoc />
    public string? Username => this.FindClaim(JwtClaimNames.UniqueName);

    /// <inheritdoc />
    public string? Role => this.FindClaim(JwtClaimNames.Role);

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    private string? FindClaim(string claimType) => this.Principal?.FindFirst(claimType)?.Value;
}
