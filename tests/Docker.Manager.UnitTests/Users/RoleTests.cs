using Docker.Manager.Core.Users;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Users;

/// <summary>
/// <see cref="Role"/> 的不变量与行为测试。
/// </summary>
public class RoleTests
{
    private static readonly DateTime _createdAt = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_自定义角色_非内置且非默认()
    {
        var id = Guid.NewGuid();

        var role = Role.Create(id, "auditor", "审计", "只读并可查看操作审计");

        role.Id.Should().Be(id);
        role.Name.Should().Be("auditor");
        role.DisplayName.Should().Be("审计");
        role.Description.Should().Be("只读并可查看操作审计");
        role.IsBuiltIn.Should().BeFalse();
        role.IsDefault.Should().BeFalse();
        role.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Create_角色名自动转小写()
    {
        // 角色名会进 JWT claim 与 [Authorize(Roles = …)]，大小写混用会让鉴权行为不可预期
        var role = Role.Create(Guid.NewGuid(), "  ReadOnly  ");

        role.Name.Should().Be("readonly");
    }

    [Fact]
    public void Create_可读名称与描述为空时回退为null()
    {
        var role = Role.Create(Guid.NewGuid(), "operator");

        role.DisplayName.Should().BeNull();
        role.Description.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("role name")]
    [InlineData("role/name")]
    [InlineData("角色")]
    public void Create_非法角色名_抛出参数异常(string name)
    {
        var act = () => Role.Create(Guid.NewGuid(), name);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_角色名超长_抛出参数异常()
    {
        var name = new string('a', Role.MaxNameLength + 1);

        var act = () => Role.Create(Guid.NewGuid(), name);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_允许的字符集_通过()
    {
        var role = Role.Create(Guid.NewGuid(), "ops.team-1_a");

        role.Name.Should().Be("ops.team-1_a");
    }

    [Fact]
    public void Create_可读名称或描述超长_抛出参数异常()
    {
        var displayName = new string('a', Role.MaxDisplayNameLength + 1);
        var description = new string('a', Role.MaxDescriptionLength + 1);

        var actDisplay = () => Role.Create(Guid.NewGuid(), "x", displayName);
        var actDescription = () => Role.Create(Guid.NewGuid(), "x", "x", description);

        actDisplay.Should().Throw<ArgumentException>();
        actDescription.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateBuiltIn_内置角色不可删除()
    {
        var role = Role.CreateBuiltIn(Guid.NewGuid(), "admin", "管理员", "可管理全部资源");

        role.IsBuiltIn.Should().BeTrue();

        var act = () => role.Retire(_createdAt, Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>().WithMessage("*admin*");
    }

    [Fact]
    public void CreateBuiltIn_可同时设为默认()
    {
        var role = Role.CreateBuiltIn(Guid.NewGuid(), "auditor", "审计", description: null, isDefault: true);

        role.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Retire_自定义角色_软删除成功()
    {
        var deleter = Guid.NewGuid();
        var role = Role.Create(Guid.NewGuid(), "temp-role");

        role.Retire(_createdAt, deleter);

        role.IsDeleted.Should().BeTrue();
        role.DeletionTime.Should().Be(_createdAt);
        role.DeleterId.Should().Be(deleter);
    }

    [Fact]
    public void Retire_内置角色_状态不变()
    {
        var role = Role.CreateBuiltIn(Guid.NewGuid(), "admin");

        var act = () => role.Retire(_createdAt, Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>();
        role.IsDeleted.Should().BeFalse();
        role.DeletionTime.Should().BeNull();
    }

    [Fact]
    public void MakeDefault与ClearDefault_切换默认标记()
    {
        var role = Role.Create(Guid.NewGuid(), "custom");

        role.MakeDefault();
        role.IsDefault.Should().BeTrue();

        role.ClearDefault();
        role.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void ChangeDescription_可改可读名称与描述()
    {
        var role = Role.Create(Guid.NewGuid(), "operator", "运维", "旧描述");

        role.ChangeDescription("  操作员  ", "  新描述  ");

        role.DisplayName.Should().Be("操作员");
        role.Description.Should().Be("新描述");
        role.Name.Should().Be("operator");
    }

    [Fact]
    public void ChangeDescription_文本超长_抛出参数异常且不改动原值()
    {
        var role = Role.Create(Guid.NewGuid(), "operator", "运维", "旧描述");
        var tooLong = new string('a', Role.MaxDescriptionLength + 1);

        var act = () => role.ChangeDescription("运维", tooLong);

        act.Should().Throw<ArgumentException>();
        role.Description.Should().Be("旧描述");
        role.DisplayName.Should().Be("运维");
    }
}