using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Whalefleet.Api.Serialization;

/// <summary>
/// API JSON 序列化的统一默认值。
/// </summary>
/// <remarks>
/// <para><b>唯一入口</b>：MVC（<c>AddJsonOptions</c>）与错误响应序列化器
/// （<see cref="Errors.ErrorResponseWriter"/>）都必须经由此处配置，
/// 否则两条路径的编码策略会各自漂移——正常响应可读、错误响应却是一堆 <c>\uXXXX</c>。</para>
/// <para><b>编码策略</b>：使用 <see cref="JavaScriptEncoder.Create(UnicodeRanges.All)"/>。</para>
/// <list type="bullet">
///   <item><description>中文等非 ASCII 字符**原样输出**，不再转义为 <c>\uXXXX</c>——可读性更好、报文更小，
///   且与 <c>docs/api-spec.md</c> 的示例一致。</description></item>
///   <item><description>仍然保留 JavaScript 编码器对 <c>&lt; &gt; &amp; ' " +</c> 等 HTML 敏感字符的转义。
///   这是与 <c>UnsafeRelaxedJsonEscaping</c> 的关键区别：即便响应被错误地嵌入 HTML，
///   也不会引入 XSS 风险。</description></item>
/// </list>
/// </remarks>
internal static class ApiJsonDefaults
{
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <summary>
    /// 将 API 的 JSON 序列化默认值应用到指定选项实例。
    /// </summary>
    /// <param name="options">待配置的序列化选项。</param>
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Encoder = Encoder;

        // 枚举一律以小驼峰字符串出现（DockerHostType.Socket → "socket"）。
        // 默认的数值形态对调用方不友好：接口契约里 type 收的就是 "socket"/"tcp"，
        // 响应却回一个 0/1，前端与文档都得靠猜；而数值一旦被写进测试或客户端，
        // 改枚举顺序就成了破坏性变更。字符串形态让契约自解释、且重排序安全。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }
}
