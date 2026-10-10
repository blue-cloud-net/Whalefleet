using Docker.Manager.Application.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Docker.Manager.Api.Errors;

/// <summary>
/// 将 MVC <see cref="ModelStateDictionary"/> 的校验错误转换为单行中文消息。
/// </summary>
/// <remarks>
/// <para>MVC 默认返回 <c>ValidationProblemDetails</c>，与本项目的 <c>ApiResult</c> 契约不符，
/// 因此 <c>Program.cs</c> 通过 <c>InvalidModelStateResponseFactory</c> 改用本类生成消息。</para>
/// <para><b>安全约束</b>：刻意不使用 <see cref="ModelError.Exception"/> 的消息——
/// 它可能包含 JSON 反序列化或类型转换的内部细节。</para>
/// </remarks>
internal static class ValidationErrorFormatter
{
    /// <summary>最多聚合的错误条数，避免超大请求产生超长响应。</summary>
    private const int MaxErrors = 10;

    /// <summary>消息最大长度（字符），超出部分截断。</summary>
    private const int MaxLength = 500;

    /// <summary>
    /// 生成校验失败消息。
    /// </summary>
    /// <param name="modelState">模型状态字典。</param>
    /// <returns>形如 <c>username: 不能为空；pageSize: 格式不正确</c> 的消息；
    /// 无具体错误时返回该错误码的默认文案。</returns>
    public static string Format(ModelStateDictionary modelState)
    {
        ArgumentNullException.ThrowIfNull(modelState);

        var errors = modelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .SelectMany(entry => entry.Value!.Errors.Select(error => Describe(entry.Key, error)))
            .Take(MaxErrors)
            .ToArray();

        if (errors.Length == 0)
        {
            return ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.ValidationFailed);
        }

        var message = String.Join("；", errors);

        return message.Length <= MaxLength
            ? message
            : String.Concat(message.AsSpan(0, MaxLength), "…");
    }

    private static string Describe(string key, ModelError error)
    {
        var detail = String.IsNullOrWhiteSpace(error.ErrorMessage)
            ? "格式不正确"
            : error.ErrorMessage;

        // key 为空表示模型级错误（非某个具体字段）。
        return String.IsNullOrEmpty(key) ? detail : $"{key}: {detail}";
    }
}
