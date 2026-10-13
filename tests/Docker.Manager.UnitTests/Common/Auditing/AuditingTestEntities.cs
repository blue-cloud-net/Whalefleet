using Docker.Manager.Core.Common;
using Docker.Manager.Core.Common.Auditing;

namespace Docker.Manager.UnitTests.Common.Auditing;

/// <summary>
/// 测试用实体：一组仅带创建审计的实体，分别以 <see cref="Guid"/> / <see cref="long"/> /
/// <see cref="string"/> 作为审计人主键类型，验证 <typeparamref name="TUserKey"/> 不加类型约束。
/// </summary>
internal sealed class GuidCreationAudited : CreationAuditedEntity<Guid, Guid>
{
    public GuidCreationAudited()
    {
    }

    public GuidCreationAudited(Guid id) => this.Id = id;
}

/// <summary>
/// 测试用实体：审计人主键为 <see cref="long"/>（自增主键场景）。
/// </summary>
internal sealed class LongCreationAudited : CreationAuditedEntity<Guid, long>
{
    public LongCreationAudited()
    {
    }
}

/// <summary>
/// 测试用实体：审计人主键为 <see cref="string"/>（引用类型主键场景，验证无约束支持）。
/// </summary>
internal sealed class StringCreationAudited : CreationAuditedEntity<Guid, string>
{
    public StringCreationAudited()
    {
    }
}

/// <summary>
/// 测试用实体：带创建 + 修改审计。
/// </summary>
internal sealed class GuidAudited : AuditedEntity<Guid, Guid>
{
    public GuidAudited()
    {
    }

    public GuidAudited(Guid id) => this.Id = id;
}

/// <summary>
/// 测试用实体：全量审计 + 软删除。
/// </summary>
internal sealed class GuidFullAudited : FullAuditedEntity<Guid, Guid>
{
    public GuidFullAudited()
    {
    }

    public GuidFullAudited(Guid id) => this.Id = id;
}

/// <summary>
/// 测试用实体：全量审计 + 软删除，且审计人主键为 <see cref="long"/>。
/// </summary>
internal sealed class LongFullAudited : FullAuditedEntity<Guid, long>
{
    public LongFullAudited()
    {
    }
}

/// <summary>
/// 测试用实体：全量审计 + 软删除，且审计人主键为 <see cref="string"/>。
/// </summary>
internal sealed class StringFullAudited : FullAuditedEntity<Guid, string>
{
    public StringFullAudited()
    {
    }
}
