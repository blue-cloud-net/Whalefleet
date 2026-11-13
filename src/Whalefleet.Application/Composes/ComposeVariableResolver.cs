using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Whalefleet.Application.Common;
using Whalefleet.Core.Variables;

using YamlDotNet.RepresentationModel;

namespace Whalefleet.Application.Composes;

/// <summary>
/// Compose 变量引用（<c>${VAR}</c>）的解析与插值。
/// </summary>
/// <remarks>
/// <para><b>支持的写法</b>：</para>
/// <list type="table">
///   <item><term><c>${VAR}</c></term><description>取变量值；未定义时<b>报错</b>。</description></item>
///   <item><term><c>${VAR:-default}</c></term><description>取变量值；未定义时用 <c>default</c>。</description></item>
///   <item><term><c>${VAR-default}</c></term><description>同上（Compose 的另一种写法）。</description></item>
///   <item><term><c>$$</c></term><description>字面量 <c>$</c>（转义）。</description></item>
/// </list>
/// <para><b>不支持 <c>${VAR:?msg}</c> 与 <c>${VAR:+alt}</c></b>：它们的语义
/// 分别是「未定义就失败」与「已定义才用 alt」，都不是「取不到就用默认值」。
/// 放行它们会让「同一个 <c>${VAR}</c> 在不同上下文里含义不同」，
/// 而本平台只有一处注入点（Compose 文件），没有第二种上下文——
/// 因此明确报错并说明原因，比半吊子实现安全。</para>
/// <para><b>落单的 <c>$</c> 按字面量处理</b>：口令里出现 <c>$</c> 很常见
/// （<c>P@$$w0rd</c>），而把它当成变量起始符会让口令变成「引用了一个不存在的变量」。
/// 需要写 <c>$$</c> 时用 <c>$$</c>（转义成字面量 <c>$</c>）。</para>
/// <para><b>插值发生在「YAML 标量」这一层</b>，而不是「解析后的模型」：
/// 只有这样，<c>ports: "${PORT}:80"</c> 里的端口号才能在替换之后再按端口解析，
/// 而不是在解析阶段就因为「不是数字」而失败。</para>
/// </remarks>
internal static class ComposeVariableResolver
{
    /// <summary>
    /// 插值一个文本里的全部变量引用。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="variables">
    /// 变量表；<see langword="null"/> 表示<b>宽松模式</b>——未定义的变量不报错，
    /// 而是被替换成保持形态的占位符（并记入 <paramref name="unresolved"/>）。
    /// </param>
    /// <param name="unresolved">宽松模式下被替换掉的变量名集合；严格模式下不写入。</param>
    /// <returns>插值后的文本。</returns>
    /// <exception cref="BusinessException">
    /// 引用未闭合（<c>1904</c>）、变量名非法（<c>1909</c>）或变量未定义（<c>1908</c>）。
    /// </exception>
    internal static string Interpolate(
        string text,
        IReadOnlyDictionary<string, string>? variables,
        ISet<string> unresolved)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(unresolved);

        if (!text.Contains('$', StringComparison.Ordinal))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (current != '$')
            {
                builder.Append(current);
                index++;
                continue;
            }

            // "$$" → 字面量 "$"
            if (index + 1 < text.Length && text[index + 1] == '$')
            {
                builder.Append('$');
                index += 2;
                continue;
            }

            if (index + 1 >= text.Length || text[index + 1] != '{')
            {
                // 落单的 "$"：按字面量处理（口令里很常见）。
                builder.Append('$');
                index++;
                continue;
            }

