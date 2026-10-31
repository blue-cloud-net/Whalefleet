using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Stacks;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 编排 Stack 端点（部署 / 更新 / 停止 / 删除 / 任务 / Webhook）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>为什么必须跑真守护进程</b>：编排的核心语义全在守护进程那边
/// ——「标签能不能筛出这个项目」「容器能不能按配置哈希决定重建」
/// 「网络能不能接上」「卷会不会被建出来」，
/// 用假实现测等于把「自己写错了标签」也一起测成绿的。</para>
/// <para><b>部署是异步的，因此必须轮询任务状态</b>：端点立刻返回 202 + 任务标识，
/// 真正的执行在后台线程上。断言必须等任务落到终态，
/// 否则测的是「请求被受理了」而不是「编排真的生效了」。</para>
/// <para><b>清理要彻底</b>：编排会建出容器、网络与卷，
/// 而卷默认保留（删 Stack 不删数据）——因此每条用例都要显式清掉卷，
/// 否则下一次跑时那个卷已经存在，「新建卷」的断言就会失败。</para>
/// </remarks>
#pragma warning disable CA1001
public class StackEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>轮询任务时的最大等待次数（每次 250ms → 最长约 60 秒）。</summary>
    private const int _maxPollAttempts = 240;

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

    // ==================== 部署 ====================

    [DockerImageFact]
    public async Task 部署成功后列表出现该编排且状态为运行中()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var image = DockerAvailability.ImageReference!;

        try
        {
            var taskId = await DeployAsync(client, name, ComposeYaml(image));

            var snapshot = await WaitForTerminalAsync(client, taskId);

            snapshot.Status.Should().Be(
                "succeeded",
                $"部署应当成功：{snapshot.ErrorCode} {snapshot.ErrorMessage}");

            var list = await PostDataAsync<ListPayload>(client, "list", new { });

            var item = list.Items.Should().ContainSingle(i => i.Name == name).Subject;
            item.Managed.Should().BeTrue("本平台部署的项目应当标记为受管");
            item.ContainerCount.Should().Be(1);
            item.RunningCount.Should().Be(1);
            item.Status.Should().Be("running");
            item.ServiceCount.Should().Be(1);
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [DockerImageFact]
    public async Task 部署后详情给出服务与端口()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var image = DockerAvailability.ImageReference!;

        try
        {
            var taskId = await DeployAsync(client, name, ComposeYaml(image));

            var snapshot = await WaitForTerminalAsync(client, taskId);
            snapshot.Status.Should().Be("succeeded", snapshot.ErrorMessage);

            var detail = await PostDataAsync<DetailPayload>(client, "inspect", new { name });

            detail.ComposeContent.Should().NotBeNullOrWhiteSpace("受管编排应当保留 YAML 原文");
            detail.Services.Should().ContainSingle();

            var service = detail.Services[0];
            service.Name.Should().Be("app");
            service.ContainerId.Should().NotBeNullOrWhiteSpace();
            service.State.Should().Be("running");
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 重复部署同名编排_返回1902()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = NewStackName();

        // 先塞一条记录（不真的部署——用例会话里没有镜像也会走到这一步）
        await SeedStackAsync(name);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/deploy",
            new { name, composeContent = ComposeYaml("alpine:3.20"), taskId = NewTaskId() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1902);
    }

    [Fact]
    public async Task 更新不存在的编排_返回1900()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/update",
            new
            {
                name = NewStackName(),
                composeContent = ComposeYaml("alpine:3.20"),
                taskId = NewTaskId(),
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(1900);
    }

    [Fact]
    public async Task 非法YAML_返回1904并带出字段路径()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/deploy",
            new
            {
                name = NewStackName(),
                composeContent = "services:\n  app:\n    image: alpine:3.20\n    build: .\n",
                taskId = NewTaskId(),
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await ReadAsync(response);
        body.GetProperty("code").GetInt32().Should().Be(1904);
        body.GetProperty("message").GetString().Should().Contain("services.app.build");
    }

    [Fact]
    public async Task 名称含大写字母_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/deploy",
            new
            {
                name = "BadName",
                composeContent = ComposeYaml("alpine:3.20"),
                taskId = NewTaskId(),
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(1000);
    }

    [Fact]
    public async Task 未认证访问编排_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/list",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ==================== 停止与删除 ====================

    [DockerImageFact]
    public async Task 停止后容器退出但仍保留()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var image = DockerAvailability.ImageReference!;

        try
        {
            var taskId = await DeployAsync(client, name, ComposeYaml(image));

            var deployed = await WaitForTerminalAsync(client, taskId);
            deployed.Status.Should().Be("succeeded", deployed.ErrorMessage);

            var stopTaskId = NewTaskId();

            using var stopResponse = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/stacks/stop",
                new { name, taskId = stopTaskId });

            stopResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var stopped = await WaitForTerminalAsync(client, stopTaskId);
            stopped.Status.Should().Be("succeeded", stopped.ErrorMessage);
            stopped.Result!.Stopped.Should().Be(1);

            var list = await PostDataAsync<ListPayload>(client, "list", new { });

            var item = list.Items.Should().ContainSingle(i => i.Name == name).Subject;
            item.RunningCount.Should().Be(0);
            item.ContainerCount.Should().Be(1, "停止只停容器，不删容器");
            item.Status.Should().Be("stopped");
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [DockerImageFact]
    public async Task 删除后容器与网络被清理()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var image = DockerAvailability.ImageReference!;

        try
        {
            var taskId = await DeployAsync(client, name, ComposeYaml(image));

            var deployed = await WaitForTerminalAsync(client, taskId);
            deployed.Status.Should().Be("succeeded", deployed.ErrorMessage);

            var removeTaskId = NewTaskId();

            using var removeResponse = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/stacks/remove",
                new { name, taskId = removeTaskId, removeVolumes = true });

            removeResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var removed = await WaitForTerminalAsync(client, removeTaskId);
            removed.Status.Should().Be("succeeded", removed.ErrorMessage);

            var list = await PostDataAsync<ListPayload>(client, "list", new { });

            list.Items.Should().NotContain(i => i.Name == name);
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    // ==================== 任务查询 ====================

    [DockerImageFact]
    public async Task 任务快照可查询且带进度()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var image = DockerAvailability.ImageReference!;

        try
        {
            var taskId = await DeployAsync(client, name, ComposeYaml(image));

            var snapshot = await WaitForTerminalAsync(client, taskId);

            snapshot.TaskId.Should().Be(taskId);
            snapshot.StackName.Should().Be(name);
            snapshot.Operation.Should().Be("deploy");
            snapshot.Status.Should().Be("succeeded");
            snapshot.Result.Should().NotBeNull();
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 查询不存在的任务_返回空数据()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var snapshot = await PostDataAsync<TaskPayload?>(client, "task", new { taskId = NewTaskId() });

        snapshot.Should().BeNull();
    }

    // ==================== Webhook ====================

    [DockerImageFact]
    public async Task 轮换Webhook后明文只出现一次()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var image = DockerAvailability.ImageReference!;

        try
        {
            var taskId = await DeployAsync(client, name, ComposeYaml(image));

            var deployed = await WaitForTerminalAsync(client, taskId);
            deployed.Status.Should().Be("succeeded", deployed.ErrorMessage);

            var info = await PostDataAsync<WebhookPayload>(
                client,
                "webhook/rotate",
                new { name });

            info.Enabled.Should().BeTrue();
            info.Token.Should().NotBeNullOrWhiteSpace("首次生成必须给明文");
            info.TokenHint.Should().NotBeNullOrWhiteSpace();

            // 吊销后再轮换，明文应当是新的
            await PostOkAsync(client, "webhook/revoke", new { name });

            var second = await PostDataAsync<WebhookPayload>(client, "webhook/rotate", new { name });

            second.Token.Should().NotBe(info.Token, "每次轮换都应产生新的令牌");
        }
        finally
        {
            await CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 轮换不存在的编排Webhook_返回1900()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/webhook/rotate",
            new { name = NewStackName() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(1900);
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 生成一个本次用例专用的 Stack 名称。
    /// </summary>
    /// <returns>Stack 名称（小写字母、数字与连字符）。</returns>
    private static string NewStackName() => $"wftest{Guid.NewGuid():N}"[..24];

    /// <summary>
    /// 生成一个任务标识。
    /// </summary>
    /// <returns>任务标识（字母、数字与短横线，长度 ≥ 32）。</returns>
    private static string NewTaskId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 拼一份最小可用的 Compose 文件。
    /// </summary>
    /// <param name="image">镜像引用。</param>
    /// <returns>YAML 文本。</returns>
    /// <remarks>
    /// <b>显式覆盖入口点</b>：镜像自带的 <c>ENTRYPOINT</c> 可能让容器秒退，
    /// 那时「容器在跑」这个断言会以「采样全零」的面貌失败，看着像业务代码坏了。
    /// </remarks>
    private static string ComposeYaml(string image) => $"""
        services:
          app:
            image: {image}
            command: ["/bin/sh", "-c", "sleep 300"]
            environment:
              APP_ENV: test
        """;

    /// <summary>
    /// 直接往库里塞一条 Stack 记录（用于「重名」这类不需要真部署的断言）。
    /// </summary>
    /// <param name="name">Stack 名称。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task SeedStackAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStackRepository>();
        var hosts = scope.ServiceProvider.GetRequiredService<IHostRepository>();

        var host = await hosts.FindByNameAsync(_localHost);
        host.Should().NotBeNull();

        var entity = Stack.Create(Guid.NewGuid(), host!.Id, name, ComposeYaml("alpine:3.20"));

        await repository.AddAsync(entity);
    }

    /// <summary>
    /// 部署并返回任务标识。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="name">Stack 名称。</param>
    /// <param name="composeContent">Compose 文件原文。</param>
    /// <returns>任务标识。</returns>
    private static async Task<string> DeployAsync(HttpClient client, string name, string composeContent)
    {
        var taskId = NewTaskId();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/deploy",
            new { name, composeContent, taskId });

        response.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            $"部署应当被受理：{await response.Content.ReadAsStringAsync()}");

        return taskId;
    }

    /// <summary>
    /// 轮询任务直到落到终态。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="taskId">任务标识。</param>
    /// <returns>终态快照。</returns>
    private static async Task<TaskPayload> WaitForTerminalAsync(HttpClient client, string taskId)
    {
        for (var attempt = 0; attempt < _maxPollAttempts; attempt++)
        {
            var snapshot = await PostDataAsync<TaskPayload>(client, "task", new { taskId });

            if (snapshot is not null
                && (snapshot.Status == "succeeded" || snapshot.Status == "failed"))
            {
                return snapshot;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"编排任务 {taskId} 在预期时间内没有落到终态");
    }

    /// <summary>
    /// 清掉本次用例留下的全部资源（容器、网络、卷与数据库记录）。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="name">Stack 名称。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <para><b>为什么删除可能先撞上 1905</b>：同一个 Stack 不允许并发任务，
    /// 而刚发起的部署可能还在跑。因此这里**重试**，
    /// 而不是把一次 409 当成「删除失败」就放弃——
    /// 放弃的话残留的网络会占掉网段，让别的集成测试（子网 / IPAM）随机地 409。</para>
    /// <para><b>清理失败不该让用例本身失败</b>：用例的断言已经跑完了，
    /// 而残留会在下一轮造成莫名其妙的失败。</para>
    /// </remarks>
    private static async Task CleanupAsync(HttpClient client, string name)
    {
        try
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                using var response = await client.PostAsJsonAsync(
                    $"/api/v1/hosts/{_localHost}/stacks/remove",
                    new { name, taskId = NewTaskId(), removeVolumes = true });

                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    var body = await ReadAsync(response);
                    var taskId = body.GetProperty("data").GetProperty("taskId").GetString();

                    if (!string.IsNullOrWhiteSpace(taskId))
                    {
                        await WaitForTerminalAsync(client, taskId);
                    }

                    return;
                }

                if (response.StatusCode != HttpStatusCode.Conflict)
                {
                    return;
                }

                // 1905：该 Stack 还有任务在跑，等它结束再来
                await Task.Delay(500);
            }
        }
        catch (Exception)
        {
            // 清理失败不该让用例本身失败
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

    private static async Task PostOkAsync(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/{action}",
            body);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"“{action}” 应当成功：{await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<T> PostDataAsync<T>(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/stacks/{action}",
            body);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"“{action}” 应当成功：{await response.Content.ReadAsStringAsync()}");

        return await ReadDataAsync<T>(response);
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
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed record LoginPayload(string AccessToken);

    private sealed record ListPayload(List<ItemPayload> Items, int Total);

    private sealed record ItemPayload(
        string Name,
        bool Managed,
        string Status,
        int ServiceCount,
        int ContainerCount,
        int RunningCount,
        int NetworkCount,
        int VolumeCount);

    private sealed record DetailPayload(
        ItemPayload Summary,
        string? ComposeContent,
        List<ServicePayload> Services);

    private sealed record ServicePayload(
        string Name,
        string Image,
        string? ContainerId,
        string? State);

    private sealed record TaskPayload(
        string TaskId,
        string StackName,
        string Operation,
        string Status,
        double? Percent,
        string? Step,
        int? ErrorCode,
        string? ErrorMessage,
        ResultPayload? Result);

    private sealed record ResultPayload(int Created, int Recreated, int Removed, int Started, int Stopped);

    private sealed record WebhookPayload(bool Enabled, string? TokenHint, string? Token, DateTime? LastUsedAt);
}
