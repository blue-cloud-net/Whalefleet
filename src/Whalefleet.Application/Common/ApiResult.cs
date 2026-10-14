namespace Whalefleet.Application.Common;

/// <summary>
/// 统一响应模型（无数据版本），用于不返回 <c>data</c> 的接口。
/// </summary>
/// <remarks>
/// <para>RPC 风格 API 的所有响应均包装为本结构；字段名与顺序遵循 <c>docs/api-spec.md</c>
/// 的「统一响应格式」，序列化为 camelCase（<c>success</c> / <c>code</c> / <c>message</c>）。</para>
/// <para>请通过 <see cref="ApiResults"/> 的工厂方法构造实例，而不是直接调用构造函数，
/// 以保证 <c>Success</c> 与 <c>Code</c> 始终自洽。工厂方法刻意放在非泛型静态类中，
/// 以规避 CA1000（不要在泛型类型中声明静态成员）。</para>
/// </remarks>
/// <param name="Success">是否成功。</param>
/// <param name="Code">业务错误码，见 <see cref="ErrorCodes"/>。</param>
/// <param name="Message">响应消息（成功时固定为 <c>ok</c>）。</param>
public record ApiResult(bool Success, int Code, string Message);

/// <summary>
/// 统一响应模型（泛型版本），携带业务数据 <typeparamref name="T"/>。
/// </summary>
/// <remarks>
/// <para>序列化时<b>不忽略</b> <c>null</c>：失败响应必须显式输出 <c>"data": null</c>，
/// 与 <c>docs/api-spec.md</c> 的响应示例保持一致。因此请勿在 <c>Program.cs</c> 中
/// 为其开启 <c>DefaultIgnoreCondition = WhenWritingNull</c>。</para>
/// <para>无数据可返回的接口请使用非泛型 <see cref="ApiResult"/>；构造入口统一见 <see cref="ApiResults"/>。</para>
/// </remarks>
/// <typeparam name="T">业务数据类型。</typeparam>
/// <param name="Success">是否成功。</param>
/// <param name="Code">业务错误码，见 <see cref="ErrorCodes"/>。</param>
/// <param name="Message">响应消息（成功时固定为 <c>ok</c>）。</param>
/// <param name="Data">业务数据（失败时为 <c>null</c>）。</param>
public record ApiResult<T>(bool Success, int Code, string Message, T? Data = default);