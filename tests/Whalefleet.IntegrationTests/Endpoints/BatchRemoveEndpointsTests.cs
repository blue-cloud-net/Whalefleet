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
/// 批量删除端点（<c>containers/batch-remove</c> 与 <c>images/batch-remove</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>这组用例守的是「部分成功」这条语义</b>：批量删 3 个容器、其中 1 个不存在，
/// 正确的结果是「成功 2、失败 1」且<b>仍然返回 200</b>。如果实现改成「有一个失败就整批报错」，
/// 用户会既不知道哪条失败，也不知道另外两条到底删没删。</para>
/// <para><b>入参校验（空列表 / 超限）才是真正的 4xx</b>：那是请求本身不合法，
/// 与「主机连不上」同属整批失败，走异常路径。两种失败必须能被界面区分开。</para>
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
    private static readonly string[] _ghostContainer = ["ghost-batch-1", "ghost-batch-2"];
    private static readonly string[] _ghostImage = ["ghost-image-1:latest"];
    private static readonly string[] _duplicateIds = ["ghost-a", "ghost-a", " ghost-a "];
    private static readonly string[] _singleGhost = ["ghost-batch-1"];
    private static readonly string[] _anyId = ["a"];
    private static readonly string[] _ghostMixed = ["ghost-batch-mixed"];
    private static readonly string[] _sleepCommand = ["sleep", "60"];

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
            $"/api/v1/hosts/{_localHost}/containers/batch-remove",
            new { containerIds = _anyId });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("containers", "containerIds")]
    [InlineData("images", "imageIds")]
    public async Task 列表为空_返回1000(string resource, string field)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/{resource}/batch-remove",
            BuildBody(field, []));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("containers", "containerIds")]
    [InlineData("images", "imageIds")]
    public async Task 列表全为空串_返回1000(string resource, string field)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/{resource}/batch-remove",
            BuildBody(field, _blanks));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// 按字段名拼一个只带单个列表字段的请求体。
    /// </summary>
    /// <remarks>
    /// 容器与镜像的批量端点字段名不同（containerIds / imageIds），而匿名类型没法用变量名，
    /// 因此只能用字典拼请求体——否则就得为两个端点复制两遍几乎一样的用例。
    /// </remarks>
    private static Dictionary<string, object> BuildBody(string field, string[] ids) =>
        new(StringComparer.Ordinal) { [field] = ids };

    [Fact]
    public async Task 超过上限_返回1000()
    {
        using var client = await this.LoginAsync();
        var tooMany = Enumerable.Range(0, 101).Select(index => $"c{index}").ToArray();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/batch-remove",
            new { containerIds = tooMany });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
        (await ReadMessageAsync(response)).Should().Contain("100");
    }

    [Fact]
    public async Task 重复ID_按去重后的个数统计()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/batch-remove",
            new { containerIds = _duplicateIds });

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
            $"/api/v1/hosts/{_unknownHost}/containers/batch-remove",
            new { containerIds = _singleGhost });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [DockerFact]
    public async Task 容器_全部不存在_返回200并逐条汇报()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/batch-remove",
            new { containerIds = _ghostContainer });

        // 部分成功也是成功；全批失败时同样 200——原因逐条写在 failures 里
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<BatchPayload>(response);

        result.Requested.Should().Be(2);
        result.Succeeded.Should().Be(0);
        result.Failures.Should().HaveCount(2);
        result.Failures.Should().OnlyContain(failure => failure.Code == ErrorCodes.DockerResourceNotFound);
        result.Failures.Select(failure => failure.ResourceId)
            .Should().BeEquivalentTo(_ghostContainer);
    }

    [DockerFact]
    public async Task 镜像_全部不存在_返回200并逐条汇报()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/batch-remove",
            new { imageIds = _ghostImage });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<BatchPayload>(response);

        result.Requested.Should().Be(1);
        result.Succeeded.Should().Be(0);
        result.Failures.Should().ContainSingle()
            .Which.Code.Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerImageFact]
    public async Task 容器_存在与不存在混合_成功与失败都被如实汇报()
    {
        using var client = await this.LoginAsync();

        var image = DockerAvailability.ImageReference!;
        var name = $"dm-batch-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            var created = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/create",
                new { image, name, cmd = _sleepCommand });

            created.StatusCode.Should().Be(HttpStatusCode.OK);
            containerId = (await ReadDataAsync<CreatedPayload>(created)).ContainerId;

            string[] mixedIds = [containerId!, .. _ghostMixed];

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/batch-remove",
                new { containerIds = mixedIds, force = true });

            response.StatusCode.Should().Be(HttpStatusCode.OK, "一条失败不该让整批报错");

            var result = await ReadDataAsync<BatchPayload>(response);

            result.Requested.Should().Be(2);
            result.Succeeded.Should().Be(1, "真实存在的那个容器必须被删掉");
            result.Failures.Should().ContainSingle()
                .Which.ResourceId.Should().Be("ghost-batch-mixed");

            // 成功计数不是「看起来对」，是真的删掉了
            using var gone = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/inspect",
                new { containerId });

            gone.StatusCode.Should().Be(HttpStatusCode.NotFound);

            containerId = null;
        }
        finally
        {
            if (containerId is not null)
            {
                using var cleanup = await client.PostAsJsonAsync(
                    $"/api/v1/hosts/{_localHost}/containers/remove",
                    new { containerId, force = true });
            }
        }
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
        (await ReadAsync(response)).GetProperty("message").GetString() ?? String.Empty;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed record LoginPayload(string AccessToken);

    private sealed record CreatedPayload(string ContainerId, List<string> Warnings);

    private sealed record BatchPayload(
        int Requested,
        int Succeeded,
        List<FailurePayload> Failures);

    private sealed record FailurePayload(string ResourceId, int Code, string Message);
}
