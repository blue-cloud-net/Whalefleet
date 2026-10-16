using Whalefleet.Application.Abstractions;

namespace Whalefleet.Infrastructure.Persistence;

/// <summary>
/// 「无登录用户」的占位实现。
/// </summary>
/// <remarks>
/// <para><b>存在意义</b>：0.0.3 尚无认证，但仓储的审计填充需要 <see cref="ICurrentUser"/>。
/// 本实现让审计字段落到「默认值哨兵」<see cref="Guid.Empty"/>（= 系统操作），
/// 于是启动引导、种子数据这类无用户上下文写入的审计语义是正确的。</para>
/// <para><b>0.0.4 会被替换</b>：认证落地后由 Api 层基于 <c>HttpContext</c> + JWT claims
/// 的实现接管（通过 <c>IServiceCollection.Replace</c> 覆盖本默认注册）。</para>
/// </remarks>
internal sealed class SystemCurrentUser : ICurrentUser
{
    /// <inheritdoc />
    public bool IsAuthenticated => false;

    /// <inheritdoc />
    public Guid? UserId => null;

    /// <inheritdoc />
    public string? Username => null;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => [];

}
