using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Hosts;

/// <summary>
/// 主机分组。
/// </summary>
/// <remarks>
/// <para><b>纯展示性</b>：分组只影响总览页的聚合方式（生产 / 测试 / 本机 …），
/// <b>不</b>参与任何授权判定——「某个用户能操作哪台主机」由 0.1.0 落地的可见性表与 0.12.0 的容器级权限表回答。
/// 若让分组承担授权，「把主机挪进生产分组」就会变成一次隐式提权，而界面上完全看不出来。</para>
/// <para><b>为什么建表而不是枚举 / 配置</b>：分组是运行时可变的数据（管理员随时增删改），
/// 枚举会把「有哪些分组」焊死在编译期。这与 0.0.3 把角色从枚举改成表是同一个理由。</para>
/// <para><b>名称是数据，不是翻译键</b>：内置分组种入的是英文标识（<c>production</c> 等），
/// 前端的展示名对这三个已知名字取 i18n 文案；管理员自定义的分组则原样显示用户输入。
/// 用户输入的数据不该被程序硬翻——「预发布」翻成 "Pre-release" 只会更难懂。</para>
/// <para><b>软删除</b>：分组删除后其历史记录仍在；名称唯一索引带 <c>WHERE is_deleted = 0</c>，
/// 删除后名称可再次使用。删除分组<b>不</b>删主机，组内主机回到「未分组」（由应用服务负责）。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class HostGroup : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 分组名最小长度（去首尾空白后）。
    /// </summary>
    public const int MinNameLength = 1;

    /// <summary>
    /// 分组名最大长度。
    /// </summary>
    public const int MaxNameLength = 32;

    /// <summary>
    /// 描述最大长度。
    /// </summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>
    /// 分组名（去首尾空白后保存，唯一性<b>不区分大小写</b>）。
    /// </summary>
    public string Name { get; private set; } = default!;

    /// <summary>
    /// 描述；未填写时为 <see langword="null"/>。
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// 排序权重（升序）。同权重时按名称排，保证顺序稳定——
    /// 否则「刷新之后分组换了位置」会让人怀疑数据被动过。
    /// </summary>
    public int SortOrder { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private HostGroup()
    {
    }

    /// <summary>
    /// 新建分组。
    /// </summary>
    /// <param name="id">分组主键。</param>
    /// <param name="name">分组名；首尾空白会被去除。</param>
    /// <param name="description">描述；空白视为未填写。</param>
    /// <param name="sortOrder">排序权重。</param>
    /// <returns>新建的分组实体。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空、过长或含控制字符，或描述过长。</exception>
    public static HostGroup Create(Guid id, string name, string? description, int sortOrder) => new()
    {
        Id = id,
        Name = NormalizeName(name),
        Description = NormalizeDescription(description),
        SortOrder = sortOrder,
    };

    /// <summary>
    /// 改名。
    /// </summary>
    /// <param name="name">新的分组名。</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 非法。</exception>
    /// <remarks>
    /// 与其他实体不同，分组名<b>允许修改</b>：它只是展示标签，没有任何外部引用
    /// （不进 URL、不进 JWT、也不做外键）。改名的代价仅限于用户需要认出新名字。
    /// </remarks>
    public void Rename(string name) => this.Name = NormalizeName(name);

    /// <summary>
    /// 设置或清空描述。
    /// </summary>
    /// <param name="description">新描述；空白表示清空。</param>
    /// <exception cref="ArgumentException">描述过长。</exception>
    public void Describe(string? description) => this.Description = NormalizeDescription(description);

    /// <summary>
    /// 调整排序权重。
    /// </summary>
    /// <param name="sortOrder">新的权重。</param>
    public void Reorder(int sortOrder) => this.SortOrder = sortOrder;

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        if (trimmed.Length < MinNameLength || trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"分组名长度须在 {MinNameLength}-{MaxNameLength} 之间",
                nameof(name));
        }

        // 控制字符会破坏界面排版（换行、制表符）与日志格式，直接在入口拒掉
        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("分组名不得包含控制字符", nameof(name));
        }

        return trimmed;
    }

    private static string? NormalizeDescription(string? description)
    {
        if (String.IsNullOrWhiteSpace(description))
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
}
