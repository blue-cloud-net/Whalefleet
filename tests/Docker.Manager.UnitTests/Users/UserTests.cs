using Docker.Manager.Core.Users;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Users;

/// <summary>
/// <see cref="User"/> 实体的不变量与行为测试。
/// </summary>
/// <remarks>
/// 这里验证的都是「不需要查库」的规则——用户名的格式、密码哈希非空、角色外键非空。
/// 「用户名是否已被占用」「是否还有别的管理员」「角色是否存在」需要跨行判断，属于应用服务职责。
/// </remarks>
public class UserTests
{
    private const string _validHash = "$argon2id$v=19$m=65536,t=3,p=1$c2FsdA$aGFzaA";

    private static readonly DateTime _createdAt = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    private static readonly Guid _roleId = new("00000000-0000-0000-0000-0000000000a2");

    private static readonly Guid _otherRoleId = new("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public void Create_合法入参_写入全部属性且默认未删除()
    {
        var id = Guid.NewGuid();

        var user = User.Create(id, "admin", _validHash, _roleId);

        user.Id.Should().Be(id);
        user.Username.Should().Be("admin");
        user.PasswordHash.Should().Be(_validHash);
        user.RoleId.Should().Be(_roleId);
        user.IsDeleted.Should().BeFalse();

        // 审计由仓储在写库前填充，实体自身不取当前时间也不取当前用户
        user.CreationTime.Should().Be(default(DateTime));
        user.CreatorId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Create_去掉用户名首尾空白()
    {
        var user = User.Create(Guid.NewGuid(), "  admin  ", _validHash, _roleId);

        user.Username.Should().Be("admin");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("admin user")]
    [InlineData("admin@example")]
    [InlineData("管理员")]
    public void Create_非法用户名_抛出参数异常(string username)
    {
        var act = () => User.Create(Guid.NewGuid(), username, _validHash, _roleId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_用户名超长_抛出参数异常()
    {
        var username = new string('a', User.MaxUsernameLength + 1);

        var act = () => User.Create(Guid.NewGuid(), username, _validHash, _roleId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_用户名达到长度上限_允许()
    {
        var username = new string('a', User.MaxUsernameLength);

        var user = User.Create(Guid.NewGuid(), username, _validHash, _roleId);

        user.Username.Should().Be(username);
    }

    [Theory]
    [InlineData("abc.def")]
    [InlineData("abc_def")]
    [InlineData("abc-def")]
    [InlineData("User123")]
    public void Create_允许的字符集_通过(string username)
    {
        var user = User.Create(Guid.NewGuid(), username, _validHash, _roleId);

        user.Username.Should().Be(username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_密码哈希为空_抛出参数异常(string passwordHash)
    {
        var act = () => User.Create(Guid.NewGuid(), "admin", passwordHash, _roleId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_角色为空Guid_抛出参数异常()
    {
        var act = () => User.Create(Guid.NewGuid(), "admin", _validHash, Guid.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_任意角色外键均可_因为角色是运行时数据()
    {
        // 角色不再由枚举约束：实体只保证「非空」，角色是否存在由外键与查询判定。
        // 这正是「角色可运行时添加」在实体层的体现——新角色不需要改这里的代码。
        var unknownRoleId = Guid.NewGuid();

        var user = User.Create(Guid.NewGuid(), "admin", _validHash, unknownRoleId);

        user.RoleId.Should().Be(unknownRoleId);
    }

    [Fact]
    public void ChangePassword_覆盖旧哈希()
    {
        var user = User.Create(Guid.NewGuid(), "admin", _validHash, _roleId);

        user.ChangePassword("$argon2id$v=19$m=65536,t=3,p=1$bmV3$aGFzaA");

        user.PasswordHash.Should().Be("$argon2id$v=19$m=65536,t=3,p=1$bmV3$aGFzaA");
    }

    [Fact]
    public void ChangePassword_空哈希_抛出参数异常且不改动原值()
    {
        var user = User.Create(Guid.NewGuid(), "admin", _validHash, _roleId);

        var act = () => user.ChangePassword("  ");

        act.Should().Throw<ArgumentException>();
        user.PasswordHash.Should().Be(_validHash);
    }

    [Fact]
    public void ChangeRole_更新角色外键()
    {
        var user = User.Create(Guid.NewGuid(), "admin", _validHash, _roleId);

        user.ChangeRole(_otherRoleId);

        user.RoleId.Should().Be(_otherRoleId);
    }

    [Fact]
    public void ChangeRole_空Guid_抛出参数异常且不改动原值()
    {
        var user = User.Create(Guid.NewGuid(), "admin", _validHash, _roleId);

        var act = () => user.ChangeRole(Guid.Empty);

        act.Should().Throw<ArgumentException>();
        user.RoleId.Should().Be(_roleId);
    }

    [Fact]
    public void 继承的软删除_可用且保留创建审计()
    {
        var creator = Guid.NewGuid();
        var deleter = Guid.NewGuid();
        var user = User.Create(Guid.NewGuid(), "admin", _validHash, _roleId);
        user.MarkCreated(_createdAt, creator);

        user.Delete(_createdAt.AddHours(1), deleter);

        user.IsDeleted.Should().BeTrue();
        user.DeletionTime.Should().Be(_createdAt.AddHours(1));
        user.DeleterId.Should().Be(deleter);
        user.CreationTime.Should().Be(_createdAt);
        user.CreatorId.Should().Be(creator);

        user.Restore();

        user.IsDeleted.Should().BeFalse();
        user.DeletionTime.Should().BeNull();
    }
}