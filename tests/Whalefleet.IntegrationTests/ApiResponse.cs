using System.Text.Json;

namespace Whalefleet.IntegrationTests;

/// <summary>
/// 统一响应格式的测试侧反序列化模型（对应 <c>docs/api-spec.md</c> 的「统一响应格式」）。
/// </summary>
/// <remarks>
/// 刻意独立于 <c>Whalefleet.Application.Common.ApiResult</c> 定义：
/// 若复用生产类型，字段改名会被两边同时改掉而测试依然"通过"，
/// 无法起到保护公开契约的作用。
/// </remarks>
/// <param name="Success">请求是否成功。</param>
/// <param name="Code">业务错误码。</param>
/// <param name="Message">响应消息。</param>
/// <param name="Data">返回数据主体；无数据时为 <c>null</c>。</param>
internal sealed record ApiResponse(
    bool Success,
    int Code,
    string Message,
    JsonElement? Data);
