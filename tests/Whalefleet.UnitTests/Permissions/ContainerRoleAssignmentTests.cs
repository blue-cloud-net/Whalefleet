using FluentAssertions;
using Whalefleet.Core.Permissions;
using Xunit;

namespace Whalefleet.UnitTests.Permissions;

public class ContainerRoleAssignmentTests
{
    [Fact]
    public void 容器分配必须同时指定主机()
    {
        var create = () => UserRoleAssignment.Assign(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "container");
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 容器分配不能作为主机分配生效()
    {
        var host = Guid.NewGuid();
        var assignment = UserRoleAssignment.Assign(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), host, "container");
        assignment.IsGlobal.Should().BeFalse();
        assignment.AppliesTo(host).Should().BeFalse();
        assignment.ContainerId.Should().Be("container");
    }
}
