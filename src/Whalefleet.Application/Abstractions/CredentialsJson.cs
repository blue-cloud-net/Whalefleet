using System.Text.Json;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 凭据载荷（<c>auth_config_json</c>）的 JSON 读取助手。
/// </summary>
/// <remarks>
/// <para><b>为什么统一在这里报错</b>：凭据不符合 schema 时<b>必须失败</b>，绝不能「读不出来就当没认证」——
/// 那会把一个配置笔误变成静默的未认证连接。错误码统一用 <see cref="ErrorCodes.HostUnreachable"/>（<c>1301</c>）：
/// 这份凭据确实连不上（或不该连）那台主机。</para>
/// <para><b>错误消息里只允许出现字段名与路径</b>，不得回显口令、私钥等载荷内容。</para>
/// <para><b>关于 JSON 语法校验</b>：<c>HostCredentials</c> 已经保证「载荷是合法 JSON」，
/// 因此从生产路径进来时 <see cref="JsonException"/> 分支不会被触发；它只是防御——
/// 若将来出现绕过值对象的写入路径（迁移脚本、手工改库），报「不是合法 JSON」
/// 比抛一个 500 要好。</para>
/// </remarks>
internal static class CredentialsJson
{
    /// <summary>
    /// 选项：camelCase + 大小写不敏感（<see cref="JsonSerializerDefaults.Web"/>），
    /// 与 API 契约的字段命名一致，且手写配置时大小写写错也能识别。
    /// </summary>
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 反序列化凭据载荷。
    /// </summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="configJson">载荷 JSON。</param>
    /// <param name="description">用于错误消息的凭据种类（如 <c>TLS</c>）。</param>
    /// <returns>反序列化结果。</returns>
    /// <exception cref="BusinessException">载荷为空或不是合法 JSON。</exception>
    public static T Deserialize<T>(string? configJson, string description)
        where T : class
    {
        if (String.IsNullOrWhiteSpace(configJson))
        {
            throw new BusinessException(
                ErrorCodes.HostUnreachable,
                $"{description} 凭据缺少配置载荷（auth_config_json 为空）");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(configJson, _options)
                ?? throw new BusinessException(
                    ErrorCodes.HostUnreachable,
                    $"{description} 凭据载荷为空 JSON");
        }
        catch (JsonException exception)
        {
            throw new BusinessException(
                ErrorCodes.HostUnreachable,
                $"{description} 凭据载荷不是合法 JSON",
                exception);
        }
    }

    /// <summary>
    /// 取出必填字符串字段。
    /// </summary>
    /// <param name="value">字段值。</param>
    /// <param name="field">字段名（用于错误消息）。</param>
    /// <param name="description">凭据种类（用于错误消息）。</param>
    /// <returns>去空白后的字段值。</returns>
    /// <exception cref="BusinessException">字段缺失或为空白。</exception>
    public static string RequireText(string? value, string field, string description)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            throw new BusinessException(
                ErrorCodes.HostUnreachable,
                $"{description} 凭据缺少必填字段 {field}");
        }

        return value.Trim();
    }
}
