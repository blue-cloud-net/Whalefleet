using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Common.Auditing;

/// <summary>
/// <see cref="Core.Common.Auditing.AuditedEntity{TId, TUserKey}"/> 的行为测试。
/// </summary>
public class AuditedEntityTests
{
    private static readonly DateTime _createdAt = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _modifiedAt = new(2026, 10, 10, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void 新建实体_修改审计为空_创建审计取默认值()
    {
        var entity = new GuidAudited();

        entity.LastModificationTime.Should().BeNull();
        entity.LastModifierId.Should().Be(default(Guid));
        entity.CreationTime.Should().Be(default);
        entity.CreatorId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void MarkModified_不影响创建审计()
    {
        var creator = Guid.NewGuid();
        var modifier = Guid.NewGuid();
        var entity = new GuidAudited();
        entity.MarkCreated(_createdAt, creator);

        entity.MarkModified(_modifiedAt, modifier);

        entity.LastModificationTime.Should().Be(_modifiedAt);
        entity.LastModifierId.Should().Be(modifier);
        entity.CreationTime.Should().Be(_createdAt);
        entity.CreatorId.Should().Be(creator);
    }

    [Fact]
    public void MarkModified_重复调用以最后一次为准()
    {
        var entity = new GuidAudited();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        entity.MarkModified(_modifiedAt, first);

        entity.MarkModified(_modifiedAt.AddHours(1), second);

        entity.LastModificationTime.Should().Be(_modifiedAt.AddHours(1));
        entity.LastModifierId.Should().Be(second);
    }

    [Fact]
    public void MarkModified_支持系统操作哨兵值()
    {
        var entity = new GuidAudited();

        entity.MarkModified(_modifiedAt, default);

        entity.LastModifierId.Should().Be(default(Guid));
    }

    [Fact]
    public void 相等性沿用Entity按Id判定()
    {
        var id = Guid.NewGuid();

        var a = new GuidAudited(id);
        var b = new GuidAudited(id);
        var c = new GuidAudited();

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(c);
    }
}
