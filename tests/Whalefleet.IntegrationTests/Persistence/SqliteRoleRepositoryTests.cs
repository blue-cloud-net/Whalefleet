using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Persistence;

/// <summary>
/// <c>SqliteRoleRepository</c> 与角色迁移脚本的集成测试。
/// </summary>
/// <remarks>
/// 覆盖三类只有真库才能证明的契约：
/// <list type="number">
///   <item><b>种子</b>：迁移必须种入内置角色、且全库恰有一个默认角色；</item>
///   <item><b>可扩展性</b>：运行时新增角色可用、可被用户引用，名字唯一且删除后可复用；</item>
///   <item><b>不变量</b>：内置角色删不掉、默认角色至多一个。</item>
/// </list>
/// </remarks>
public class SqliteRoleRepositoryTests
{
    private static readonly Guid _operatorId = new("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task 迁移_种入四个内置角色()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var list = await roles.QueryAllListAsync();

        list.Select(role => role.Name).Should().Contain(
        [
            "admin",
            "operator",
            "viewer",
            "auditor",
        ]);
        list.Should().OnlyContain(role => role.IsBuiltIn);
    }

    [Fact]
    public async Task 迁移_内置角色带可读名称与职责描述()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var admin = await roles.FindByNameAsync("admin");
        var operatorRole = await roles.FindByNameAsync("operator");

        admin.Should().NotBeNull();
        admin!.DisplayName.Should().Be("管理员");
        admin.Description.Should().Contain("可管理用户、主机与全部资源");

