namespace Whalefleet.Application.Common;

/// <summary>
/// 清理（prune）端点的过滤器构造与标签归一化。
/// </summary>
/// <remarks>
/// <para><b>为什么自己拼 <c>filters</c> 而不直接用 SDK 的字典</b>：SDK 只给了
/// <c>IDictionary&lt;string, IDictionary&lt;string, bool&gt;&gt;</c>，
/// 外层键是过滤名（<c>dangling</c> / <c>label</c> / <c>all</c>），
/// 内层是「取值 → 是否启用」。两层字典的形状很容易拼错
/// （把 <c>true</c> 放到外层、或把标签<b>值</b>当成了内层键），
/// 而拼错的症状是<b>过滤静默不生效</b>：清理照样执行，只是范围比预想的大。
/// 因此这里收口成一处构造，由集成测试钉住实际范围。</para>
/// <para><b>内层的 <c>true</c> 不能省</b>：守护进程按「取值 → 布尔」解释，
/// 只有布尔值为 <c>true</c> 的条目才算启用。</para>
/// <para><b>类型是 <c>public</c></b>：标签归一化是纯逻辑，值得在单元测试里直接覆盖；
/// 而 <c>Application</c> 的 <c>internal</c> 成员单元测试项目访问不到
/// （参见 <c>0.4.0</c> 把 <c>ImageReference</c> 提成 public 的先例）。</para>
/// </remarks>
public static class PruneFilter
{
    /// <summary>标签过滤表达式的最大长度。</summary>
    public const int MaxLabelLength = 256;

    /// <summary>
    /// 校验并归一化标签过滤表达式。
    /// </summary>
    /// <param name="label">原始表达式（<c>key</c> 或 <c>key=value</c>）。</param>
    /// <returns>去首尾空白后的表达式；为空时返回 <see langword="null"/>（表示不按标签过滤）。</returns>
    /// <exception cref="BusinessException">含控制字符或超长（<c>1000</c>）。</exception>
    /// <remarks>
    /// <para><b>两种写法都允许，原样透传给守护进程</b>：<c>key</c> 表示「带这个键即可」，
    /// <c>key=value</c> 表示「键与值都相等」。语义由守护进程定义，
    /// 服务端<b>不重新解释</b>——重新解释就等于维护了第二套与 Docker 不一致的规则，
    /// 而两套规则漂移时，症状是「我明明选了它却没被清理」。</para>
    /// <para><b>值里允许有空格</b>（标签值本来就可以含空格），
    /// 但<b>不允许控制字符</b>：它们不可能出现在正常的标签里，放进来只会是拼错。</para>
    /// </remarks>
    public static string? NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var trimmed = label.Trim();

        if (trimmed.Length > MaxLabelLength)
        {
            throw BusinessException.Validation(
                $"label 最长 {MaxLabelLength} 个字符，当前 {trimmed.Length} 个");
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                throw BusinessException.Validation("label 不能包含控制字符");
            }
        }

        return trimmed;
    }

    /// <summary>
    /// 构造守护进程要的过滤器字典。
    /// </summary>
    /// <param name="entries">过滤条目（过滤名 → 取值）。</param>
    /// <returns>可直接交给 SDK 的过滤器字典。</returns>
    /// <remarks>
    /// 同一个过滤名出现多次时合并到同一组里（内层是「取值 → 布尔」的多值结构）。
    /// </remarks>
    public static IDictionary<string, IDictionary<string, bool>> Build(
        params (string Key, string Value)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal);

        foreach (var (key, value) in entries)
        {
            if (!filters.TryGetValue(key, out var bucket))
            {
                bucket = new Dictionary<string, bool>(StringComparer.Ordinal);
                filters[key] = bucket;
            }

            bucket[value] = true;
        }

        return filters;
    }
}
