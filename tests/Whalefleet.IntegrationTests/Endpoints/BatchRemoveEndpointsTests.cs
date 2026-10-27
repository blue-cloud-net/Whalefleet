using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 批量删除端点（<c>images/batch-remove</c> 与 <c>volumes/batch-remove</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>这组用例守的是「部分成功」这条语义</b>：批量删 3 个镜像、其中 1 个不存在，
/// 正确的结果是「成功 2、失败 1」且<b>仍然返回 200</b>。如果实现改成「有一个失败就整批报错」，
/// 用户会既不知道哪条失败，也不知道另外两条到底删没删。</para>
/// <para><b>入参校验（空列表 / 超限）才是真正的 4xx</b>：那是请求本身不合法，
/// 与「主机连不上」同属整批失败，走异常路径。两种失败必须能被界面区分开。</para>
/// <para><b>容器侧不再有批量删除端点</b>（0.7.0）：容器的批量操作改为界面逐条调用
/// 单条端点（启停 / 终止 / 暂停 / 恢复都是幂等的，逐条调用的代价只是几个来回），
/// 而删除虽然不可逆，逐条调用同样能把「哪一条失败、为什么」如实报出来。
/// 容器侧因此只保留单条 <c>remove</c>。</para>
/// </remarks>
#pragma warning disable CA1001
public class BatchRemoveEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] _blanks = ["  ", "", "\t"];
    private static readonly string[] _ghostImage = ["ghost-image-1:latest"];
    private static readonly string[] _duplicateIds = ["ghost-a", "ghost-a", " ghost-a "];
    private static readonly string[] _singleGhost = ["ghost-image-1:latest"];
    private static readonly string[] _anyId = ["a"];

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
        adminRole.Should().NotBeNull("内置角色应由迁移种入");

        // 管理员身份来自**全局分配**，而不是用户表上的某个外键（0.1.0 起）。
        // 只建用户不建分配的话，这个账号能登录却对任何主机都没有 host.read，
        // 于是每个端点测试都会拿到 403 而不是它真正想验证的状态码。
        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _adminName,
            hasher.Hash(_password),
            adminRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/batch-remove",
            new { imageIds = _anyId });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 列表为空_返回1000()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/batch-remove",
            new { imageIds = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 列表全为空串_返回1000()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/batch-remove",
            new { imageIds = _blanks });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 超过上限_返回1000()
    {
        using var client = await this.LoginAsync();
        var tooMany = Enumerable.Range(0, 101).Select(index => $"c{index}").ToArray();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/batch-remove",
            new { imageIds = tooMany });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
        (await ReadMessageAsync(response)).Should().Contain("100");
    }

    [Fact]
    public async Task 重复ID_按去重后的个数统计()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/batch-remove",
            new { imageIds = _duplicateIds });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<BatchPayload>(response);

        // 去重后是 1 个：若不去重，第二次删除必然 1401，界面会显示「成功 0、失败 2」
        result.Requested.Should().Be(1);
        result.Succeeded.Should().Be(0);
        result.Failures.Should().ContainSingle();
    }

    [Fact]
    public async Task 主机不存在_返回1300()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/images/batch-remove",
            new { imageIds = _singleGhost });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [DockerFact]
    public async Task 镜像_全部不存在_返回200并逐条汇报()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/batch-remove",
            new { imageIds = _ghostImage });

        // 部分成功也是成功；全批失败时同样 200——原因逐条写在 failures 里
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<BatchPayload>(response);

        result.Requested.Should().Be(1);
        result.Succeeded.Should().Be(0);
        result.Failures.Should().ContainSingle()
            .Which.Code.Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await ReadDataAsync<LoginPayload>(login)).AccessToken);

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

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("message").GetString() ?? string.Empty;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed class BatchPayload
    {
        public int Requested { get; init; }

        public int Succeeded { get; init; }

        public List<FailurePayload> Failures { get; init; } = [];
    }

    private sealed class FailurePayload
    {
        public string ResourceId { get; init; } = string.Empty;

        public int Code { get; init; }

        public string Message { get; init; } = string.Empty;
    }

    private sealed class LoginPayload
    {
        public string AccessToken { get; init; } = string.Empty;
    }
}
