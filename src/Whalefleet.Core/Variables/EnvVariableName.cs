using System.Diagnostics.CodeAnalysis;

using Whalefleet.Core.Common;

namespace Whalefleet.Core.Variables;

/// <summary>
/// 环境变量名（Compose 的 <c>${VAR}</c> 里的 <c>VAR</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么是值对象而不是裸字符串</b>：变量名会同时出现在
/// <c>env_variables.name</c> 列、YAML 的 <c>${VAR}</c> 引用、Webhook 的查询串键
/// 与界面的输入框里。<b>四处必须逐字一致</b>——尤其是大小写：
/// <c>${TAG}</c> 与 <c>${tag}</c> 是两个不同的变量（与 Compose 一致），
/// 而「统一转小写」这种自作主张的规范化会让用户写 <c>${Tag}</c> 却拿到 <c>TAG</c> 的值，
/// 且症状是「变量没生效」，极难联想到是大小写。</para>
/// <para><b>字符集为什么收紧</b>：<c>[A-Za-z_][A-Za-z0-9_]*</c> 是 POSIX 习惯的标识符形态，
/// 也避开了「变量名里带点号 / 短横线」这类在 Webhook 查询串与 shell 里都需要额外转义的字符。
/// 它比 Compose 本身更严——Compose 允许 <c>.</c>，但那种名字在 Webhook 的
/// <c>?a.b=1</c> 里与「查询串的键名约定」混在一起时容易出错。</para>
/// <para><b>不可变</b>：值对象按值相等（<see cref="ValueObject"/>）。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "「变量名」是本项目的领域名词，非 CA1711 所针对的系统命名空间后缀。")]
public sealed class EnvVariableName : ValueObject
{
    /// <summary>
    /// 变量名最大长度。
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// 初始化变量名值对象。
    /// </summary>
    /// <param name="value">规范化后的变量名（已去首尾空白，字符集合法）。</param>
    private EnvVariableName(string value)
    {
        this.Value = value;
    }

    /// <summary>
    /// 变量名文本。
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// 解析并校验一个变量名。
    /// </summary>
    /// <param name="name">原始变量名。</param>
    /// <returns>变量名值对象。</returns>
    /// <exception cref="ArgumentException">
    /// 变量名为空、超长或字符集非法。
    /// </exception>
    public static EnvVariableName Parse(string? name)
    {
        if (!TryParse(name, out var parsed))
        {
            throw new ArgumentException(
                $"变量名不得为空、不得超过 {MaxLength} 个字符，且只能由字母、数字与下划线组成（且不得以数字开头）",
                nameof(name));
        }

        return parsed;
    }

    /// <summary>
    /// 尝试解析一个变量名。
    /// </summary>
    /// <param name="name">原始变量名。</param>
    /// <param name="parsed">解析成功时的值对象；失败时为 <see langword="null"/>。</param>
    /// <returns>解析是否成功。</returns>
    public static bool TryParse(string? name, [NotNullWhen(true)] out EnvVariableName? parsed)
    {
        parsed = null;

        if (String.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxLength)
        {
            return false;
        }

        if (!IsValidStart(trimmed[0]))
        {
            return false;
        }

        for (var index = 1; index < trimmed.Length; index++)
        {
            if (!IsValidPart(trimmed[index]))
            {
                return false;
            }
        }

        parsed = new EnvVariableName(trimmed);
        return true;
    }

    /// <summary>
    /// 变量名是否合法。
    /// </summary>
    /// <param name="name">原始变量名。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    public static bool IsValid(string? name) => TryParse(name, out _);

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return this.Value;
    }

    /// <summary>
    /// 首字符是否合法：字母或下划线。
    /// </summary>
    /// <param name="character">字符。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    private static bool IsValidStart(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    /// <summary>
    /// 后续字符是否合法：字母、数字或下划线。
    /// </summary>
    /// <param name="character">字符。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    private static bool IsValidPart(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
}
