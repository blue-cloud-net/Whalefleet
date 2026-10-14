using Whalefleet.Core.Users;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Persistence;

/// <summary>
/// <c>SqliteUserRepository</c> 的持久化行为集成测试。
/// </summary>
/// <remarks>
/// 这组用例覆盖三类「只有真库才能证明」的契约：
/// <list type="number">
///   <item>Dapper 映射（snake_case 列 → PascalCase 属性、枚举以小写文本落库、Guid 以规范文本落库）；</item>
///   <item>审计填充（时间来自 <c>TimeProvider</c>、操作人来自 <c>ICurrentUser</c>）；</item>
///   <item>软删除语义（默认过滤、部分唯一索引让业务键可复用）。</item>
/// </list>
/// </remarks>
public class SqliteUserRepositoryTests
{
    private const string _passwordHash = "$argon2id$v=19$m=65536,t=3,p=1$c2FsdA$aGFzaA";

    private static readonly Guid _operatorId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>迁移种入的 operator 角色（“普通可操作”）。</summary>
    private static readonly Guid _operatorRoleId = new("00000000-0000-0000-0000-0000000000a2");

    /// <summary>迁移种入的 admin 角色。</summary>
    private static readonly Guid _adminRoleId = new("00000000-0000-0000-0000-0000000000a1");

    [Fact]
    public async Task AddAsync_填充创建审计并可用主键读回()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var user = CreateUser("admin");

        await users.AddAsync(user);
        var loaded = await users.QueryByIdAsync(user.Id);

        loaded.Should().NotBeNull();

        // Dapper 物化：私有构造 + 非公开 setter 不影响赋值；snake_case 列必须映射成功
        loaded!.Id.Should().Be(user.Id);
        loaded.Username.Should().Be("admin");
        loaded.PasswordHash.Should().Be(_passwordHash);
        loaded.RoleId.Should().Be(_adminRoleId);

        // 审计填充：时间取自 TimeProvider，操作人取自 ICurrentUser
        loaded.CreationTime.Should().Be(PersistenceTestHost.Now.UtcDateTime);
        loaded.CreationTime.Kind.Should().Be(DateTimeKind.Utc);
        loaded.CreatorId.Should().Be(_operatorId);
        loaded.LastModificationTime.Should().BeNull();
        loaded.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task 落库形态_角色外键与主键为规范小写文本()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var user = CreateUser("admin", _adminRoleId);

        await users.AddAsync(user);

        (await host.QueryScalarAsync("SELECT role_id FROM users")).Should().Be(_adminRoleId.ToString("D"));
        (await host.QueryScalarAsync("SELECT id FROM users")).Should().Be(user.Id.ToString("D"));
        (await host.QueryScalarAsync("SELECT creator_id FROM users")).Should().Be(_operatorId.ToString("D"));
        (await host.QueryScalarAsync("SELECT is_deleted FROM users")).Should().Be("0");

