namespace Whalefleet.Application.Images;

/// <summary>
/// Registry V2 的 <c>WWW-Authenticate</c> 挑战解析。
/// </summary>
/// <remarks>
/// <para><b>为什么这是纯逻辑而不是探测实现的一部分</b>：挑战头的形态由规范固定
/// （<c>Bearer realm="…",service="…",scope="…"</c>），解析失败时实现只能返回
/// 「仓库不可达」——把这段留在 Infrastructure 里，就等于让它永远进不了单测。
/// </para>
/// <para><b>只认 <c>Bearer</c> 与 <c>Basic</c> 两种方案</b>：其余方案（如
/// <c>Negotiate</c>）本项目不支持，解析出来也没人会用到。</para>
/// <para><b>值里的逗号</b>：<c>scope="repository:library/nginx:pull"</c> 本身含
/// 冒号，而 <c>realm</c> 的 URL 里可能含逗号（极少但合法），因此解析时按
/// <c>key=value</c> 的引号边界切分，而不是简单 <c>Split(',')</c>——
/// 后者会把一个 URL 切成两半，然后得到一个拼错的令牌端点。</para>
/// </remarks>
public sealed record RegistryAuthChallenge(
    string Scheme,
    string? Realm,
    string? Service,
    string? Scope)
{
    /// <summary>
    /// 尝试从 <c>WWW-Authenticate</c> 头解析挑战。
    /// </summary>
    /// <param name="headerValue">响应头原文。</param>
    /// <returns>解析结果；头为空或方案不受支持时返回 <see langword="null"/>。</returns>
    public static RegistryAuthChallenge? TryParse(string? headerValue)
    {
        if (String.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var text = headerValue.Trim();
        var space = text.IndexOf(' ', StringComparison.Ordinal);

        var scheme = space < 0 ? text : text[..space];

        if (!String.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            && !String.Equals(scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (space < 0)
        {
            return new RegistryAuthChallenge(scheme, null, null, null);
        }

        var parameters = ParseParameters(text[(space + 1)..]);

        parameters.TryGetValue("realm", out var realm);
        parameters.TryGetValue("service", out var service);
        parameters.TryGetValue("scope", out var scope);

        return new RegistryAuthChallenge(scheme, realm, service, scope);
    }

    /// <summary>
    /// 解析 <c>key="value"</c> 形式的参数列表。
    /// </summary>
    /// <param name="text">参数部分。</param>
    /// <returns>参数表（键小写）。</returns>
    private static Dictionary<string, string> ParseParameters(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        while (index < text.Length)
        {
            // 跳到下一个 key 的起点（跳过逗号与空白）。
            while (index < text.Length && (text[index] == ',' || Char.IsWhiteSpace(text[index])))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var equals = text.IndexOf('=', index);

            if (equals < 0)
            {
                break;
            }

            var key = text[index..equals].Trim();

            index = equals + 1;

            while (index < text.Length && Char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            string value;

            if (index < text.Length && text[index] == '"')
            {
                // 引号值：找到下一个引号为止（值内部可以有逗号与等号）。
                var closing = text.IndexOf('"', index + 1);

                if (closing < 0)
                {
                    value = text[(index + 1)..];
                    index = text.Length;
                }
                else
                {
                    value = text[(index + 1)..closing];
                    index = closing + 1;
                }
            }
            else
            {
                // 无引号值：到下一个逗号为止。
                var comma = text.IndexOf(',', index);

                value = comma < 0 ? text[index..] : text[index..comma];
                index = comma < 0 ? text.Length : comma + 1;
            }

            if (key.Length > 0)
            {
                result[key] = value.Trim();
            }
        }

        return result;
    }
}
