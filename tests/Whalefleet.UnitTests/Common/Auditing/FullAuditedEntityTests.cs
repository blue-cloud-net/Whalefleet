using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Common.Auditing;

/// <summary>
/// <see cref="Core.Common.Auditing.FullAuditedEntity{TId, TUserKey}"/> 的软删除行为测试。
/// </summary>
public class FullAuditedEntityTests
{
    private static readonly DateTime _createdAt = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _modifiedAt = new(2026, 10, 10, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime _deletedAt = new(2026, 10, 10, 10, 45, 0, DateTimeKind.Utc);

    [Fact]
    public void 新建实体_默认未删除且删除审计为空()
    {
        var entity = new GuidFullAudited();

        entity.IsDeleted.Should().BeFalse();
        entity.DeletionTime.Should().BeNull();
        entity.DeleterId.Should().Be(default(Guid));
    }

    [Fact]
    public void Delete_同时置位删除标志并写入时间与删除者()
    {
        var deleter = Guid.NewGuid();
        var entity = new GuidFullAudited();

        entity.Delete(_deletedAt, deleter);

        entity.IsDeleted.Should().BeTrue();
        entity.DeletionTime.Should().Be(_deletedAt);
        entity.DeleterId.Should().Be(deleter);
    }

    [Fact]
    public void Delete_不影响创建与修改审计()
    {
        var creator = Guid.NewGuid();
        var entity = new GuidFullAudited();
        entity.MarkCreated(_createdAt, creator);
        entity.MarkModified(_modifiedAt, creator);

        entity.Delete(_deletedAt, creator);

        entity.CreationTime.Should().Be(_createdAt);
        entity.CreatorId.Should().Be(creator);
        entity.LastModificationTime.Should().Be(_modifiedAt);
        entity.LastModifierId.Should().Be(creator);
    }

    [Fact]
    public void Delete_重复调用为幂等且刷新删除信息()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entity = new GuidFullAudited();
        entity.Delete(_deletedAt, first);

        entity.Delete(_deletedAt.AddHours(1), second);

        entity.IsDeleted.Should().BeTrue();
        entity.DeletionTime.Should().Be(_deletedAt.AddHours(1));
        entity.DeleterId.Should().Be(second);
    }

    [Fact]
    public void Restore_复位删除标志与删除审计()
    {
        var deleter = Guid.NewGuid();
        var entity = new GuidFullAudited();
        entity.Delete(_deletedAt, deleter);

        entity.Restore();

        entity.IsDeleted.Should().BeFalse();
        entity.DeletionTime.Should().BeNull();
        entity.DeleterId.Should().Be(default(Guid));
    }

    [Fact]
    public void Restore_保留创建与修改审计()
    {
        var actor = Guid.NewGuid();
        var entity = new GuidFullAudited();
        entity.MarkCreated(_createdAt, actor);
        entity.MarkModified(_modifiedAt, actor);
        entity.Delete(_deletedAt, actor);

        entity.Restore();

        entity.CreationTime.Should().Be(_createdAt);
        entity.CreatorId.Should().Be(actor);
        entity.LastModificationTime.Should().Be(_modifiedAt);
        entity.LastModifierId.Should().Be(actor);
    }

    [Fact]
    public void Delete后Restore再Delete_删除信息可重复写入()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var entity = new GuidFullAudited();
        entity.Delete(_deletedAt, first);
        entity.Restore();

        entity.Delete(_deletedAt.AddHours(2), second);

        entity.IsDeleted.Should().BeTrue();
        entity.DeletionTime.Should().Be(_deletedAt.AddHours(2));
        entity.DeleterId.Should().Be(second);
    }

    [Fact]
    public void Restore_对未删除实体为幂等()
    {
        var entity = new GuidFullAudited();

        entity.Restore();

        entity.IsDeleted.Should().BeFalse();
        entity.DeletionTime.Should().BeNull();
        entity.DeleterId.Should().Be(default(Guid));
    }

    [Fact]
    public void 审计人主键为long时软删除正常()
    {
        var entity = new LongFullAudited();

        entity.Delete(_deletedAt, 7L);
        entity.IsDeleted.Should().BeTrue();
        entity.DeleterId.Should().Be(7L);

        entity.Restore();
        entity.IsDeleted.Should().BeFalse();
        entity.DeleterId.Should().Be(0L);
    }

    [Fact]
    public void 审计人主键为string时软删除正常且支持空值哨兵()
    {
        var entity = new StringFullAudited();

        entity.Delete(_deletedAt, "admin");
        entity.IsDeleted.Should().BeTrue();
        entity.DeleterId.Should().Be("admin");

        entity.Restore();
        entity.IsDeleted.Should().BeFalse();
        entity.DeleterId.Should().BeNull();
    }

    [Fact]
    public void 软删除不参与相等性判定()
    {
        var id = Guid.NewGuid();
        var a = new GuidFullAudited(id);
        var b = new GuidFullAudited(id);
        b.Delete(_deletedAt, Guid.NewGuid());

        a.IsDeleted.Should().BeFalse();
        b.IsDeleted.Should().BeTrue();
        a.Should().Be(b);
    }
}