        // 时间为 UTC TEXT，不带时区偏移
        (await host.QueryScalarAsync("SELECT creation_time FROM users")).Should().StartWith("2026-10-10 08:00:00");
    }

    [Fact]
    public async Task 未认证时_审计人落到默认值哨兵()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var user = CreateUser("admin");

        await users.AddAsync(user);

        (await host.QueryScalarAsync("SELECT creator_id FROM users")).Should().Be(Guid.Empty.ToString("D"));
    }

    [Fact]
    public async Task FindByUsernameAsync_不区分大小写()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        await users.AddAsync(CreateUser("AdminUser"));

        (await users.FindByUsernameAsync("adminuser")).Should().NotBeNull();
        (await users.FindByUsernameAsync("ADMINUSER"))!.Username.Should().Be("AdminUser");
        (await users.FindByUsernameAsync("nobody")).Should().BeNull();
    }

    [Fact]
    public async Task ExistsByUsernameAsync_忽略已软删除的用户()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var user = CreateUser("admin");
        await users.AddAsync(user);

        (await users.ExistsByUsernameAsync("ADMIN")).Should().BeTrue();

        await users.RemoveByIdAsync(user.Id);

        (await users.ExistsByUsernameAsync("admin")).Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_按创建时间升序且支持分页()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();

        var first = CreateUser("first");
        await users.AddAsync(first);
        host.TimeProvider.Advance(TimeSpan.FromMinutes(1));

        var second = CreateUser("second");
        await users.AddAsync(second);
        host.TimeProvider.Advance(TimeSpan.FromMinutes(1));

        var third = CreateUser("third");
        await users.AddAsync(third);

        var all = await users.ListAsync(0, 10);
        all.Select(user => user.Username).Should().ContainInOrder("first", "second", "third");

        var page = await users.ListAsync(1, 1);
        page.Should().ContainSingle().Which.Username.Should().Be("second");
    }

    [Fact]
    public async Task ListAsync_参数越界_抛出参数异常()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();

        await FluentActions.Awaiting(() => users.ListAsync(-1, 10))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(() => users.ListAsync(0, 0))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task 计数_忽略已软删除的用户()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var admin = CreateUser("admin", _adminRoleId);
        var member = CreateUser("member", _operatorRoleId);
        var removed = CreateUser("removed", _operatorRoleId);

        await users.AddAsync(admin);
        await users.AddAsync(member);
        await users.AddAsync(removed);

        (await users.CountAsync()).Should().Be(3);
        (await users.CountByRoleIdAsync(_adminRoleId)).Should().Be(1);
        (await users.CountByRoleIdAsync(_operatorRoleId)).Should().Be(2);

        await users.RemoveByIdAsync(removed.Id);

        (await users.CountAsync()).Should().Be(2);
        (await users.CountByRoleIdAsync(_operatorRoleId)).Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_写入修改审计并持久化新密码与角色()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var user = CreateUser("admin", _operatorRoleId);
        await users.AddAsync(user);

        host.TimeProvider.Advance(TimeSpan.FromHours(2));
        user.ChangePassword("$argon2id$v=19$m=65536,t=3,p=1$bmV3$aGFzaA");
        user.ChangeRole(_adminRoleId);

        var updated = await users.UpdateAsync(user);

        updated.Should().BeTrue();

        var loaded = (await users.QueryByIdAsync(user.Id))!;
        loaded.PasswordHash.Should().Be("$argon2id$v=19$m=65536,t=3,p=1$bmV3$aGFzaA");
        loaded.RoleId.Should().Be(_adminRoleId);
        loaded.LastModificationTime.Should().Be(PersistenceTestHost.Now.AddHours(2).UtcDateTime);
        loaded.LastModifierId.Should().Be(_operatorId);
    }

    [Fact]
    public async Task UpdateAsync_目标不存在_返回false()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();

        var updated = await users.UpdateAsync(CreateUser("ghost"));

        updated.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_软删除_查询不可见但审计完整()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var user = CreateUser("admin");
        await users.AddAsync(user);

        host.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        (await users.RemoveByIdAsync(user.Id)).Should().BeTrue();

        (await users.QueryByIdAsync(user.Id)).Should().BeNull();
        (await users.CountAsync()).Should().Be(0);
        (await users.FindByUsernameAsync("admin")).Should().BeNull();

        // 行仍在（软删除），且删除审计来自 TimeProvider / ICurrentUser
        (await host.QueryScalarAsync("SELECT is_deleted FROM users")).Should().Be("1");
        (await host.QueryScalarAsync("SELECT deleter_id FROM users")).Should().Be(_operatorId.ToString("D"));
        (await host.QueryScalarAsync("SELECT deletion_time FROM users")).Should().StartWith("2026-10-10 08:05:00");
    }

    [Fact]
    public async Task RemoveAsync_重复调用_幂等且返回false()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var user = CreateUser("admin");
        await users.AddAsync(user);

        (await users.RemoveByIdAsync(user.Id)).Should().BeTrue();
        (await users.RemoveByIdAsync(user.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task 用户名唯一约束_不区分大小写()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        await users.AddAsync(CreateUser("admin"));

        var act = () => users.AddAsync(CreateUser("ADMIN"));

        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task 软删除后_同名可再次创建()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var users = host.Services.GetRequiredService<IUserRepository>();
        var original = CreateUser("admin");
        await users.AddAsync(original);
        await users.RemoveByIdAsync(original.Id);

        // 部分唯一索引（WHERE is_deleted = 0）让业务键可复用
        var recreated = CreateUser("ADMIN");
        await users.AddAsync(recreated);

        (await users.FindByUsernameAsync("admin")).Should().NotBeNull();
        (await users.CountAsync()).Should().Be(1);
    }

    private static User CreateUser(string username, Guid? roleId = null) =>
        User.Create(Guid.NewGuid(), username, _passwordHash, roleId ?? _adminRoleId);
}
