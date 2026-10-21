using Whalefleet.Application.Common;
using Whalefleet.Application.Images;

namespace Whalefleet.Api.Controllers;

/// <summary>
/// 镜像构建的表单模型（<c>multipart/form-data</c> 的非文件部分）。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个表单模型而不是直接收 <see cref="BuildImageRequest"/></b>：
/// <see cref="BuildImageRequest"/> 里的 <c>Context</c> 是应用层的抽象（一个 <c>Stream</c>），
/// 而表单绑定拿到的是 <c>IFormFile</c>。两者形态不同，且
/// <c>Stream</c> 无法从表单字段直接绑定。</para>
/// <para><b>字典字段用 JSON 字符串传</b>：<c>buildArgs</c> / <c>labels</c> 是键值对，
/// 表单里没有天然的字典形态。约定用<b>JSON 对象的字符串</b>传（如
/// <c>buildArgs={"VERSION":"1.0"}</c>），比「同名字段重复出现」更不容易出错
/// （后者还要处理同名多值到底取哪个）。</para>
/// <para><b>解析失败一律报 <c>1000</c></b>：字典解析失败属于「参数不合法」，
/// 归到与其它字段校验失败相同的错误码，界面不需要为它单开一套处理。</para>
/// </remarks>
public sealed class BuildImageForm
{
    /// <summary>构建产物的镜像引用（仓库名 + 可选标签，如 <c>myapp:v1</c>）；必填。</summary>
    public string? Tag { get; set; }

    /// <summary>tar 包内的 Dockerfile 路径；省略时为 <c>Dockerfile</c>。</summary>
    public string? Dockerfile { get; set; }

    /// <summary>构建参数（<c>ARG</c>），以 JSON 对象的字符串传入。</summary>
    public string? BuildArgs { get; set; }

    /// <summary>镜像标签（<c>LABEL</c>），以 JSON 对象的字符串传入。</summary>
    public string? Labels { get; set; }

    /// <summary>多阶段构建的目标阶段（<c>--target</c>）。</summary>
    public string? Target { get; set; }

    /// <summary>目标平台（如 <c>linux/amd64</c>）。</summary>
    public string? Platform { get; set; }

    /// <summary>是否不使用构建缓存（<c>--no-cache</c>）。</summary>
    public bool? NoCache { get; set; }

    /// <summary>
    /// 进度令牌（0.5.0）：客户端先生成它、在 SignalR 上订阅它，再把它带进本次请求。
    /// </summary>
    /// <remarks>
    /// <b>省略时行为完全不变</b>：仍然一次性返回结果，只是不推实时进度。
    /// </remarks>
    public string? ProgressToken { get; set; }

    /// <summary>
    /// 转成应用层请求。
    /// </summary>
    /// <param name="contextLength">构建上下文的长度（字节）。</param>
    /// <param name="contextStream">构建上下文的流。</param>
    /// <returns>应用层构建请求。</returns>
    /// <exception cref="BusinessException">
    /// 字典字段不是合法的 JSON 对象（<c>1000</c>）。
    /// </exception>
    public BuildImageRequest ToRequest(long contextLength, Stream contextStream) => new()
    {
        Context = contextStream,
        ContextLength = contextLength,
        Tag = this.Tag,
        Dockerfile = this.Dockerfile,
        BuildArgs = ParseDictionary(this.BuildArgs, "buildArgs"),
        Labels = ParseDictionary(this.Labels, "labels"),
        Target = this.Target,
        Platform = this.Platform,
        NoCache = this.NoCache,
        ProgressToken = this.ProgressToken,
    };

    /// <summary>
    /// 解析 JSON 字典字段。
    /// </summary>
    /// <param name="json">JSON 对象的文本；为空时返回 <see langword="null"/>。</param>
    /// <param name="field">字段名（用于错误消息）。</param>
    /// <returns>键值对；输入为空时返回 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">不是合法的 JSON 对象（<c>1000</c>）。</exception>
    private static Dictionary<string, string>? ParseDictionary(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            return parsed;
        }
        catch (System.Text.Json.JsonException)
        {
            throw BusinessException.Validation(
                $"{field} 不是合法的 JSON 对象（例：{{\"KEY\":\"value\"}}）");
        }
    }
}
