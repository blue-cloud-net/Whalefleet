using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 容器列表按 <b>compose 项目</b> 过滤的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>为什么必须跑真守护进程</b>：这条过滤的核心是
/// 「把 <c>label=com.docker.compose.project=xxx</c> 这个过滤串下发给 Docker，
/// 而 Docker 真的会按它筛」——<b>假实现测不出这一点</b>：
/// 过滤串写对了但守护进程没按它筛（或者键的写法不对），在假实现上全是绿的。</para>
///
/// <para><b>夹具怎么造</b>：用本平台的「创建容器」端点建容器，并带上 compose 标签——
/// 这样既验证了标签是有效的，也让用例不必真的跑 <c>docker compose up</c>
/// （那会牵出网络、卷与项目名的清理问题）。</para>
///
/// <para><b>清理必须彻底</b>：容器留在宿主机上会连累其它用例
/// （比如「列出全部容器」会多出几个），因此每条用例都在 finally 里删掉自己建的容器。</para>
/// </remarks>
#pragma warning disable CA1001
public class ContainerProjectFilterEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _adminName,
            hasher.Hash(_password),
            adminRole);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [DockerImageFact]
    public async Task 按项目过滤_只返回该项目下的容器()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        // 两个不同的 compose 项目，各建一个容器
        var projectA = NewProjectName();
        var projectB = NewProjectName();
        var nameA = NewContainerName();
        var nameB = NewContainerName();

        try
        {
            await CreateAsync(client, nameA, projectA, "api");
            await CreateAsync(client, nameB, projectB, "api");

            var filtered = await ListAsync(client, project: projectA);

            filtered.Items.Should().ContainSingle();
            filtered.Items[0].Name.Should().Be(nameA);
            filtered.Total.Should().Be(1);

            // 服务名也要投影出来：编排详情页靠它把容器归到「服务」
            filtered.Items[0].ComposeService.Should().Be("api");
        }
        finally
        {
            await CleanupAsync(client, nameA, nameB);
        }
    }

    [DockerImageFact]
    public async Task 按项目过滤_不含其他项目的容器()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var projectA = NewProjectName();
        var projectB = NewProjectName();
        var nameA = NewContainerName();
        var nameB = NewContainerName();

        try
        {
            await CreateAsync(client, nameA, projectA, "api");
            await CreateAsync(client, nameB, projectB, "api");

            var filtered = await ListAsync(client, project: projectA);

            filtered.Items.Should().NotContain(item => item.Name == nameB);
        }
        finally
        {
            await CleanupAsync(client, nameA, nameB);
        }
    }

    [DockerImageFact]
    public async Task 不指定项目_返回全部容器()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var project = NewProjectName();
        var name = NewContainerName();

        try
        {
            await CreateAsync(client, name, project, "api");

            var all = await ListAsync(client);

            all.Items.Should().Contain(item => item.Name == name);
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [DockerImageFact]
    public async Task 按项目过滤_一次性容器被剔除()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var project = NewProjectName();
        var name = NewContainerName();

        try
        {
            await CreateAsync(client, name, project, "api", oneOff: true);

            var filtered = await ListAsync(client, project: project);

            filtered.Items.Should().NotContain(item => item.Name == name);
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 非法项目名_返回1000()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/list",
            new { all = true, project = "Shop Project" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    #region 辅助

    /// <summary>
    /// 生成一个本次用例专用的 compose 项目名。
    /// </summary>
    /// <returns>项目名（小写字母、数字与连字符）。</returns>
    private static string NewProjectName() => $"wftest{Guid.NewGuid():N}"[..24];

    /// <summary>
    /// 生成一个本次用例专用的容器名。
    /// </summary>
    /// <returns>容器名。</returns>
    private static string NewContainerName() => $"wf-{Guid.NewGuid():N}"[..20];

    /// <summary>
    /// 建一个带 compose 标签的容器。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="name">容器名。</param>
    /// <param name="project">compose 项目名。</param>
    /// <param name="service">compose 服务名。</param>
    /// <param name="oneOff">是否是一次性容器（<c>docker compose run</c> 那种）。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task CreateAsync(
        HttpClient client,
        string name,
        string project,
        string service,
        bool oneOff = false)
    {
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = project,
            ["com.docker.compose.service"] = service,
        };

        if (oneOff)
        {
            labels["com.docker.compose.oneoff"] = "True";
        }

        var body = new Dictionary<string, object?>
        {
            ["image"] = DockerAvailability.ImageReference,
            ["name"] = name,
            ["cmd"] = _sleepCommand,
            ["labels"] = labels,
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            body);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"容器应当创建成功：{await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// 列出容器（可按 compose 项目过滤）。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="project">compose 项目名；省略则不过滤。</param>
    /// <returns>列表结果。</returns>
    private static async Task<ListPayload> ListAsync(HttpClient client, string? project = null)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/list",
            new { all = true, project });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"容器列表应当成功：{await response.Content.ReadAsStringAsync()}");

        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue();

        return body.GetProperty("data").Deserialize<ListPayload>(_jsonOptions)!;
    }

    /// <summary>
    /// 清理容器（已删掉的会 404，属于预期，不断言状态码）。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="names">要清理的容器名。</param>
    private static async Task CleanupAsync(HttpClient client, params string[] names)
    {
        foreach (var name in names)
        {
            using var listed = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/list",
                new { all = true, search = name });

            if (!listed.IsSuccessStatusCode)
            {
                continue;
            }

            var body = await ReadAsync(listed);

            if (!body.GetProperty("success").GetBoolean())
            {
                continue;
            }

            foreach (var item in body.GetProperty("data").GetProperty("items").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();

                if (String.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                using var removed = await client.PostAsJsonAsync(
                    $"/api/v1/hosts/{_localHost}/containers/remove",
                    new { containerId = id, force = true });
            }
        }
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await ReadDataAsync<LoginPayload>(login);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.AccessToken);

        return client;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }
    #endregion

    private sealed record LoginPayload(string AccessToken);

    private sealed record ListPayload(List<ItemPayload> Items, int Total);

    private sealed record ItemPayload(string Id, string Name, string? ComposeService);
}