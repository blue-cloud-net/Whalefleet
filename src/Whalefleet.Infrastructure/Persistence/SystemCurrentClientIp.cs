using Whalefleet.Application.Abstractions;

namespace Whalefleet.Infrastructure.Persistence;

/// <summary>
/// 「无请求上下文」的客户端 IP 占位实现。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：审计写入器依赖 <see cref="ICurrentClientIp"/>，而启动引导、
/// 后台任务这类场景没有 HTTP 请求。没有占位实现时这些代码路径会在解析 DI 时失败。</para>
/// <para><b>返回 <see langword="null"/> 而不是 "unknown"</b>：真值缺失就是缺失，
/// 编一个占位值会让审计里出现一堆「来自 unknown 的操作」，
/// 而它们其实「根本没有来源 IP」——两者在现场处置时是不同的事实。</para>
/// <para><b>Api 层会替换本注册</b>（<c>Replace</c>），与 <see cref="SystemCurrentUser"/> 同一做法。</para>
/// </remarks>
internal sealed class SystemCurrentClientIp : ICurrentClientIp
{
    /// <inheritdoc />
    public string? Ip => null;
}
