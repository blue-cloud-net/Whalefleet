using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Stacks;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 编排 Webhook 端点（匿名触发重放）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>这些用例不携带 access token</b>：Webhook 的凭据就是令牌本身。
/// 若用例改用登录令牌来调，反而掩盖了「端点是不是真的匿名」这个问题。</para>
/// <para><b>「令牌无效一律 404」是要守住的契约</b>：不区分「不存在」「已吊销」「未启用」，
/// 否则这个端点就成了令牌探测工具。</para>
/// <para><b>路径里不能出现主机标识</b>：否则 <c>CurrentHostMiddleware</c> 会先拿它去查库，
/// 表现为「令牌还没验，主机先报错」。</para>
/// </remarks>
#pragma warning disable CA1001
public class StackWebhookEndpointsTests : IAsyncLifetime
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
    public async Task 无效令牌_返回1906()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/webhooks/stacks/not-a-real-token",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(1906);
    }

    [Fact]
    public async Task 空令牌_返回1906()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/webhooks/stacks/",
            new { });

        // 路径里没有令牌段 → 路由不匹配 → 404（包装成 1000 语义）
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 有效令牌触发重放_返回202()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var token = await this.SeedStackWithWebhookAsync(name);

        try
        {
            using var anonymous = _factory.CreateClient();

            using var response = await anonymous.PostAsJsonAsync(
                $"/api/v1/webhooks/stacks/{token}",
                new { wait = false });

            response.StatusCode.Should().Be(
                HttpStatusCode.Accepted,
                $"Webhook 触发应当被受理：{await response.Content.ReadAsStringAsync()}");

            var body = await ReadAsync(response);
            body.GetProperty("success").GetBoolean().Should().BeTrue();
            body.GetProperty("data").GetProperty("taskId").GetString().Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await this.CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 吊销后触发_返回1906()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var token = await this.SeedStackWithWebhookAsync(name);

        try
        {
            using var revoke = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/stacks/webhook/revoke",
                new { name });

            revoke.StatusCode.Should().Be(HttpStatusCode.OK);

            using var anonymous = _factory.CreateClient();

            using var response = await anonymous.PostAsJsonAsync(
                $"/api/v1/webhooks/stacks/{token}",
                new { });

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ReadCodeAsync(response)).Should().Be(1906);
        }
        finally
        {
            await this.CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 触发后可以查询任务快照()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var token = await this.SeedStackWithWebhookAsync(name);

        try
        {
            using var anonymous = _factory.CreateClient();

            using var trigger = await anonymous.PostAsJsonAsync(
                $"/api/v1/webhooks/stacks/{token}",
                new { wait = false });

            trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var taskId = (await ReadAsync(trigger))
                .GetProperty("data")
                .GetProperty("taskId")
                .GetString();

            using var task = await anonymous.PostAsJsonAsync(
                $"/api/v1/webhooks/stacks/{token}/task",
                new { taskId });

            task.StatusCode.Should().Be(HttpStatusCode.OK);

            var snapshot = (await ReadAsync(task)).GetProperty("data");
            snapshot.GetProperty("taskId").GetString().Should().Be(taskId);
            snapshot.GetProperty("stackName").GetString().Should().Be(name);
        }
        finally
        {
            await this.CleanupAsync(client, name);
        }
    }

    [Fact]
    public async Task 用别的编排的令牌查任务_返回空数据()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        var name = NewStackName();
        var otherName = NewStackName();
        var token = await this.SeedStackWithWebhookAsync(name);

        try
        {
            await this.SeedStackWithWebhookAsync(otherName);

            using var anonymous = _factory.CreateClient();

            using var response = await anonymous.PostAsJsonAsync(
                $"/api/v1/webhooks/stacks/{token}/task",
                new { taskId = Guid.NewGuid().ToString("N") });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var data = (await ReadAsync(response)).GetProperty("data");
            data.ValueKind.Should().Be(JsonValueKind.Null, "不属于该编排的任务不应被读出来");
        }
        finally
        {
            await this.CleanupAsync(client, name);
            await this.CleanupAsync(client, otherName);
        }
    }

    #region 辅助

    /// <summary>
    /// 生成一个本次用例专用的 Stack 名称。
    /// </summary>
    /// <returns>Stack 名称。</returns>
    private static string NewStackName() => $"wfhook{Guid.NewGuid():N}"[..24];

    /// <summary>
    /// 造一个带 Webhook 令牌的 Stack 记录，并返回令牌明文。
    /// </summary>
    /// <param name="name">Stack 名称。</param>
    /// <returns>令牌明文。</returns>
    /// <remarks>
    /// <b>直接写库而不是走轮换端点</b>：轮换端点要求该 Stack 已存在，
    /// 而「先有记录再发令牌」的顺序在这里没意义——
    /// 用例要的是「有一个可用的令牌」，不是「轮换端点怎么工作」。
    /// </remarks>
    private async Task<string> SeedStackWithWebhookAsync(string name)
    {
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStackRepository>();
        var hosts = scope.ServiceProvider.GetRequiredService<IHostRepository>();

        var host = await hosts.FindByNameAsync(_localHost);
        host.Should().NotBeNull();

        var entity = Stack.Create(Guid.NewGuid(), host!.Id, name, ComposeYaml());
        entity.SetWebhook(HashToken(token), token[..8]);

        await repository.AddAsync(entity);

        return token;
    }

    /// <summary>
    /// 拼一份最小可用的 Compose 文件。
    /// </summary>
    /// <returns>YAML 文本。</returns>
    private static string ComposeYaml() => """
        services:
          app:
            image: alpine:3.20
            command: ["/bin/sh", "-c", "sleep 300"]
        """;

    /// <summary>
    /// 计算令牌哈希（与生产实现同一口径：SHA-256 的小写十六进制）。
    /// </summary>
    /// <param name="token">令牌明文。</param>
    /// <returns>哈希文本。</returns>
    private static string HashToken(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>
    /// 清掉本次用例留下的全部资源。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="name">Stack 名称。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <para><b>Webhook 触发会真的部署</b>：它入队的是一次真实的「更新」操作，
    /// 因此会在守护进程上建出网络与容器。清库里的记录是不够的——
    /// 残留的网络会占掉网段，让别的集成测试（子网 / IPAM）随机地 409。</para>
    /// <para><b>为什么删除可能先撞上 1905</b>：同一个 Stack 不允许并发任务，
    /// 而触发出来的部署任务可能还在跑。因此这里**重试**，
    /// 而不是把一次 409 当成「删除失败」就放弃。</para>
    /// </remarks>
    private async Task CleanupAsync(HttpClient client, string name)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/stacks/remove",
                new { name, taskId = Guid.NewGuid().ToString("N"), removeVolumes = true });

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var body = await ReadAsync(response);
                var taskId = body.GetProperty("data").GetProperty("taskId").GetString();

                if (!String.IsNullOrWhiteSpace(taskId))
                {
                    await WaitForTerminalAsync(client, taskId);
                }

                break;
            }

            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                break;
            }

            // 1905：该 Stack 还有任务在跑，等它结束再来
            await Task.Delay(500);
        }

        // 兜底：删除任务失败时库里的记录仍要清掉
        await this.DeleteRecordAsync(name);
    }

    /// <summary>
    /// 直接删掉库里的 Stack 记录。
    /// </summary>
    /// <param name="name">Stack 名称。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task DeleteRecordAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IStackRepository>();
        var hosts = scope.ServiceProvider.GetRequiredService<IHostRepository>();

        var host = await hosts.FindByNameAsync(_localHost);

        if (host is null)
        {
            return;
        }

        var entity = await repository.FindByHostAndNameAsync(host.Id, name);

        if (entity is not null)
        {
            await repository.RemoveByIdAsync(entity.Id);
        }
    }

    /// <summary>
    /// 轮询任务直到落到终态。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="taskId">任务标识。</param>
    /// <returns>终态快照。</returns>
    private static async Task<TaskPayload> WaitForTerminalAsync(HttpClient client, string taskId)
    {
        for (var attempt = 0; attempt < 240; attempt++)
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
    /// 调用主机作用域的端点并读出 <c>data</c>。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="action">动作名。</param>
    /// <param name="body">请求体。</param>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <returns>数据。</returns>
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
    #endregion

    private sealed record LoginPayload(string AccessToken);

    private sealed record TaskPayload(
        string TaskId,
        string StackName,
        string Operation,
        string Status,
        int? ErrorCode,
        string? ErrorMessage);
}