            var close = text.IndexOf('}', index + 2);
            if (close < 0)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{text}」里的变量引用没有闭合——找不到与 ${{ 配对的 }}");
            }

            builder.Append(ResolveExpression(text[(index + 2)..close], variables, unresolved, text));
            index = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 收集一个文本里引用到的变量名（**只扫描，不解析、不报错**）。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>变量名列表（去重，保持首次出现顺序）。</returns>
    /// <remarks>
    /// <b>与 <see cref="Interpolate"/> 的区别</b>：这里不查变量表、不看默认值的语义、
    /// 也不对非法变量名报错——它只回答「这个文本引用了哪些名字」。
    /// 严格模式下的报错交给 <see cref="Interpolate"/>，两边各管一件事。
    /// </remarks>
    internal static IReadOnlyList<string> CollectReferences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (current != '$')
            {
                index++;
                continue;
            }

            // "$$" 是转义，不算引用
            if (index + 1 < text.Length && text[index + 1] == '$')
            {
                index += 2;
                continue;
            }

            if (index + 1 >= text.Length || text[index + 1] != '{')
            {
                index++;
                continue;
            }

            var close = text.IndexOf('}', index + 2);
            if (close < 0)
            {
                // 未闭合：留给 Interpolate 报 1904，这里不重复报错。
                break;
            }

            var expression = text[(index + 2)..close];
            var separator = FindSeparator(expression, out var operatorLength, out _);
            var name = separator < 0 ? expression : expression[..separator];

            if (EnvVariableName.IsValid(name) && seen.Add(name))
            {
                names.Add(name);
            }

            index = close + 1;
        }

        return names;
    }

    /// <summary>
    /// 对整棵 YAML 树做插值。
    /// </summary>
    /// <param name="node">树根。</param>
    /// <param name="variables">变量表；<see langword="null"/> 表示宽松模式。</param>
    /// <param name="unresolved">宽松模式下被替换掉的变量名集合。</param>
    internal static void InterpolateTree(
        YamlNode node,
        IReadOnlyDictionary<string, string>? variables,
        ISet<string> unresolved)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(unresolved);

        switch (node)
        {
            case YamlScalarNode scalar when scalar.Value is not null:
                scalar.Value = Interpolate(scalar.Value, variables, unresolved);
                break;

            case YamlMappingNode mapping:
                foreach (var pair in mapping.Children)
                {
                    InterpolateTree(pair.Key, variables, unresolved);
                    InterpolateTree(pair.Value, variables, unresolved);
                }
                break;

            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    InterpolateTree(child, variables, unresolved);
                }
                break;
        }
    }

    /// <summary>
    /// 解析一个引用表达式（<c>${}</c> 里的内容）。
    /// </summary>
    /// <param name="expression">表达式文本。</param>
    /// <param name="variables">变量表；<see langword="null"/> 表示宽松模式。</param>
    /// <param name="unresolved">宽松模式下被替换掉的变量名集合。</param>
    /// <param name="text">原始文本（用于错误消息）。</param>
    /// <returns>替换后的文本。</returns>
    private static string ResolveExpression(
        string expression,
        IReadOnlyDictionary<string, string>? variables,
        ISet<string> unresolved,
        string text)
    {
        var separator = FindSeparator(expression, out var operatorLength, out var operatorKind);

        if (separator < 0)
        {
            return ResolveValue(expression, null, variables, unresolved, text);
        }

        if (operatorKind == '?' || operatorKind == '+')
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{text}」用了不受支持的变量语法（{expression}）——"
                + "本平台只支持 ${VAR} 与 ${VAR:-默认值}，"
                + "不支持「未定义就失败」与「已定义才用备选」这两种语义");
        }

        var name = expression[..separator];
        var defaultValue = expression[(separator + operatorLength)..];

        return ResolveValue(name, defaultValue, variables, unresolved, text);
    }

    /// <summary>
    /// 找出表达式里的分隔符。
    /// </summary>
    /// <param name="expression">表达式文本。</param>
    /// <param name="operatorLength">分隔符长度（<c>:-</c> 是 2，其余是 1）。</param>
    /// <param name="operatorKind">分隔符的语义种类（<c>-</c> / <c>?</c> / <c>+</c>）。</param>
    /// <returns>分隔符位置；没有分隔符时返回 -1。</returns>
    private static int FindSeparator(string expression, out int operatorLength, out char operatorKind)
    {
        for (var index = 0; index < expression.Length; index++)
        {
            var current = expression[index];

            if (current == ':')
            {
                if (index + 1 >= expression.Length)
                {
                    // 结尾的 ":" 是非法表达式，交给上层按「变量名」处理并报 1909。
                    operatorLength = 0;
                    operatorKind = current;
                    return -1;
                }

                var next = expression[index + 1];
                if (next is not ('-' or '?' or '+'))
                {
                    operatorLength = 0;
                    operatorKind = current;
                    return -1;
                }

                operatorLength = 2;
                operatorKind = next;
                return index;
            }

            if (current is '-' or '?' or '+')
            {
                operatorLength = 1;
                operatorKind = current;
                return index;
            }
        }

        operatorLength = 0;
        operatorKind = '\0';
        return -1;
    }

    /// <summary>
    /// 取变量值。
    /// </summary>
    /// <param name="name">变量名。</param>
    /// <param name="defaultValue">默认值；<see langword="null"/> 表示没有默认值。</param>
    /// <param name="variables">变量表；<see langword="null"/> 表示宽松模式。</param>
    /// <param name="unresolved">宽松模式下被替换掉的变量名集合。</param>
    /// <param name="text">原始文本（用于错误消息）。</param>
    /// <returns>变量值或默认值。</returns>
    private static string ResolveValue(
        string name,
        string? defaultValue,
        IReadOnlyDictionary<string, string>? variables,
        ISet<string> unresolved,
        string text)
    {
        if (!EnvVariableName.IsValid(name))
        {
            throw new BusinessException(
                ErrorCodes.InvalidVariableName,
                $"「{text}」里的变量名「{name}」非法——只能由字母、数字与下划线组成，且不得以数字开头");
        }

        if (variables is not null && variables.TryGetValue(name, out var value))
        {
            return value;
        }

        if (defaultValue is not null)
        {
            return defaultValue;
        }

        if (variables is null)
        {
            // 宽松模式：未定义的变量换成占位符，让「只看结构」的解析不至于整个失败。
            unresolved.Add(name);
            return PlaceholderFor(name);
        }

        throw new BusinessException(
            ErrorCodes.VariableNotDefined,
            $"变量「{name}」未定义——引用出现在「{text}」。请先在变量管理里声明它");
    }

    /// <summary>
    /// 为未定义的变量生成占位符。
    /// </summary>
    /// <param name="name">变量名。</param>
    /// <returns>一个稳定的纯数字文本。</returns>
    /// <remarks>
    /// <b>为什么是纯数字</b>：端口映射是「宿主端口:容器端口」，
    /// 占位符若不是数字，端口就解析不出来——而「只看结构」的解析（详情页、
    /// 校验提示）恰恰要能解析出端口。
    /// <b>为什么稳定</b>：同一个变量名每次都得到同一个占位符，
    /// 这样两次解析的结构一致，不会出现「列表页说 3 个端口、详情页说 2 个」的抖动。
    /// </remarks>
    private static string PlaceholderFor(string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        var value = BinaryPrimitives.ReadUInt32BigEndian(bytes) % 999_990_000u + 10_000u;

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
