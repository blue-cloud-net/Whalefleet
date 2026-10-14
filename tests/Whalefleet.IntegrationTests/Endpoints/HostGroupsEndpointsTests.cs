using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 主机分组端点（<c>/api/v1/host-groups/list</c> 与 <c>/api/v1/admin/host-groups/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para>重点不在「能增删改查」，而在三条容易被写错的语义：</para>
/// <list type="number">
///   <item><description><b>读对所有角色开放、写只给 admin</b>——总览页对非管理员可见，
///   若读端点也要求 admin，非 admin 的总览页会整块报错；</description></item>
///   <item><description><b>删除分组不删主机</b>——组内主机回到未分组，级联删主机是灾难性的；</description></item>
///   <item><description><b>归属调整支持「移出分组」</b>——这正是它单设端点而不是塞进 update 的理由。</description></item>
/// </list>
/// </remarks>
#pragma warning disable CA1001
public class HostGroupsEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";

    /// <summary>迁移种入的「测试环境」分组主键（与 <c>0005_host_groups.sql</c> 一致）。</summary>
    private static readonly Guid _stagingGroupId = new("00000000-0000-0000-0000-0000000000a2");

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
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync(Role.AdminName);
        var operatorRole = await roles.FindByNameAsync("operator");

        var passwordHash = hasher.Hash(_password);

        await users.AddAsync(User.Create(Guid.NewGuid(), _adminName, passwordHash, adminRole!.Id));
        await users.AddAsync(User.Create(Guid.NewGuid(), _operatorName, passwordHash, operatorRole!.Id));
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 列表_未认证返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/host-groups/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 列表_非管理员可读()
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsync("/api/v1/host-groups/list", content: null);

        // 总览页对所有角色可见，因此这条读端点不能要求 admin
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var groups = await ReadDataAsync<List<GroupPayload>>(response);
        groups.Should().HaveCount(3, "迁移应种入 production / staging / local 三个内置分组");
    }

    [Fact]
    public async Task 列表_按排序权重升序且内置分组带主机数()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync("/api/v1/host-groups/list", content: null);
        var groups = await ReadDataAsync<List<GroupPayload>>(response);

        groups.Select(group => group.Name).Should().ContainInOrder("production", "staging", "local");
        groups.Should().BeInAscendingOrder(group => group.SortOrder);

        // 默认主机由迁移归入「本机」分组
        groups.Single(group => group.Name == "local").HostCount.Should().Be(1);
        groups.Single(group => group.Name == "production").HostCount.Should().Be(0);
    }

    [Fact]
    public async Task 创建_非管理员返回403()
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/host-groups/create",
            new { name = "edge" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ResourceForbidden);
    }

    [Fact]
    public async Task 创建_名称重复返回1304()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/host-groups/create",
            new { name = "production" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.GroupNameAlreadyExists);
    }

    [Fact]
    public async Task 创建_名称非法返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/host-groups/create",
            new { name = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建与改名_成功且改名不与自己冲突()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var created = await client.PostAsJsonAsync(
            "/api/v1/admin/host-groups/create",
            new { name = "edge", description = "边缘节点", sortOrder = 5 });

        var group = await ReadDataAsync<GroupPayload>(created);
        group.Name.Should().Be("edge");
        group.HostCount.Should().Be(0, "新建的分组必然为空");

        // 只改描述、不改名字：若「重名检查」没排除自己，这里会误报 1304
        using var updated = await client.PostAsJsonAsync(
            $"/api/v1/admin/host-groups/{group.Id}/update",
            new { description = "边缘机房" });

        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await ReadDataAsync<GroupPayload>(updated);
        after.Name.Should().Be("edge");
        after.Description.Should().Be("边缘机房");
    }

    [Fact]
    public async Task 更新_分组不存在返回1305()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/host-groups/{Guid.NewGuid()}/update",
            new { name = "whatever" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.GroupNotFound);
    }

    [Fact]
    public async Task 归属_未知主机返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/hosts/{_unknownHost}/group",
            new { groupId = _stagingGroupId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 归属_目标分组不存在返回1305()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/admin/hosts/{_localHost}/group",
            new { groupId = Guid.NewGuid() });

        // 外键拦不住「指向一个已软删除的分组」，因此必须在应用服务里显式校验
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.GroupNotFound);
    }

    [Fact]
    public async Task 归属_可移入也可移出分组()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var moved = await client.PostAsJsonAsync(
            $"/api/v1/admin/hosts/{_localHost}/group",
            new { groupId = _stagingGroupId });

        moved.StatusCode.Should().Be(HttpStatusCode.OK);

        using var groups = await client.PostAsync("/api/v1/admin/host-groups/list", content: null);
        var afterMove = await ReadDataAsync<List<GroupPayload>>(groups);
        afterMove.Single(group => group.Name == "staging").HostCount.Should().Be(1);
        afterMove.Single(group => group.Name == "local").HostCount.Should().Be(0);

        // 移出分组：groupId 显式为 null。这正是它单设端点而不是塞进 hosts/update 的理由——
        // 那边的「未提供表示不改」语义分不清「没给 groupId」与「给了 null」。
        using var cleared = await client.PostAsJsonAsync(
            $"/api/v1/admin/hosts/{_localHost}/group",
            new { groupId = (Guid?)null });

        cleared.StatusCode.Should().Be(HttpStatusCode.OK);

        using var groups2 = await client.PostAsync("/api/v1/admin/host-groups/list", content: null);
        var afterClear = await ReadDataAsync<List<GroupPayload>>(groups2);
        afterClear.Should().OnlyContain(
            group => group.HostCount == 0,
            "主机已不属于任何分组");
    }

    [Fact]
    public async Task 删除分组_主机回到未分组而不是被删掉()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        // 先把本机挪进 staging，再删掉 staging
        using var assigned = await client.PostAsJsonAsync(
            $"/api/v1/admin/hosts/{_localHost}/group",
            new { groupId = _stagingGroupId });
        assigned.StatusCode.Should().Be(HttpStatusCode.OK);

        using var removed = await client.PostAsync(
            $"/api/v1/admin/host-groups/{_stagingGroupId}/delete",
            content: null);

        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadDataAsync<int>(removed)).Should().Be(1, "应回带被移出分组的主机数");

        using var groups = await client.PostAsync("/api/v1/admin/host-groups/list", content: null);
        var remaining = await ReadDataAsync<List<GroupPayload>>(groups);
        remaining.Should().HaveCount(2);
        remaining.Should().NotContain(group => group.Name == "staging");

        // 主机还在，只是不属于任何分组——级联删主机是灾难性的（配置与凭据一起没了）
        using var hosts = await client.PostAsync("/api/v1/admin/hosts/list", content: null);
        hosts.StatusCode.Should().Be(HttpStatusCode.OK);

        var hostList = await ReadDataAsync<List<HostPayload>>(hosts);
        hostList.Should().ContainSingle(host => host.Name == _localHost);
        hostList.Single(host => host.Name == _localHost).GroupId.Should().BeNull();
    }

    [Fact]
    public async Task 概览_带出分组归属供总览页聚合()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync("/api/v1/hosts/list", content: null);
        var hosts = await ReadDataAsync<List<HostPayload>>(response);

        // 总览页要按分组聚合，因此归属必须出现在**列表**接口上（否则前端得逐台再查一次）
        hosts.Single(host => host.Name == _localHost).GroupId.Should().Be(
            new Guid("00000000-0000-0000-0000-0000000000a3"),
            "迁移已把默认主机归入「本机」分组");
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

    private sealed record GroupPayload(Guid Id, string Name, string? Description, int SortOrder, int HostCount);

    private sealed record HostPayload(string Id, string Name, Guid? GroupId);

    private sealed record LoginPayload(string AccessToken);
}
