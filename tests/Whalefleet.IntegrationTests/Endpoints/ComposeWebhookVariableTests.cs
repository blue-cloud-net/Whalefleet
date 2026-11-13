using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Variables;
using Whalefleet.Core.Composes;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// Compose Webhook 的**变量传参**（<c>?VAR=value</c>）端到端测试。
/// </summary>
/// <remarks>
/// <para><b>白名单只三档</b>：已声明 + 显式勾选可被覆盖 + 非密钥。
/// 三档里任何一档不满足，键就不生效——而「不生效」分两种处理：
/// 密钥<b>报错 1910</b>（静默忽略会让 CI 以为口令改了），
/// 其余<b>忽略并回带名字</b>（否则「哪些键可用」成了可枚举信息）。</para>
/// <para><b>覆盖只作用于本次部署、不落库</b>：因此这里断言的是
/// 「响应里的 applied / ignored 清单」与「变量表本身没变」，
/// 而不是容器的环境变量——后者要等部署跑完，而部署会真的拉镜像。</para>
/// </remarks>
#pragma warning disable CA1001
public class ComposeWebhookVariableTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";

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
        adminRole.Should().NotBeNull("内置角色应由迁移种入");

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
    public async Task 覆盖已声明且允许的变量_出现在applied里()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        var name = NewComposeName();
        var token = await this.SeedAsync(name);

        try
        {
            using var anonymous = _factory.CreateClient();

            using var response = await anonymous.PostAsync(
                $"/api/v1/webhooks/composes/{token}?TAG=9.9.9",
                content: null);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var data = (await ReadAsync(response)).GetProperty("data");
            data.GetProperty("appliedVariables").EnumerateArray()
                .Select(item => item.GetString())
                .Should().Contain("TAG");

            // 覆盖不落库：变量表里读回来的值仍是原值。
            using var scope = _factory.Services.CreateScope();
            var variables = scope.ServiceProvider.GetRequiredService<EnvVariableService>();
            var hostId = await this.ResolveHostIdAsync();
            var resolved = await variables.ResolveAsync(hostId, name);

            resolved.Values["TAG"].Should().Be("1.0.0", "Webhook 的覆盖只作用于本次部署");
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 覆盖未声明的变量_被忽略并回带名字()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        var name = NewComposeName();
        var token = await this.SeedAsync(name);

        try
        {
            using var anonymous = _factory.CreateClient();

            using var response = await anonymous.PostAsync(
                $"/api/v1/webhooks/composes/{token}?NOT_DECLARED=1",
                content: null);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var data = (await ReadAsync(response)).GetProperty("data");
            data.GetProperty("ignoredVariables").EnumerateArray()
                .Select(item => item.GetString())
                .Should().Contain("NOT_DECLARED");
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 覆盖未勾选可覆盖的普通变量_被忽略而不是报错()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        var name = NewComposeName();
        var token = await this.SeedAsync(name, overridable: false);

        try
        {
            using var anonymous = _factory.CreateClient();

            using var response = await anonymous.PostAsync(
                $"/api/v1/webhooks/composes/{token}?TAG=9.9.9",
                content: null);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var data = (await ReadAsync(response)).GetProperty("data");
            data.GetProperty("ignoredVariables").EnumerateArray()
                .Select(item => item.GetString())
                .Should().Contain("TAG");
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 覆盖密钥_返回1910而不是静默忽略()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        var name = NewComposeName();
        var token = await this.SeedAsync(name, secret: true);

        try
        {
            using var anonymous = _factory.CreateClient();

            using var response = await anonymous.PostAsync(
                $"/api/v1/webhooks/composes/{token}?TAG=hacked",
                content: null);

            // 静默忽略会让 CI 以为口令改了，而实际跑的还是旧值——那是最糟的组合。
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadAsync(response)).GetProperty("code").GetInt32().Should().Be(1910);
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    private static string NewComposeName() => $"wfvar{Guid.NewGuid():N}"[..24];

    /// <summary>
    /// 造一个带 Webhook 令牌与一个项目级变量的 Compose 项目记录。
    /// </summary>
    /// <param name="name">Compose 项目名称。</param>
    /// <param name="overridable">变量是否允许被 Webhook 覆盖。</param>
    /// <param name="secret">变量是否标记为密钥。</param>
    /// <returns>令牌明文。</returns>
    private async Task<string> SeedAsync(string name, bool overridable = true, bool secret = false)
    {
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var hostId = await this.ResolveHostIdAsync();

        using var scope = _factory.Services.CreateScope();
        var composes = scope.ServiceProvider.GetRequiredService<IComposeProjectRepository>();
        var variables = scope.ServiceProvider.GetRequiredService<EnvVariableService>();

        var entity = ComposeProject.Create(
            Guid.NewGuid(),
            hostId,
            name,
            """
            services:
              app:
                image: alpine:${TAG}
                command: ["/bin/sh", "-c", "sleep 300"]
            """);

        entity.SetWebhook(HashToken(token), token[..8]);
        await composes.AddAsync(entity);

        await variables.CreateForComposeAsync(
            hostId,
            _localHost,
            new ComposeEnvVariableRequest
            {
                ComposeName = name,
                Name = "TAG",
                Value = "1.0.0",
                IsSecret = secret,
                WebhookOverridable = overridable,
            });

        return token;
    }

    private async Task<Guid> ResolveHostIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var hosts = scope.ServiceProvider.GetRequiredService<IHostRepository>();
        var host = await hosts.FindByNameAsync(_localHost);

        host.Should().NotBeNull("默认主机应由启动流程种入");

        return host!.Id;
    }

    /// <summary>
    /// 清掉本次用例留下的 Compose 记录。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="name">Compose 项目名称。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>Webhook 触发会真的部署</b>（入队的是一次真实的更新），
    /// 因此必须走删除端点把容器与网络一并清掉；
    /// 只清数据库会留下占着网段的网络，让别的集成测试随机 409。
    /// </remarks>
    private static async Task CleanupAsync(HttpClient client, string name)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/composes/remove",
                new { name, taskId = Guid.NewGuid().ToString("N") });

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string username)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadAsync(login)).GetProperty("data");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            data.GetProperty("accessToken").GetString());

        return client;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