        operatorRole!.DisplayName.Should().Be("运维");
        operatorRole.Description.Should().Contain("容器");
    }

    [Fact]
    public async Task 迁移_种子创建时间固定_保证可复现()
    {
        await using var host = await CreateHostAsync();

        (await host.QueryScalarAsync("SELECT creation_time FROM roles WHERE name = 'admin'"))
            .Should().Be("2026-01-01 00:00:00");
    }

    [Fact]
    public async Task 迁移_全库恰有一个默认角色且种子行按主键固定()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        (await host.QueryScalarAsync("SELECT COUNT(1) FROM roles WHERE is_default = 1")).Should().Be("1");

        var defaultRole = await roles.GetDefaultAsync();
        defaultRole.Should().NotBeNull();
        defaultRole!.Name.Should().Be("auditor");
        defaultRole.IsDefault.Should().BeTrue();

        // 内置角色主键固定，运维脚本与文档可引用
        (await roles.FindByNameAsync("admin"))!.Id
            .Should().Be(new Guid("00000000-0000-0000-0000-0000000000a1"));
    }

    [Fact]
    public async Task AddAsync_运行时新增自定义角色()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var role = Role.Create(Guid.NewGuid(), "NightShift", "夜班运维", "仅夜班时段可用");
        await roles.AddAsync(role);

        var loaded = await roles.QueryByIdAsync(role.Id);
        loaded!.Name.Should().Be("nightshift");
        loaded.DisplayName.Should().Be("夜班运维");
        loaded.IsBuiltIn.Should().BeFalse();
        loaded.CreationTime.Should().Be(PersistenceTestHost.Now.UtcDateTime);
        loaded.CreatorId.Should().Be(_operatorId);

        (await host.QueryScalarAsync($"SELECT name FROM roles WHERE id = '{role.Id:D}'"))
            .Should().Be("nightshift");
    }

    [Fact]
    public async Task 角色名唯一约束_不区分大小写()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var act = () => roles.AddAsync(Role.Create(Guid.NewGuid(), "ADMIN"));

        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public async Task RemoveAsync_自定义角色可删且同名可复用()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var role = Role.Create(Guid.NewGuid(), "temp", "临时");
        await roles.AddAsync(role);

        host.TimeProvider.Advance(TimeSpan.FromMinutes(10));
        (await roles.RemoveByIdAsync(role.Id)).Should().BeTrue();

        (await roles.QueryByIdAsync(role.Id)).Should().BeNull();
        (await host.QueryScalarAsync("SELECT is_deleted FROM roles WHERE name = 'temp'")).Should().Be("1");

        // 部分唯一索引（WHERE is_deleted = 0）让同名可再次创建
        await roles.AddAsync(Role.Create(Guid.NewGuid(), "temp"));
        (await roles.FindByNameAsync("temp")).Should().NotBeNull();
    }

    /// <summary>
    /// 内置角色：仓储返回 <see langword="false"/> 而不抛异常（“未命中”用 false 表达是本层的统一契约）。
    /// 面向用户的报错由应用服务根据 <c>role.IsBuiltIn</c> 提前拦下。
    /// </summary>
    [Theory]
    [InlineData("admin")]
    [InlineData("operator")]
    [InlineData("viewer")]
    [InlineData("auditor")]
    public async Task RemoveAsync_内置角色删不掉(string name)
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var role = (await roles.FindByNameAsync(name))!;

        (await roles.RemoveByIdAsync(role.Id)).Should().BeFalse();
        (await host.QueryScalarAsync($"SELECT is_deleted FROM roles WHERE name = '{name}'")).Should().Be("0");
    }

    [Fact]
    public async Task RemoveAsync_内置角色即便绕过实体也被数据层拒绝()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();
        var admin = (await roles.FindByNameAsync("admin"))!;

        // 模拟"调用方图省事直接调基类 Delete"：实体绕过了 Retire 的内置检查，
        // 仓储的 WHERE is_built_in = 0 才是无法绕过的兜底。
        admin.Delete(PersistenceTestHost.Now.UtcDateTime, _operatorId);

        (await roles.RemoveByIdAsync(admin.Id)).Should().BeFalse();
        (await host.QueryScalarAsync("SELECT is_deleted FROM roles WHERE name = 'admin'")).Should().Be("0");
    }

    [Fact]
    public async Task SetDefaultAsync_改默认后全库仍只有一个()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var viewer = (await roles.FindByNameAsync("viewer"))!;
        (await roles.SetDefaultAsync(viewer.Id)).Should().BeTrue();

        (await host.QueryScalarAsync("SELECT COUNT(1) FROM roles WHERE is_default = 1")).Should().Be("1");
        (await roles.GetDefaultAsync())!.Name.Should().Be("viewer");

        // 旧的默认角色标记被清掉，并留下修改审计
        var auditor = (await roles.FindByNameAsync("auditor"))!;
        auditor.IsDefault.Should().BeFalse();
        (await host.QueryScalarAsync("SELECT last_modification_time FROM roles WHERE name = 'viewer'"))
            .Should().StartWith("2026-10-10 08:00:00");
    }

    [Fact]
    public async Task SetDefaultAsync_目标不存在_返回false且不改任何行()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        (await roles.SetDefaultAsync(Guid.NewGuid())).Should().BeFalse();

        // 失败路径不得影响既有默认角色
        (await roles.GetDefaultAsync())!.Name.Should().Be("auditor");
        (await host.QueryScalarAsync("SELECT COUNT(1) FROM roles WHERE is_default = 1")).Should().Be("1");
    }

    [Fact]
    public async Task UpdateAsync_可改可读名称与描述但角色名不变()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();

        var operatorRole = (await roles.FindByNameAsync("operator"))!;
        operatorRole.ChangeDescription("操作员", "仅可操作已授权主机");

        (await roles.UpdateAsync(operatorRole)).Should().BeTrue();

        var loaded = (await roles.FindByNameAsync("operator"))!;
        loaded.Name.Should().Be("operator");
        loaded.DisplayName.Should().Be("操作员");
        loaded.Description.Should().Be("仅可操作已授权主机");
        loaded.LastModifierId.Should().Be(_operatorId);
    }

    [Fact]
    public async Task 新用户可被分配运行时新增的角色()
    {
        await using var host = await CreateHostAsync();
        var roles = host.Services.GetRequiredService<IRoleRepository>();
        var users = host.Services.GetRequiredService<IUserRepository>();
        var assignments = host.Services.GetRequiredService<IUserRoleAssignmentRepository>();

        var role = Role.Create(Guid.NewGuid(), "nightshift", "夜班运维");
        await roles.AddAsync(role);

        var userId = await TestAccounts.CreateUserAsync(
            users,
            assignments,
            "alice",
            "$argon2id$stub",
            role);

        var loaded = (await users.FindByUsernameAsync("alice"))!;
        loaded.Should().NotBeNull();

        // 角色是「分配」而非用户属性：这条用例证明**运行时新增**的角色
        // 不需要改任何代码就能被分配出去——这正是 0.0.3 把角色从枚举改成表的初衷。
        var userAssignments = await assignments.ListByUserAsync(userId);
        userAssignments.Should().ContainSingle();
        userAssignments[0].RoleId.Should().Be(role.Id);
        userAssignments[0].IsGlobal.Should().BeTrue();

        (await assignments.CountByRoleIdAsync(role.Id)).Should().Be(1);
    }

    private static async Task<PersistenceTestHost> CreateHostAsync()
    {
        var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        return host;
    }
}