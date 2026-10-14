using Whalefleet.Application.Common;

namespace Whalefleet.Api.Development;

/// <summary>
/// 开发专用故障注入。
/// </summary>
/// <remarks>
/// <para>存在的唯一理由：0.0.2 阶段系统尚无数据库与外部依赖，<c>/healthz</c> 与后续端点在正常情况下
/// 不会抛异常，导致「异常 → 统一 <c>ApiResult</c> 错误响应」这一验收项无法被触发。
/// 本类为每个异常分支提供一个可复现的入口，同时也被集成测试复用。</para>
/// <para><b>安全边界</b>：调用方（<c>Program.cs</c>）只在 <c>IsDevelopment()</c> 为真时注册对应端点，
/// 生产环境不存在该路由。<b>禁止</b>把本类用于生产环境的任何路径。</para>
/// </remarks>
internal static class DevelopmentFaults
{
    /// <summary>兜底分支：未捕获的通用异常。</summary>
    public const string Internal = "internal";

    /// <summary>业务异常分支。</summary>
    public const string Business = "business";

    /// <summary>领域异常分支。</summary>
    public const string Domain = "domain";

    /// <summary>参数异常分支。</summary>
    public const string Validation = "validation";

    /// <summary>资源不存在分支。</summary>
    public const string NotFound = "notfound";

    /// <summary>超时分支。</summary>
    public const string Timeout = "timeout";

    /// <summary>
    /// 按 <paramref name="kind"/> 创建对应的故障异常。
    /// </summary>
    /// <param name="kind">
    /// 故障类型，取值见本类常量（<c>internal</c> / <c>business</c> /
    /// <c>validation</c> / <c>notfound</c> / <c>timeout</c>）；为空时按 <see cref="Internal"/> 处理。
    /// </param>
    /// <returns>用于触发的异常实例。</returns>
    public static Exception Create(string? kind) => kind switch
    {
        Business => BusinessException.NotFound("开发用故障注入：资源不存在"),

        Validation => new ArgumentException("开发用故障注入：参数不合法", nameof(kind)),

        NotFound => new KeyNotFoundException("开发用故障注入：键不存在"),

        Timeout => new TaskCanceledException("开发用故障注入：操作超时"),

        Internal or null or "" => new InvalidOperationException("开发用故障注入：未捕获异常"),

        // 未知 kind 用 4xx 业务异常返回，使其消息对调用者可见（9999 的消息会被中间件固定化）。
        _ => BusinessException.Validation(
            $"开发用故障注入：未知的 kind={kind}（可选值：internal / business / validation / notfound / timeout）"),
    };
}
