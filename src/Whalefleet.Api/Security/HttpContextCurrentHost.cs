using Whalefleet.Application.Abstractions;

namespace Whalefleet.Api.Security;

/// <summary>
/// 当前主机上下文的请求级实现（Scoped，可读可写）。
/// </summary>
/// <remarks>
/// <para><b>为什么用字段而不是 <c>HttpContext.Items</c></b>：本类型是 Scoped，生命周期就等于
/// 一次请求，字段天然是请求级的；<c>Items</c> 还要处理键名冲突与类型转换，没有收益。</para>
/// <para><b>没有请求上下文时</b>（后台任务、单元测试中解析本服务）读取到的就是
/// <see langword="null"/> —— 与「无主机上下文」语义一致，不需要额外的占位实现。</para>
/// <para><b>它不读路由</b>：路由解析与主机校验统一放在 <c>CurrentHostMiddleware</c> 里，
/// 只有一处决定「当前主机是谁」。本类型只做存储。</para>
/// </remarks>
internal sealed class HttpContextCurrentHost : ICurrentHost, ICurrentHostAccessor
{
    private string? _hostId;

    /// <inheritdoc />
    public string? HostId => _hostId;

    /// <inheritdoc />
    public void Set(string hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        _hostId = hostId;
    }
}
