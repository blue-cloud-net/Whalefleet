using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace Whalefleet.Api.Serialization;

/// <summary>
/// 原始字节流的输入格式化器（<c>application/octet-stream</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：MVC 的默认输入格式化器只认 JSON / XML 之类的结构化格式。
/// 而 0.14.0 的导入端点收的是<b>裸 tar 流</b>（<c>docker save</c> 的产物）——
/// 它没有结构可解析，只能整段透传。</para>
/// <para><b>为什么不用 <c>byte[]</c> 代替</b>：那会把整个文件读进内存。
/// 20 GB 的上传会把进程直接撑爆——而流式读取的内存占用与文件大小无关。</para>
/// <para><b>为什么不用「直接读 <c>Request.Body</c>」代替</b>：那样可行，
/// 但动作签名会失去「我要一个请求体」这层表达；
/// 而本格式化器让「请求体就是一个流」这件事在模型绑定层就成立，
/// 后续新增端点（容器导入、文件上传）可以直接复用。</para>
/// <para><b>体积上限不在这里管</b>：那是应用层 <c>LimitedReadStream</c> 的职责
/// （框架默认上限只有 30 MB，而本项目要支持 20 GB）。</para>
/// </remarks>
public sealed class OctetStreamInputFormatter : IInputFormatter
{
    /// <summary>
    /// 该格式化器支持的媒体类型。
    /// </summary>
    public static readonly string[] SupportedMediaTypes = ["application/octet-stream"];

    /// <inheritdoc />
    public bool CanReadType(Type type) => type == typeof(Stream);

    /// <inheritdoc />
    public Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context) =>
        this.ReadAsync(context);

    /// <inheritdoc />
    public async Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.HttpContext.Request;

        if (request.ContentLength is 0)
        {
            return await InputFormatterResult.FailureAsync().ConfigureAwait(false);
        }

        // 直接把请求体流交出去：不缓冲、不物化。
        // 读取与体积上限由调用方（应用层的 LimitedReadStream）负责。
        return await InputFormatterResult.SuccessAsync(request.Body).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool CanRead(InputFormatterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!this.CanReadType(context.ModelType))
        {
            return false;
        }

        var contentType = context.HttpContext.Request.ContentType;

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = MediaTypeHeaderValue.Parse(contentType);

        return SupportedMediaTypes.Any(
            supported => MediaTypeHeaderValue.Parse(supported).IsSubsetOf(mediaType));
    }
}
