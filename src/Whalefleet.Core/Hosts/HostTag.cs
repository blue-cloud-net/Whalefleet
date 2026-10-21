using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Hosts;

/// <summary>
/// 主机标签（多对多自由打标）。
/// </summary>
/// <remarks>
/// <para><b>与分组的职责分工</b>（写死在 roadmap，避免两套组织方式打架）：
/// 分组是<b>单一归属</b>（一台主机一个分组，是选择 / 过滤 / 批量操作的边界），
/// 标签是<b>多对多</b>（一台主机多个标签、一个标签多台主机，只用于过滤与批量选择）。</para>
/// <para><b>不参与授权</b>：授权边界只有一条，就是分组与角色分配。
/// 若让标签参与授权，「给主机打个标签」就会变成一次隐式提权，而界面上完全看不出来——</para>
/// <para><b>为什么建表而不是枚举</b>：标签是运行时可变的数据（管理员随时增删改），
/// 枚举会把「有哪些标签」焊死在编译期。与 <see cref="HostGroup"/> 同一理由。</para>
/// <para><b>名称是数据，不是翻译键</b>：前端原样展示用户输入，不做任何硬翻。
/// 显示名与唯一性都<b>不区分大小写</b>，唯一索引带 <c>WHERE is_deleted = 0</c>，
/// 删除后名称可再次使用。</para>
/// <para><b>颜色只接受 6 位十六进制</b>（<c>#RRGGBB</c>）：放行 <c>#RGB</c> / <c>rgb()</c> /
/// 颜色名会让前端与后端各写一套解析规则，而两套规则漂移的症状是「颜色看着对但在某个主题下不可读」。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class HostTag : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 标签名最小长度（去首尾空白后）。
    /// </summary>
    public const int MinNameLength = 1;

    /// <summary>
    /// 标签名最大长度。
    /// </summary>
    public const int MaxNameLength = 32;

    /// <summary>
    /// 描述最大长度。
    /// </summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>
    /// 颜色文本长度（<c>#RRGGBB</c>）。
    /// </summary>
    public const int ColorLength = 7;

    /// <summary>
    /// 标签名（去首尾空白后保存，唯一性<b>不区分大小写</b>）。
    /// </summary>
    public string Name { get; private set; } = default!;

    /// <summary>
    /// 描述；未填写时为 <see langword="null"/>。
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// 展示颜色（<c>#RRGGBB</c>）；未设置时为 <see langword="null"/>（界面用默认色）。
    /// </summary>
    public string? Color { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private HostTag()
    {
    }

    /// <summary>
    /// 新建标签。
    /// </summary>
    /// <param name="id">标签主键。</param>
    /// <param name="name">标签名；首尾空白会被去除。</param>
    /// <param name="description">描述；空白视为未填写。</param>
    /// <param name="color">颜色（<c>#RRGGBB</c>）；空白视为未设置。</param>
    /// <returns>新建的标签实体。</returns>
    /// <exception cref="ArgumentException">任一字段非法。</exception>
    public static HostTag Create(Guid id, string name, string? description, string? color) => new()
    {
        Id = id,
        Name = NormalizeName(name),
        Description = NormalizeDescription(description),
        Color = NormalizeColor(color),
    };

    /// <summary>
    /// 改名。
    /// </summary>
    /// <param name="name">新的标签名。</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 非法。</exception>
    /// <remarks>
    /// 标签名<b>允许修改</b>：它只是展示标签，不进 URL、不做外键（关联表引用的是主键）。
    /// 这与主机名不同——主机名是对外标识，改名的代价要大得多。
    /// </remarks>
    public void Rename(string name) => this.Name = NormalizeName(name);

    /// <summary>
    /// 设置或清空描述。
    /// </summary>
    /// <param name="description">新描述；空白表示清空。</param>
    /// <exception cref="ArgumentException">描述过长。</exception>
    public void Describe(string? description) => this.Description = NormalizeDescription(description);

    /// <summary>
    /// 设置或清空颜色。
    /// </summary>
    /// <param name="color">新颜色；空白表示清空（回到默认色）。</param>
    /// <exception cref="ArgumentException">颜色不是 6 位十六进制。</exception>
    public void SetColor(string? color) => this.Color = NormalizeColor(color);

    /// <summary>
    /// 规范化并校验标签名。
    /// </summary>
    /// <param name="name">原始名称。</param>
    /// <returns>去首尾空白后的名称。</returns>
    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        if (trimmed.Length < MinNameLength || trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"标签名长度须在 {MinNameLength}-{MaxNameLength} 之间",
                nameof(name));
        }

        // 控制字符会破坏界面排版（换行、制表符）与日志格式，直接在入口拒掉
        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("标签名不得包含控制字符", nameof(name));
        }

        return trimmed;
    }

    /// <summary>
    /// 规范化并校验描述。
    /// </summary>
    /// <param name="description">原始描述。</param>
    /// <returns>去首尾空白后的描述；空白输入返回 <see langword="null"/>。</returns>
    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var trimmed = description.Trim();

        if (trimmed.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"描述长度不得超过 {MaxDescriptionLength} 个字符", nameof(description));
        }

        return trimmed;
    }

    /// <summary>
    /// 规范化并校验颜色。
    /// </summary>
    /// <param name="color">原始颜色。</param>
    /// <returns>大写化的 <c>#RRGGBB</c>；空白输入返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 只收 6 位十六进制：存进库的颜色要被<b>两处</b>解析（界面标签底色、将来的告警渠道），
    /// 放行多种写法等于让两处各写一套解析。大写化是为了让「同色不同写法」在库里只有一个样子。
    /// </remarks>
    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var trimmed = color.Trim();

        if (trimmed.Length != ColorLength
            || trimmed[0] != '#'
            || !trimmed[1..].All(static c => char.IsAsciiHexDigit(c)))
        {
            throw new ArgumentException("颜色须为 #RRGGBB 形式的 6 位十六进制", nameof(color));
        }

        return trimmed.ToUpperInvariant();
    }
}
