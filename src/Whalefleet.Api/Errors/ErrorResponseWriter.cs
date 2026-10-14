using System.Text.Json;
using Whalefleet.Api.Serialization;
using Whalefleet.Application.Common;

namespace Whalefleet.Api.Errors;

/// <summary>
/// 错误响应的统一序列化入口。
/// </summary>
/// <remarks>
/// <para>全局异常处理中间件与 <c>UseStatusCodePages</c> 处理器共用本类，确保所有错误响应
/// 的 JSON 形状完全一致（<c>docs/api-spec.md</c> 的「统一响应格式」）。</para>
/// <para>这里使用独立的 <see cref="JsonSerializerOptions"/> 而非注入 MVC 的 <c>JsonOptions</c>：
/// 本类只会写出固定的错误信封（<c>data</c> 恒为 <c>null</c>），不承载任何业务负载类型，
/// 因此不需要（也不应依赖）MVC 的自定义转换器，从而避免多一条失败路径。</para>
/// <para>但<b>编码策略必须与 MVC 一致</b>，故经由 <see cref="ApiJsonDefaults"/> 统一配置，
/// 避免出现「正常响应中文可读、错误响应却是 <c>\uXXXX</c>」的割裂。</para>
/// </remarks>
internal static class ErrorResponseWriter
{
    /// <summary>
    /// 错误响应统一使用的内容类型；显式声明 <c>charset</c>，避免中文在部分客户端上乱码。
    /// </summary>
    public const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// 将错误码与消息序列化为统一响应 JSON。
    /// </summary>
    /// <param name="code">业务错误码，见 <see cref="ErrorCodes"/>。</param>
    /// <param name="message">错误消息。</param>
    /// <returns>形如 <c>{"success":false,"code":9999,"message":"…","data":null}</c> 的 JSON 字符串。</returns>
    public static string Serialize(int code, string message) =>
        JsonSerializer.Serialize(ApiResults.Fail<object>(code, message), SerializerOptions);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApiJsonDefaults.Apply(options);
        return options;
    }
}
