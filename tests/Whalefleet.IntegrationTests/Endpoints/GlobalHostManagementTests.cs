using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 全局管理权限不隐式依赖目标主机的 host.read。
/// </summary>
public sealed class GlobalHostManagementTests
{
    [Fact]
    public async Task 主机管理_仅有全局管理权限_可更新分组和删除但不可读资源()
    {
        using var factory = new ApiFactory();
        using var client = await LoginAsync(factory, [Permission.HostManage]);
        using var update = await client.PostAsJsonAsync("/api/v1/admin/hosts/local/update", new
        {
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        using var group = await client.PostAsJsonAsync("/api/v1/admin/hosts/local/group", new
        {
            groupId = (string?)null
        });
        group.StatusCode.Should().Be(HttpStatusCode.OK);
        using var resource = await client.PostAsJsonAsync("/api/v1/hosts/local/containers/list", new
        {
        });
        resource.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var delete = await client.PostAsync("/api/v1/admin/hosts/local/delete", null);
        delete.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 主机角色_仅有角色读写权限_可查询分配并撤销()
    {
        using var factory = new ApiFactory();
        using var client = await LoginAsync(factory, [Permission.RoleRead, Permission.RoleWrite]);
        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            userId = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().FindByUsernameAsync("manager"))!.Id;
        }

        var body = new
        {
            userId,
            role = "viewer"
        };
        using var list = await client.PostAsJsonAsync("/api/v1/admin/hosts/local/roles/list", new
        {
        });
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var assign = await client.PostAsJsonAsync("/api/v1/admin/hosts/local/roles/assign", body);
        assign.StatusCode.Should().Be(HttpStatusCode.OK);
        using var revoke = await client.PostAsJsonAsync("/api/v1/admin/hosts/local/roles/revoke", body);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 管理端点_没有管理权限_仍返回403()
    {
        using var factory = new ApiFactory();
        using var client = await LoginAsync(factory, []);
        using var host = await client.PostAsJsonAsync("/api/v1/admin/hosts/local/update", new
        {
        });
        host.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var roles = await client.PostAsJsonAsync("/api/v1/admin/hosts/local/roles/list", new
        {
        });
        roles.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string[] permissions)
    {
        const string password = "StrongP@ss123";
        using (var scope = factory.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
            var role = Role.Create(Guid.NewGuid(), "manager", permissions: permissions);
            await roles.AddAsync(role);
            await roles.ReplacePermissionsAsync(role.Id, permissions);
            await TestAccounts.CreateUserAsync(scope.ServiceProvider.GetRequiredService<IUserRepository>(),
                scope.ServiceProvider.GetRequiredService<IUserRoleAssignmentRepository>(), "manager",
                scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(password), role);
        }

        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            username = "manager",
            password
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            json.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }
}
