using Docker.Manager.Core.Permissions;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Permissions;

/// <summary>
/// <see cref="UserContainerPermission"/> 的授权与权限位判定测试。
/// </summary>
public class UserContainerPermissionTests
{
    private static readonly Guid _userId = Guid.NewGuid();
    private static readonly Guid _hostId = Guid.NewGuid();

    [Fact]
    public void Grant_写入全部属性且去掉容器标识空白()
    {
        var id = Guid.NewGuid();

        var permission = UserContainerPermission.Grant(
            id,
            _userId,
            _hostId,
            "  nginx-proxy  ",
            ContainerPermissions.Read | ContainerPermissions.Write);

        permission.Id.Should().Be(id);
        permission.UserId.Should().Be(_userId);
        permission.HostId.Should().Be(_hostId);
        permission.ContainerId.Should().Be("nginx-proxy");
        permission.Permissions.Should().Be(ContainerPermissions.Read | ContainerPermissions.Write);
    }

    [Fact]
    public void Grant_用户为空_抛出参数异常()
    {
        var act = () => UserContainerPermission.Grant(Guid.NewGuid(), Guid.Empty, _hostId, "nginx", ContainerPermissions.Read);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Grant_主机为空_抛出参数异常()
    {
        var act = () => UserContainerPermission.Grant(Guid.NewGuid(), _userId, Guid.Empty, "nginx", ContainerPermissions.Read);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Grant_容器标识为空_抛出参数异常(string containerId)
    {
        var act = () => UserContainerPermission.Grant(Guid.NewGuid(), _userId, _hostId, containerId, ContainerPermissions.Read);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Grant_容器标识超长_抛出参数异常()
    {
        var containerId = new string('a', UserContainerPermission.MaxContainerIdLength + 1);

        var act = () => UserContainerPermission.Grant(Guid.NewGuid(), _userId, _hostId, containerId, ContainerPermissions.Read);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Grant_权限位含未定义值_抛出参数异常()
    {
        var act = () => UserContainerPermission.Grant(Guid.NewGuid(), _userId, _hostId, "nginx", (ContainerPermissions)0b0000_0100);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(ContainerPermissions.Read, true)]
    [InlineData(ContainerPermissions.Write, false)]
    [InlineData(ContainerPermissions.None, true)]
    public void Has_按位判定(ContainerPermissions required, bool expected)
    {
        var permission = UserContainerPermission.Grant(Guid.NewGuid(), _userId, _hostId, "nginx", ContainerPermissions.Read);

        permission.Has(required).Should().Be(expected);
    }

    [Fact]
    public void UpdatePermissions_覆盖式更新()
    {
        var permission = UserContainerPermission.Grant(Guid.NewGuid(), _userId, _hostId, "nginx", ContainerPermissions.Read);

        permission.UpdatePermissions(ContainerPermissions.Read | ContainerPermissions.Write);

        permission.Has(ContainerPermissions.Write).Should().BeTrue();
    }
}
