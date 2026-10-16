using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.Infrastructure.Permissions;
using Whalefleet.IntegrationTests.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whalefleet.IntegrationTests.Permissions;

/// <summary>
/// <c>PermissionChecker</c> 的集成测试——**0.1.0 授权模型唯一一份生效规则**。
/// </summary>
/// <remarks>
/// <para><b>为什么必须用真库而不是桩仓储</b>：这三条规则都写在「SQL 查回来的分配」
/// 与「表里种着的权限」之间。桩仓储只能证明内存里的 LINQ 写对了，
/// 证明不了迁移种下的权限集、软删除过滤与 <c>COALESCE(host_id,'')</c> 唯一索引的配合正确。</para>
/// <para><b>覆盖的 roadmap 验收</b>：
/// <list type="number">
///   <item><description>验收 2：A 主机 operator / B 主机 viewer 权限互不串味；</description></item>
///   <item><description>验收 5：主机分配里的 admin 不带来全局权限；</description></item>
///   <item><description>验收 1 与 3 的判定侧：viewer 只读 / operator 可写、改授权即时生效。</description></item>
/// </list>
/// 最容易回归且最难靠肉眼发现的是<b>缓存键必须带 hostId</b>——
/// 一旦退化成按 userId 缓存，「A 主机的 operator」会被误用到 B 主机，
/// 那是<b>越权</b>，不是精度问题，且不抛任何异常。</para>
/// </remarks>
public class PermissionCheckerTests
{
    /// <summary>占位哈希：本组用例不经过登录流程，只为满足 <c>users.password_hash</c> 的非空约束。</summary>
    private const string _passwordHash = "$argon2id$v=19$m=1,t=1,p=1$0000$0000";

    // ===== 验收 2：作用域隔离 =====

    [Fact]
    public async Task 同一用户在两台主机上角色不同_权限互不串味()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operatorRole = await fixture.FindRoleAsync(RoleNames.Operator);
        var viewerRole = await fixture.FindRoleAsync(RoleNames.Viewer);

        // A 主机 = operator（可写），B 主机 = viewer（只读）
        await fixture.AssignAsync(operatorRole, fixture.HostAId);
        await fixture.AssignAsync(viewerRole, fixture.HostBId);

        var checker = fixture.CreateChecker();

        var onA = await checker.GetEffectivePermissionsAsync(fixture.UserId, fixture.HostAId);
        var onB = await checker.GetEffectivePermissionsAsync(fixture.UserId, fixture.HostBId);

        onA.Should().Contain(Permission.ContainerWrite);
        onB.Should().NotContain(Permission.ContainerWrite);

