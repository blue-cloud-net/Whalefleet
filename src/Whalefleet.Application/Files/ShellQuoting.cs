using System.Text;

namespace Whalefleet.Application.Files;

/// <summary>
/// POSIX shell 的单引号转义。
/// </summary>
/// <remarks>
/// <para><b>为什么不用双引号</b>：双引号里 <c>$</c>、反引号、<c>\</c>、<c>!</c>
/// 仍然有特殊含义，而环境变量值里这些字符都很常见（连接串里就有 <c>$</c>）。
/// 单引号里除了 <c>'</c> 本身之外一切原样——规则只有一条，也最容易讲清楚。</para>
/// <para><b>为什么必须转义</b>：生成的 <c>docker run</c> 命令要「<b>原样可执行</b>」。
/// 一个含空格的 env 值不转义就会让 shell 把它拆成两个参数——
/// 命令看起来对，跑起来参数错位，这种问题在界面上根本看不出来。</para>
/// </remarks>
public static class ShellQuoting
{
    /// <summary>
    /// 把参数包成可安全放进 POSIX shell 的字面量。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>转义后的字面量。</returns>
    /// <remarks>
    /// 单引号内的 <c>'</c> 无法转义，标准做法是「关引号 → 写 <c>\'</c> → 再开引号」，
    /// 即 <c>'it'\''s'</c>。少写这一段就会让含撇号的值把整条命令打断。
    /// </remarks>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return "''";
        }

        // 不含特殊字符时直接给裸值，让命令更可读（绝大多数值都属于这一类）。
        if (value.All(character =>
                Char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':' or '=' or '@' or ','))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\'');

        foreach (var character in value)
        {
            if (character == '\'')
            {
                builder.Append("'\\''");
                continue;
            }

            builder.Append(character);
        }

        builder.Append('\'');

        return builder.ToString();
    }
}
