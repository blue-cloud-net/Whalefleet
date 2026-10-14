using Whalefleet.Core.Permissions;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Permissions;

/// <summary>
/// <see cref="UserHostPermission"/> 的授权与权限位判定测试。
/// </summary>
public class UserHostPermissionTests
{
    private static readonly Guid _userId = Guid.NewGuid();
    private static readonly Guid _hostId = Guid.NewGuid();

    [Fact]
    public void Grant_写入全部属性()
    {
        var id = Guid.NewGuid();

        var permission = UserHostPermission.Grant(id, _userId, _hostId, HostPermissions.Read | HostPermissions.Write);

        permission.Id.Should().Be(id);
        permission.UserId.Should().Be(_userId);
        permission.HostId.Should().Be(_hostId);
        permission.Permissions.Should().Be(HostPermissions.Read | HostPermissions.Write);
        permission.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Grant_用户为空_抛出参数异常()
    {
        var act = () => UserHostPermission.Grant(Guid.NewGuid(), Guid.Empty, _hostId, HostPermissions.Read);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Grant_主机为空_抛出参数异常()
    {
        var act = () => UserHostPermission.Grant(Guid.NewGuid(), _userId, Guid.Empty, HostPermissions.Read);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Grant_权限位含未定义值_抛出参数异常()
    {
        var act = () => UserHostPermission.Grant(Guid.NewGuid(), _userId, _hostId, (HostPermissions)0b1000_0000);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(HostPermissions.Read, true)]
    [InlineData(HostPermissions.Write, true)]
    [InlineData(HostPermissions.Admin, false)]
    [InlineData(HostPermissions.None, true)]
    public void Has_按位判定(HostPermissions required, bool expected)
    {
        var permission = UserHostPermission.Grant(
            Guid.NewGuid(),
            _userId,
            _hostId,
            HostPermissions.Read | HostPermissions.Write);

        permission.Has(required).Should().Be(expected);
    }

    [Fact]
    public void Has_组合权限_须全部具备()
    {
        var permission = UserHostPermission.Grant(Guid.NewGuid(), _userId, _hostId, HostPermissions.Read);

        permission.Has(HostPermissions.Read | HostPermissions.Write).Should().BeFalse();

        permission.UpdatePermissions(HostPermissions.Read | HostPermissions.Write | HostPermissions.Admin);

        permission.Has(HostPermissions.Read | HostPermissions.Write).Should().BeTrue();
    }

    [Fact]
    public void Has_权限位含未定义值_抛出参数异常()
    {
        var permission = UserHostPermission.Grant(Guid.NewGuid(), _userId, _hostId, HostPermissions.Read);

        var act = () => permission.Has((HostPermissions)0b1000_0000);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdatePermissions_覆盖式更新()
    {
        var permission = UserHostPermission.Grant(Guid.NewGuid(), _userId, _hostId, HostPermissions.Read);

        permission.UpdatePermissions(HostPermissions.Admin);

        permission.Permissions.Should().Be(HostPermissions.Admin);
        permission.Has(HostPermissions.Read).Should().BeFalse();
    }

    [Fact]
    public void UpdatePermissions_权限位非法_抛出参数异常且不改动原值()
    {
        var permission = UserHostPermission.Grant(Guid.NewGuid(), _userId, _hostId, HostPermissions.Read);

        var act = () => permission.UpdatePermissions((HostPermissions)0b1000_0000);

        act.Should().Throw<ArgumentException>();
        permission.Permissions.Should().Be(HostPermissions.Read);
    }

    [Fact]
    public void 撤销授权_走软删除()
    {
        var deleter = Guid.NewGuid();
        var revokedAt = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var permission = UserHostPermission.Grant(Guid.NewGuid(), _userId, _hostId, HostPermissions.Read);

        permission.Delete(revokedAt, deleter);

        permission.IsDeleted.Should().BeTrue();
        permission.DeletionTime.Should().Be(revokedAt);
        permission.DeleterId.Should().Be(deleter);
    }
}
