using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 角色管理与角色分配端点的端到端测试（0.1.0 授权基座）。
/// </summary>
/// <remarks>
/// <para>重点不在「能增删改查」，而在六条只有端到端才暴露的语义：</para>
/// <list type="number">
///   <item><description><b>权限目录是后端下发的</b>——界面遍历它渲染矩阵，
///   因此目录与 <c>Permission.All</c> 必须逐项一致（前端留副本必然不同步）；</description></item>
///   <item><description><b>角色标识不区分大小写且会被转小写</b>（唯一索引是 <c>COLLATE NOCASE</c>），
///   判重也必须不区分大小写；</description></item>
///   <item><description><b>内置角色的权限集不可改（1513）、可删（1513），但可读信息可改</b>——
///   这个不对称最容易在「统一拦掉内置角色」时写过头；</description></item>
///   <item><description><b>删除仍有分配的角色返回 1512 且不级联撤销</b>——
///   级联会悄悄收回没人要求收回的访问权；</description></item>
///   <item><description><b>分配是 (用户, 角色, 作用域) 三元组且幂等</b>，
///   重复分配返回<b>同一条记录</b>而不换主键；</description></item>
///   <item><description><b><c>set-default</c> 是单向的</b>：请求体里没有 <c>isDefault</c>，
///   取消默认的唯一方式是「把别的角色设为默认」。</description></item>
/// </list>
/// </remarks>
#pragma warning disable CA1001
public class RoleEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
    private const string _localHost = "local";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>不在权限目录里的标识，用于触发 1514。</summary>
    private static readonly string[] _unknownPermissions = ["container.explode"];

    /// <summary><see cref="_unknownPermissions"/> 的 PascalCase 包装，避免 CA1861（常量数组参数）。</summary>
    private static string[] UnknownPermissions => _unknownPermissions;

    private ApiFactory _factory = null!;
    private Guid _operatorUserId = Guid.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();

        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var assignments = scope.ServiceProvider.GetRequiredService<IUserRoleAssignmentRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync(Role.AdminName);
        var operatorRole = await roles.FindByNameAsync("operator");
        var passwordHash = hasher.Hash(_password);

        await TestAccounts.CreateUserAsync(users, assignments, _adminName, passwordHash, adminRole);
        _operatorUserId = await TestAccounts.CreateUserAsync(
            users, assignments, _operatorName, passwordHash, operatorRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ===== 权限目录 =====

    [Fact]
    public async Task 权限目录_与代码内的目录逐项一致()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync("/api/v1/admin/roles/permissions", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var catalog = await ReadDataAsync<List<PermissionPayload>>(response);

        // 前端权限矩阵直接遍历这份目录渲染行，因此它少一项 = 某个能力永远勾不上
        catalog.Select(item => item.Name)
            .Should().BeEquivalentTo(Permission.All);

        // 每一条都要能带出作用域，否则前端分不出 Host 与 Global 两组。
        // 写成 Equals 而非 is 模式：FluentAssertions 走表达式树，不能含模式匹配。
        catalog.Should().OnlyContain(item =>
            string.Equals(item.Scope, "global", StringComparison.Ordinal)
            || string.Equals(item.Scope, "host", StringComparison.Ordinal));
    }

    // ===== 角色列表 =====

    [Fact]
    public async Task 列表_未认证返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/admin/roles/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 列表_无roleRead权限返回403()
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsync("/api/v1/admin/roles/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 1500 是「你没有这个权限」，1502 是「没有执行这个操作的权限」——
        // 策略层面的拒绝报前者，不要拿后者的常量去断言。
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ResourceForbidden);
    }

    [Fact]
    public async Task 列表_customOnly排除内置角色()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync("/api/v1/admin/roles/list", new { customOnly = true });
        var roles = await ReadDataAsync<List<RolePayload>>(response);

        roles.Should().NotContain(role => role.IsBuiltIn);

        using var all = await client.PostAsJsonAsync("/api/v1/admin/roles/list", new { customOnly = false });
        var withBuiltIn = await ReadDataAsync<List<RolePayload>>(all);
        withBuiltIn.Should().Contain(role => role.Name == Role.AdminName);
    }

    // ===== 创建 =====

    [Fact]
    public async Task 创建_成功且标识被转小写()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/create",
            new { name = "NightShift", displayName = "夜班", description = "值夜班用" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await ReadDataAsync<RolePayload>(response);
        created.Name.Should().Be("nightshift", "角色标识统一转小写存储");
        created.IsBuiltIn.Should().BeFalse();
        created.Permissions.Should().BeEmpty("省略权限集即空权限");
    }

    [Fact]
    public async Task 创建_重复标识返回1511且不区分大小写()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var first = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/create",
            new { name = "oncall" });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var duplicate = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/create",
            new { name = "OnCall" });

        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(duplicate)).Should().Be(ErrorCodes.RoleNameAlreadyExists);
    }

    [Fact]
    public async Task 创建_未知权限返回1514()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/create",
            new { name = "bogus", permissions = UnknownPermissions });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.PermissionUnknown);
    }

    [Fact]
    public async Task 创建_空标识返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/create",
            new { name = "  " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    // ===== 内置角色的不对称保护（1513）=====

    [Fact]
    public async Task 内置角色_权限集不可改返回1513()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/set-permissions",
            new { role = Role.AdminName, permissions = new[] { Permission.HostRead } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.RoleProtected);
    }

    [Fact]
    public async Task 内置角色_不可删返回1513()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/remove",
            new { role = Role.AdminName });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.RoleProtected);
    }

    [Fact]
    public async Task 内置角色_可读信息可以改()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        // ⚠️ 「内置角色受保护」**只覆盖权限集与删除**。
        // 一律拦掉会让「给内置角色改个中文名」这条完全正常的路径变成 400。
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/update",
            new { role = Role.AdminName, displayName = "超级管理员" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await ReadDataAsync<RolePayload>(response);
        updated.DisplayName.Should().Be("超级管理员");
        updated.Name.Should().Be(Role.AdminName, "标识不可改");
    }

    // ===== 修改 / 权限集 =====

    [Fact]
    public async Task 设置权限集_自定义角色可覆盖为空()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var created = await CreateRoleAsync(client, "temp", new[] { Permission.ContainerRead });

        using var cleared = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/set-permissions",
            new { roleId = created.Id, permissions = Array.Empty<string>() });

        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDataAsync<RolePayload>(cleared)).Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task 设置权限集_未知角色返回1510()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/set-permissions",
            new { roleId = Guid.NewGuid(), permissions = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.RoleNotFound);
    }

    // ===== 默认角色（单向）=====

    [Fact]
    public async Task 设为默认_全库至多一个()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var created = await CreateRoleAsync(client, "newdefault");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/set-default",
            new { roleId = created.Id });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var list = await client.PostAsJsonAsync("/api/v1/admin/roles/list", new { customOnly = false });
        var roles = await ReadDataAsync<List<RolePayload>>(list);

        roles.Where(role => role.IsDefault).Should().ContainSingle();
        roles.Single(role => role.IsDefault).Id.Should().Be(created.Id);
    }

    // ===== 删除 =====

    [Fact]
    public async Task 删除_自定义角色成功()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "disposable");

        using var removed = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/remove",
            new { roleId = created.Id });
        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var list = await client.PostAsJsonAsync("/api/v1/admin/roles/list", new { customOnly = true });
        var roles = await ReadDataAsync<List<RolePayload>>(list);
        roles.Should().NotContain(role => role.Name == "disposable");
    }

    [Fact]
    public async Task 删除_仍有分配返回1512且不级联撤销()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "stillused");

        using var assign = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id });
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        using var removed = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/remove",
            new { roleId = created.Id });

        removed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(removed)).Should().Be(ErrorCodes.RoleInUse);

        // 级联撤销会悄悄收回没人要求收回的访问权，因此分配必须原封不动
        using var assignments = await client.PostAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/list",
            content: null);
        var list = await ReadDataAsync<List<AssignmentPayload>>(assignments);
        list.Should().Contain(item => item.RoleId == created.Id);
    }

    // ===== 用户视角的分配 =====

    [Fact]
    public async Task 分配_省略host即全局分配()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "globalrole");

        using var assign = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id });
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        var record = await ReadDataAsync<AssignmentPayload>(assign);
        record.HostId.Should().BeNull("省略 host 即全局分配");
        record.HostName.Should().BeNull();
    }

    [Fact]
    public async Task 分配_带host即主机作用域()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "hostrole");

        // ⚠️ 字段名是 `host`（主机名）而不是 `hostId`：
        // JSON 里多一个后端不认识的字段不会报错，只会被当成「没给 host」＝ 全局分配。
        using var assign = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id, host = _localHost });
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        var record = await ReadDataAsync<AssignmentPayload>(assign);
        record.HostId.Should().NotBeNull();
        record.HostName.Should().Be(_localHost);
    }

    [Fact]
    public async Task 分配_重复分配幂等且不换主键()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "idemrole");

        using var first = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id, host = _localHost });
        var firstRecord = await ReadDataAsync<AssignmentPayload>(first);

        using var second = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id, host = _localHost });
        var secondRecord = await ReadDataAsync<AssignmentPayload>(second);

        secondRecord.Id.Should().Be(firstRecord.Id, "重复分配必须返回同一条记录，否则刷新后界面会看到一条 id 不同的新记录");
    }

    [Fact]
    public async Task 分配_未知角色返回1510()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.RoleNotFound);
    }

    [Fact]
    public async Task 分配_未知主机返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "badhostrole");

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id, host = "no-such-host" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    // ===== 撤销 =====

    [Fact]
    public async Task 撤销_已撤销再撤返回1515()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "revokeme");

        using var assign = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id, host = _localHost });
        var record = await ReadDataAsync<AssignmentPayload>(assign);

        using var first = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/revoke",
            new { roleId = created.Id, host = _localHost });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var again = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/revoke",
            new { roleId = record.RoleId, host = _localHost });

        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(again)).Should().Be(ErrorCodes.RoleAssignmentNotFound);
    }

    [Fact]
    public async Task 撤销_软删除后可以重新分配同一组合()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "recycle");

        using var assign = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id, host = _localHost });
        await ReadDataAsync<AssignmentPayload>(assign);

        using var revoke = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/revoke",
            new { roleId = created.Id, host = _localHost });
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        // 撤销走软删除（保留「谁在什么时候撤的」这条事实），
        // 唯一索引是 WHERE is_deleted = 0，因此同一组合可以再次分配。
        using var reassign = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id, host = _localHost });
        reassign.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ===== 主机视角的分配 =====

    [Fact]
    public async Task 主机视角列表_不含全局分配()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var created = await CreateRoleAsync(client, "mixedrole");

        using var globalAssign = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id });
        await ReadDataAsync<AssignmentPayload>(globalAssign);

        using var hostAssign = await client.PostAsJsonAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/assign",
            new { roleId = created.Id, host = _localHost });
        await ReadDataAsync<AssignmentPayload>(hostAssign);

        using var hostList = await client.PostAsync(
            $"/api/v1/admin/hosts/{_localHost}/roles/list",
            content: null);
        hostList.StatusCode.Should().Be(HttpStatusCode.OK);

        var fromHost = await ReadDataAsync<List<AssignmentPayload>>(hostList);
        fromHost.Should().ContainSingle(
            "全局分配对所有主机生效、无法归属到某一台，因此不该出现在这里");
        fromHost.Should().Contain(item => item.RoleId == created.Id);

        // 用户视角要同时看到全局与各主机的分配。
        // ⚠️ 断言按角色而不是按条数：operator 夹具本身已带一条全局分配，
        // 写死条数会随夹具变动而假失败。
        using var userList = await client.PostAsync(
            $"/api/v1/admin/users/{_operatorUserId}/roles/list",
            content: null);
        var fromUser = await ReadDataAsync<List<AssignmentPayload>>(userList);

        fromUser.Where(item => item.RoleId == created.Id)
            .Should().HaveCount(2)
            .And.Contain(item => item.HostId == null, "全局分配")
            .And.Contain(item => item.HostName == _localHost, "主机分配");
    }

    // ===== 夹具 =====

    private static async Task<RolePayload> CreateRoleAsync(HttpClient client, string name, string[]? permissions = null)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/roles/create",
            new { name, permissions });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<RolePayload>(response);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string username)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await ReadDataAsync<LoginPayload>(login);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.AccessToken);

        return client;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue();

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed record LoginPayload(string AccessToken, string RefreshToken);

    private sealed record PermissionPayload(string Name, string Domain, string Level, string Scope);

    private sealed record RolePayload(
        Guid Id,
        string Name,
        string? DisplayName,
        string? Description,
        bool IsBuiltIn,
        bool IsDefault,
        IReadOnlyList<string> Permissions,
        int AssignmentCount);

    private sealed record AssignmentPayload(
        Guid Id,
        Guid UserId,
        string Username,
        Guid RoleId,
        string Role,
        string? RoleDisplayName,
        Guid? HostId,
        string? HostName,
        DateTime? LastModificationTime);
}
