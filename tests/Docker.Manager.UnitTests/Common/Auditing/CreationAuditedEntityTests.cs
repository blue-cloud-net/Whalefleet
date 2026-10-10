using Docker.Manager.Core.Common.Auditing;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Common.Auditing;

/// <summary>
/// <see cref="CreationAuditedEntity{TId, TUserKey}"/> 的行为测试。
/// </summary>
public class CreationAuditedEntityTests
{
    private static readonly DateTime _createdAt = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MarkCreated_写入创建时间与创建者()
    {
        var creator = Guid.NewGuid();
        var entity = new GuidCreationAudited();

        entity.MarkCreated(_createdAt, creator);

        entity.CreationTime.Should().Be(_createdAt);
        entity.CreatorId.Should().Be(creator);
    }

    [Fact]
    public void MarkCreated_重复调用以最后一次为准()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entity = new GuidCreationAudited();
        entity.MarkCreated(_createdAt, first);

        entity.MarkCreated(_createdAt.AddHours(2), second);

        entity.CreationTime.Should().Be(_createdAt.AddHours(2));
        entity.CreatorId.Should().Be(second);
    }

    [Fact]
    public void 新建实体_审计字段取默认值()
    {
        var entity = new GuidCreationAudited();

        entity.CreationTime.Should().Be(default);
        entity.CreatorId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void MarkCreated_支持系统操作哨兵值()
    {
        var entity = new GuidCreationAudited();

        entity.MarkCreated(_createdAt, default);

        entity.CreatorId.Should().Be(default(Guid));
    }

    [Fact]
    public void 审计人主键为long时正常写入()
    {
        var entity = new LongCreationAudited();

        entity.MarkCreated(_createdAt, 42L);

        entity.CreatorId.Should().Be(42L);
    }

    [Fact]
    public void 审计人主键为string时正常写入且支持空值哨兵()
    {
        var entity = new StringCreationAudited();

        entity.MarkCreated(_createdAt, "admin");
        entity.CreatorId.Should().Be("admin");

        entity.MarkCreated(_createdAt, default!);
        entity.CreatorId.Should().BeNull();
    }

    [Fact]
    public void 相等性沿用Entity按Id判定()
    {
        var id = Guid.NewGuid();

        var a = new GuidCreationAudited(id);
        var b = new GuidCreationAudited(id);
        var c = new GuidCreationAudited();

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.Should().NotBe(c);
        (a != c).Should().BeTrue();
    }
}