        onA.Should().Contain(Permission.ContainerRead);
        onB.Should().Contain(Permission.ContainerRead);
    }

    [Fact]
    public async Task 缓存键必须带主机_交替查询两台主机不会互相污染()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Operator), fixture.HostAId);

        var checker = fixture.CreateChecker();

        // 交替两轮：只查一轮的话，「第一个键被缓存后一直命中」的退化写法也能通过。
        for (var round = 0; round < 2; round++)
        {
            var onA = await checker.GetEffectivePermissionsAsync(fixture.UserId, fixture.HostAId);
            var onB = await checker.GetEffectivePermissionsAsync(fixture.UserId, fixture.HostBId);

            onA.Should().Contain(Permission.ContainerWrite);
            onB.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task 主机分配在别的主机上完全不生效()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Operator), fixture.HostAId);

        var checker = fixture.CreateChecker();

        var elsewhere = await checker.GetEffectivePermissionsAsync(fixture.UserId, fixture.HostBId);
        elsewhere.Should().BeEmpty();
    }

    // ===== 验收 5：主机分配里的 admin 不带来全局权限 =====

    [Fact]
    public async Task 主机分配的admin只带来主机域权限_全局权限被忽略()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(Role.AdminName), fixture.HostAId);

        var checker = fixture.CreateChecker(role: Role.AdminName);

        var onHost = await checker.GetEffectivePermissionsAsync(fixture.UserId, fixture.HostAId);

        // Host 域权限生效
        onHost.Should().Contain(Permission.HostRead);
        onHost.Should().Contain(Permission.ContainerWrite);

        // ⚠️ 核心断言：Global 域权限必须被忽略，
        // 否则「把 admin 分配到某台主机」就是一次静默的系统级提权。
        onHost.Should().NotContain(Permission.UserWrite);
        onHost.Should().NotContain(Permission.RoleWrite);
        onHost.Should().NotContain(Permission.HostManage);
        onHost.Should().NotContain(Permission.AuditRead);
    }

    [Fact]
    public async Task 主机分配的admin_全局作用域下没有任何权限()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(Role.AdminName), fixture.HostAId);

        var checker = fixture.CreateChecker(role: Role.AdminName);

        var global = await checker.GetEffectivePermissionsAsync(fixture.UserId, hostId: null);
        global.Should().BeEmpty();
    }

    [Fact]
    public async Task 主机分配的admin不构成系统管理员()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(Role.AdminName), fixture.HostAId);

        var checker = fixture.CreateChecker(role: Role.AdminName);

        (await checker.IsAdministratorAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task 全局分配的admin才是系统管理员()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(Role.AdminName), hostId: null);

        var checker = fixture.CreateChecker(role: Role.AdminName);

        (await checker.IsAdministratorAsync()).Should().BeTrue();
    }

    // ===== 验收 1 的判定侧：viewer / operator =====

    [Fact]
    public async Task viewer只有读权限_ensure拒绝写操作()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Viewer), fixture.HostAId);

        var checker = fixture.CreateChecker();

        (await checker.HasPermissionAsync(fixture.UserId, fixture.HostAId, Permission.ContainerRead))
            .Should().BeTrue();
        (await checker.HasPermissionAsync(fixture.UserId, fixture.HostAId, Permission.ContainerWrite))
            .Should().BeFalse();

        var act = async () => await checker
            .EnsurePermissionAsync(fixture.UserId, fixture.HostAId, Permission.ContainerWrite);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage($"*{Permission.ContainerWrite}*");
    }

    [Fact]
    public async Task 改授operator后下一次请求即可写_无需等待令牌过期()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operatorRole = await fixture.FindRoleAsync(RoleNames.Operator);
        var viewerRole = await fixture.FindRoleAsync(RoleNames.Viewer);

        var viewerAssignment = await fixture.AssignAsync(viewerRole, fixture.HostAId);

        var before = fixture.CreateChecker();
        (await before.HasPermissionAsync(fixture.UserId, fixture.HostAId, Permission.ContainerWrite))
            .Should().BeFalse();

        // 撤销 viewer、改授 operator。**新建一个 checker** 模拟下一次请求——
        // 同一个 checker 的请求级缓存本来就不该看到新授权，那是快照语义而非缺陷。
        await fixture.RemoveAssignmentAsync(viewerAssignment);
        await fixture.AssignAsync(operatorRole, fixture.HostAId);

        var next = fixture.CreateChecker();
        (await next.HasPermissionAsync(fixture.UserId, fixture.HostAId, Permission.ContainerWrite))
            .Should().BeTrue();
    }

    // ===== 并集 =====

    [Fact]
    public async Task 同一主机上的多条分配取权限并集()
    {
        await using var fixture = await Fixture.CreateAsync();

        // ⚠️ 必须用**全局**分配才能观察到并集里 auditor 独有的 audit.read：
        // audit.read 属 Global 域，主机分配会把它正确地忽略掉。
        // （主机分配下 viewer 与 auditor 的 Host 域权限完全相同，看不出并集差异。）
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Operator), hostId: null);
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Auditor), hostId: null);

        var checker = fixture.CreateChecker();
        var effective = await checker.GetEffectivePermissionsAsync(fixture.UserId, hostId: null);

        // operator 独有 container.write，auditor 独有 audit.read——
        // 若判定逐条赋值而不是并集，这两条会互相覆盖，且不报任何错。
        effective.Should().Contain(Permission.ContainerWrite);
        effective.Should().Contain(Permission.AuditRead);
        effective.Should().Contain(Permission.ContainerRead);
    }

    [Fact]
    public async Task 同一主机上的多条分配_全局权限不参与并集()
    {
        await using var fixture = await Fixture.CreateAsync();

        // viewer 与 auditor 的 Host 域权限完全相同，唯一的差异是 auditor 多一条
        // Global 域的 audit.read——主机分配下它必须不出现。
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Viewer), fixture.HostAId);
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Auditor), fixture.HostAId);

        var checker = fixture.CreateChecker();
        var effective = await checker.GetEffectivePermissionsAsync(fixture.UserId, fixture.HostAId);

        effective.Should().NotContain(Permission.AuditRead);
        effective.Should().Contain(Permission.ContainerRead);
        effective.Should().NotContain(Permission.ContainerWrite);
    }

    // ===== 未认证 =====

    [Fact]
    public async Task 未认证用户拿不到任何权限()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(Role.AdminName), hostId: null);

        // 令牌里带着 admin 的 role claim，但主体是空的——
        // 判定必须以主体为准，否则未认证请求会被误判成系统管理员。
        var checker = fixture.CreateAnonymousChecker(role: Role.AdminName);

        var effective = await checker.GetEffectivePermissionsAsync(Guid.Empty, hostId: null);
        effective.Should().BeEmpty();

        (await checker.IsAdministratorAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task 空白权限名不授予任何权限()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(Role.AdminName), hostId: null);

        var checker = fixture.CreateChecker(role: Role.AdminName);

        (await checker.HasPermissionAsync(fixture.UserId, null, "  ")).Should().BeFalse();
    }

    // ===== 主机可见性 =====

    [Fact]
    public async Task 主机分配只让该主机可见()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Viewer), fixture.HostAId);

        var checker = fixture.CreateChecker();

        var result = await checker.FilterVisibleHostsAsync(fixture.UserId, fixture.Hosts);

        result.Select(item => item.Id).Should().Equal(fixture.HostAId);
    }

    [Fact]
    public async Task 全局分配让所有主机可见()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(RoleNames.Operator), hostId: null);

        var checker = fixture.CreateChecker();

        var result = await checker.FilterVisibleHostsAsync(fixture.UserId, fixture.Hosts);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task 系统管理员看到全部主机()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AssignAsync(await fixture.FindRoleAsync(Role.AdminName), hostId: null);

        var checker = fixture.CreateChecker(role: Role.AdminName);

        var result = await checker.FilterVisibleHostsAsync(fixture.UserId, fixture.Hosts);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task 无任何分配的用户看不到任何主机()
    {
        await using var fixture = await Fixture.CreateAsync();
        var checker = fixture.CreateChecker();

        var result = await checker.FilterVisibleHostsAsync(fixture.UserId, fixture.Hosts);

        result.Should().BeEmpty();
    }

    // ===== 撤销后即时失效 =====

    [Fact]
    public async Task 撤销分配后新请求不再有权限()
    {
        await using var fixture = await Fixture.CreateAsync();
        var assignment = await fixture.AssignAsync(
            await fixture.FindRoleAsync(RoleNames.Operator),
            fixture.HostAId);

        var before = fixture.CreateChecker();
        (await before.HasPermissionAsync(fixture.UserId, fixture.HostAId, Permission.ContainerWrite))
            .Should().BeTrue();

        await fixture.RemoveAssignmentAsync(assignment);

        var after = fixture.CreateChecker();
        (await after.HasPermissionAsync(fixture.UserId, fixture.HostAId, Permission.ContainerWrite))
            .Should().BeFalse();
    }

    // ===== 夹具 =====

    /// <summary>
    /// 内置角色名。<see cref="Role"/> 只把 <c>admin</c> 提升为常量
    /// （它被短路逻辑与「至少保留一名管理员」引用），其余三个仅测试与种子使用。
    /// </summary>
    private static class RoleNames
    {
        internal const string Operator = "operator";
        internal const string Viewer = "viewer";
        internal const string Auditor = "auditor";
    }

    /// <summary>
    /// 一名用户 + 两台主机的最小夹具。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须建真实行</b>：<c>user_role_assignments</c> 对 <c>users</c> 与
    /// <c>hosts</c> 都有外键，随手 <c>Guid.NewGuid()</c> 当主键会直接撞
    /// <c>SQLite Error 19: FOREIGN KEY constraint failed</c>——
    /// 而这个失败与「判定逻辑对不对」毫无关系，只会让人误以为测错了东西。
    /// </remarks>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PersistenceTestHost _host;

        private Fixture(PersistenceTestHost host, Guid userId, Guid hostAId, Guid hostBId, List<DockerHost> hosts)
        {
            this._host = host;
            this.UserId = userId;
            this.HostAId = hostAId;
            this.HostBId = hostBId;
            this.Hosts = hosts;
        }

        /// <summary>被测用户主键。</summary>
        internal Guid UserId { get; }

        /// <summary>主机 A（授权 operator）。</summary>
        internal Guid HostAId { get; }

        /// <summary>主机 B（授权 viewer 或不授权）。</summary>
        internal Guid HostBId { get; }

        /// <summary>两台主机的实体，供可见性过滤使用。</summary>
        internal IReadOnlyList<DockerHost> Hosts { get; }

        /// <summary>建夹具。</summary>
        internal static async Task<Fixture> CreateAsync()
        {
            var host = new PersistenceTestHost();
            await host.InitializeAsync();

            // 迁移种入一台默认主机；本组用例自控主机集合，清空后重建。
            await host.ExecuteAsync("DELETE FROM hosts");

            var users = host.Services.GetRequiredService<IUserRepository>();
            var hostRepository = host.Services.GetRequiredService<IHostRepository>();

            var user = User.Create(Guid.NewGuid(), "tester", _passwordHash);
            await users.AddAsync(user);

            var hostA = DockerHost.Register(
                Guid.NewGuid(), "alpha", DockerHostType.Socket, new HostEndpoint("/var/run/docker.sock"));
            var hostB = DockerHost.Register(
                Guid.NewGuid(), "beta", DockerHostType.Socket, new HostEndpoint("/var/run/docker.sock"));

            await hostRepository.AddAsync(hostA);
            await hostRepository.AddAsync(hostB);

            return new Fixture(host, user.Id, hostA.Id, hostB.Id, [hostA, hostB]);
        }

        /// <summary>按名字取内置角色。</summary>
        internal async Task<Role> FindRoleAsync(string name)
        {
            var role = await this.Get<IRoleRepository>().FindByNameAsync(name);
            role.Should().NotBeNull($"{name} 应由迁移种入");

            return role!;
        }

        /// <summary>建立一条分配，返回该记录以便后续撤销。</summary>
        internal async Task<UserRoleAssignment> AssignAsync(Role role, Guid? hostId)
        {
            var assignment = UserRoleAssignment.Assign(Guid.NewGuid(), this.UserId, role.Id, hostId);
            await this.Get<IUserRoleAssignmentRepository>().AddAsync(assignment);

            return assignment;
        }

        /// <summary>撤销一条分配（软删除）。</summary>
        internal Task<bool> RemoveAssignmentAsync(UserRoleAssignment assignment) =>
            this.Get<IUserRoleAssignmentRepository>().RemoveByIdAsync(assignment.Id);

        /// <summary>建一个判定实例，主体为夹具用户。</summary>
        internal PermissionChecker CreateChecker(string? role = null) =>
            this.Build(new StubCurrentUser(this.UserId, username: "tester", role: role));

        /// <summary>建一个**未认证**的判定实例。</summary>
        /// <remarks>
        /// 刻意与方法分开而不是给 <see cref="CreateChecker"/> 一个可空 userId 参数：
        /// 后者写成 <c>userId ?? this.UserId</c> 时，<c>null</c>（未认证）
        /// 会被静默替换成真实用户，测试照绿而断的正是它要断的那条断言。
        /// </remarks>
        internal PermissionChecker CreateAnonymousChecker(string? role = null) =>
            this.Build(new StubCurrentUser(null, username: null, role: role));

        /// <inheritdoc />
        public ValueTask DisposeAsync() => this._host.DisposeAsync();

        private PermissionChecker Build(StubCurrentUser currentUser) =>
            new(
                currentUser,
                this.Get<IUserRoleAssignmentRepository>(),
                this.Get<IRoleRepository>(),
                NullLogger<PermissionChecker>.Instance);

        private T Get<T>()
            where T : notnull =>
            this._host.Services.GetRequiredService<T>();
    }
}
