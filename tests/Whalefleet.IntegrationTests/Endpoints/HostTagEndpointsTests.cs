using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 主机标签端点（<c>/api/v1/host-tags/list</c>、<c>/api/v1/admin/host-tags/*</c> 与
/// <c>/api/v1/admin/hosts/tags/*</c>）与总览过滤的端到端测试。
/// </summary>
/// <remarks>
/// <para>重点不在「能增删改查」，而在四条容易被写错的语义：</para>
/// <list type="number">
///   <item><description><b>读对所有角色开放、写只给 admin</b>——过滤栏对非管理员可见；</description></item>
///   <item><description><b>打标 / 清标幂等</b>——「全选 + 批量打标」必然包含重复项；</description></item>
///   <item><description><b>删除标签不删主机</b>，但关联必须一并清除；</description></item>
///   <item><description><b>「未分组」用空串表达</b>（JSON 分不清「没给」与「给了 null」）。</description></item>
/// </list>
/// </remarks>
#pragma warning disable CA1001
public class HostTagEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private ApiFactory _factory = null!;

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
        await TestAccounts.CreateUserAsync(users, assignments, _operatorName, passwordHash, operatorRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 列表_未认证返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/host-tags/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 列表_非管理员可读且初始为空()
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsync("/api/v1/host-tags/list", content: null);

        // 过滤栏对所有角色可见，因此这条读端点不能要求 admin
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDataAsync<List<TagPayload>>(response)).Should().BeEmpty();
    }

    [Fact]
    public async Task 创建_颜色被规范化且主机数为零()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var created = await CreateTagAsync(client, "Production", "#1a2b3c");

        created.Color.Should().Be("#1A2B3C", "颜色统一大写，避免「同色不同写法」");
        created.HostCount.Should().Be(0);

        using var list = await client.PostAsync("/api/v1/host-tags/list", content: null);
        var tags = await ReadDataAsync<List<TagPayload>>(list);

        tags.Should().ContainSingle(tag => tag.Name == "Production");
    }

    [Fact]
    public async Task 创建_重名不区分大小写返回1307()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        await CreateTagAsync(client, "gpu", null);

        using var duplicate = await client.PostAsJsonAsync(
            "/api/v1/admin/host-tags/create",
            new { name = "GPU" });

        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(duplicate)).Should().Be(ErrorCodes.HostTagNameAlreadyExists);
    }

    [Fact]
    public async Task 创建_非法颜色返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/host-tags/create",
            new { name = "bad-color", color = "red" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 更新_改名并清空颜色()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var tag = await CreateTagAsync(client, "old-name", "#112233");

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/host-tags/{tag.Id}/update",
            new { name = "new-name", color = string.Empty });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await ReadDataAsync<TagPayload>(response);
        updated.Name.Should().Be("new-name");
        updated.Color.Should().BeNull("空串表示清空，回到默认色");
    }

    [Fact]
    public async Task 更新_不存在的标签返回1306()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/host-tags/{Guid.NewGuid()}/update",
            new { name = "whatever" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostTagNotFound);
    }

    [Fact]
    public async Task 打标_幂等且回带在主机列表上()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var tag = await CreateTagAsync(client, "prod", "#22C55E");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var assigned = await client.PostAsJsonAsync(
                "/api/v1/admin/hosts/tags/assign",
                new { hostIds = new[] { _localHost }, tagIds = new[] { tag.Id.ToString() } });

            assigned.StatusCode.Should().Be(HttpStatusCode.OK, "重复打标必须幂等");
        }

        using var list = await client.PostAsync("/api/v1/host-tags/list", content: null);
        var tags = await ReadDataAsync<List<TagPayload>>(list);

        tags.Single(item => item.Id == tag.Id).HostCount.Should().Be(1);

        // 列表接口要带出标签，界面才能在行上显示「凭什么被筛出来」
        var host = await GetLocalHostAsync(client);
        host.Tags.Should().ContainSingle(item => item.Id == tag.Id);
    }

    [Fact]
    public async Task 打标_未知标签返回1306_未知主机返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var tag = await CreateTagAsync(client, "known", null);

        using var unknownTag = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/tags/assign",
            new { hostIds = new[] { _localHost }, tagIds = new[] { Guid.NewGuid().ToString() } });

        unknownTag.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(unknownTag)).Should().Be(ErrorCodes.HostTagNotFound);

        using var unknownHost = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/tags/assign",
            new { hostIds = new[] { _unknownHost }, tagIds = new[] { tag.Id.ToString() } });

        unknownHost.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(unknownHost)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 清标_幂等且主机仍在()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var tag = await CreateTagAsync(client, "temp", null);

        using var assigned = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/tags/assign",
            new { hostIds = new[] { _localHost }, tagIds = new[] { tag.Id.ToString() } });
        assigned.StatusCode.Should().Be(HttpStatusCode.OK);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var revoked = await client.PostAsJsonAsync(
                "/api/v1/admin/hosts/tags/revoke",
                new { hostIds = new[] { _localHost }, tagIds = new[] { tag.Id.ToString() } });

            revoked.StatusCode.Should().Be(HttpStatusCode.OK, "重复清标必须幂等");
        }

        var host = await GetLocalHostAsync(client);
        host.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task 删除标签_解除关联但不删主机()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var tag = await CreateTagAsync(client, "doomed", null);

        using var assigned = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/tags/assign",
            new { hostIds = new[] { _localHost }, tagIds = new[] { tag.Id.ToString() } });
        assigned.StatusCode.Should().Be(HttpStatusCode.OK);

        using var removed = await client.PostAsync(
            $"/api/v1/admin/host-tags/{tag.Id}/delete",
            content: null);

        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDataAsync<int>(removed)).Should().Be(1, "应回带被解除关联的主机数");

        using var list = await client.PostAsync("/api/v1/host-tags/list", content: null);
        (await ReadDataAsync<List<TagPayload>>(list)).Should().BeEmpty();

        // 主机还在，只是不再挂这个标签——级联删主机是灾难性的
        var host = await GetLocalHostAsync(client);
        host.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task 过滤_按标签任一命中()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var tag = await CreateTagAsync(client, "filter-me", null);

        using var assigned = await client.PostAsJsonAsync(
            "/api/v1/admin/hosts/tags/assign",
            new { hostIds = new[] { _localHost }, tagIds = new[] { tag.Id.ToString() } });
        assigned.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListHostsAsync(client, new { tagIds = new[] { tag.Id.ToString() } }))
            .Should().ContainSingle(host => host.Id == _localHost);

        (await ListHostsAsync(client, new { tagIds = new[] { Guid.NewGuid().ToString() } }))
            .Should().BeEmpty("没有主机挂这个标签");
    }

    [Fact]
    public async Task 过滤_未分组用空串表达()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        // 默认主机由迁移归入「本机」分组，因此「未分组」应当筛不出任何主机
        (await ListHostsAsync(client, new { groupId = string.Empty })).Should().BeEmpty();

        // 移出分组之后就能筛到
        using var cleared = await client.PostAsJsonAsync(
            $"/api/v1/admin/hosts/{_localHost}/group",
            new { groupId = (Guid?)null });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListHostsAsync(client, new { groupId = string.Empty }))
            .Should().ContainSingle(host => host.Id == _localHost);

        // 缺失 groupId = 不过滤（与空串不是一回事）
        (await ListHostsAsync(client, new { status = "online" }))
            .Should().ContainSingle(host => host.Id == _localHost);
    }

    [Fact]
    public async Task 过滤_状态与关键字()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        (await ListHostsAsync(client, new { status = "online" }))
            .Should().ContainSingle(host => host.Id == _localHost);

        (await ListHostsAsync(client, new { status = "offline" })).Should().BeEmpty();

        (await ListHostsAsync(client, new { search = "loc" }))
            .Should().ContainSingle(host => host.Id == _localHost);

        (await ListHostsAsync(client, new { search = "no-such-name" })).Should().BeEmpty();
    }

    [Fact]
    public async Task 过滤_非法状态与非法分组标识返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var badStatus = await client.PostAsJsonAsync(
            "/api/v1/hosts/list",
            new { status = "zombie" });

        badStatus.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(badStatus)).Should().Be(ErrorCodes.ValidationFailed);

        using var badGroup = await client.PostAsJsonAsync(
            "/api/v1/hosts/list",
            new { groupId = "not-a-guid" });

        badGroup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(badGroup)).Should().Be(ErrorCodes.ValidationFailed);
    }

    private static async Task<HostPayload> GetLocalHostAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/api/v1/hosts/list", content: null);

        var hosts = await ReadDataAsync<List<HostPayload>>(response);

        return hosts.Single(host => host.Id == _localHost);
    }

    private static async Task<List<HostPayload>> ListHostsAsync(HttpClient client, object filter)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/hosts/list", filter);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<List<HostPayload>>(response);
    }

    private static async Task<TagPayload> CreateTagAsync(HttpClient client, string name, string? color)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/host-tags/create",
            new { name, color });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<TagPayload>(response);
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

    private sealed record TagPayload(Guid Id, string Name, string? Description, string? Color, int HostCount);

    private sealed record TagRefPayload(Guid Id, string Name, string? Color);

    private sealed record HostPayload(string Id, string Name, string Status, List<TagRefPayload> Tags);

    private sealed record LoginPayload(string AccessToken);
}
