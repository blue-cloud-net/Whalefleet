using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Application.Alerting;
using Whalefleet.Application.Common;
using Whalefleet.Core.Alerting;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 告警规则、通知渠道与通知中心的端到端测试（0.11.0）。
/// </summary>
/// <remarks>
/// 重点覆盖四件事：
/// <list type="number">
///   <item><description>管理员能看到迁移种下的 5 条默认规则；</description></item>
///   <item><description>阈值乱序返回 <c>1701</c>，而不是被静默接受；</description></item>
///   <item><description>渠道响应<b>不回传令牌</b>，测试失败返回 <c>1703</c>；</description></item>
///   <item><description>通知的已读 / 删除是<b>每用户</b>状态。</description></item>
/// </list>
/// </remarks>
#pragma warning disable CA1001
public class AlertEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";

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
    public async Task 规则列表_管理员看到5条默认规则()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsync("/api/v1/admin/alert-rules/list", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var rules = await ReadDataAsync<JsonElement>(response);

        rules.GetArrayLength().Should().Be(5);
    }

    [Fact]
    public async Task 规则列表_无alert权限返回403()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsync("/api/v1/admin/alert-rules/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ResourceForbidden);
    }

    [Fact]
    public async Task 新建规则_阈值乱序返回1701()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules/create",
            new
            {
                host = "local",
                kind = "container-cpu",
                enabled = true,
                infoThreshold = 95,
                warningThreshold = 85,
                criticalThreshold = 70,
                severity = "warning",
                consecutiveSamples = 3,
                windowSeconds = (int?)null,
                cooldownSeconds = 300,
                channelIds = Array.Empty<string>(),
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.AlertRuleInvalid);
    }

    [Fact]
    public async Task 规则新建_启停_删除()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var created = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules/create",
            new
            {
                host = "local",
                kind = "container-cpu",
                enabled = true,
                infoThreshold = 60,
                warningThreshold = 80,
                criticalThreshold = 90,
                severity = "warning",
                consecutiveSamples = 3,
                windowSeconds = (int?)null,
                cooldownSeconds = 300,
                channelIds = Array.Empty<string>(),
            });

        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = (await ReadDataAsync<JsonElement>(created)).GetProperty("id").GetGuid();

        using var disabled = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules/set-enabled",
            new { id = rule, enabled = false });
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);

        using var removed = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules/remove",
            new { id = rule });
        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var again = await client.PostAsJsonAsync(
            "/api/v1/admin/alert-rules/remove",
            new { id = rule });
        (await ReadCodeAsync(again)).Should().Be(ErrorCodes.AlertRuleNotFound);
    }

    [Fact]
    public async Task 渠道_响应不回传令牌_测试失败返回1703()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var created = await client.PostAsJsonAsync(
            "/api/v1/admin/notification-channels/create",
            new
            {
                name = "集成测试渠道",
                kind = "webhook",
                enabled = true,
                webhookUrl = "http://127.0.0.1:1/hook",
                webhookBearerToken = "secret-token",
                emailRecipients = (string[]?)null,
            });

        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadDataAsync<JsonElement>(created);
        var channelId = body.GetProperty("id").GetGuid();

        body.GetProperty("hasWebhookToken").GetBoolean().Should().BeTrue();
        body.TryGetProperty("webhookBearerToken", out _).Should().BeFalse("响应绝不回传令牌");
        body.GetProperty("webhookUrl").GetString().Should().NotContain("secret-token");

        using var tested = await client.PostAsJsonAsync(
            "/api/v1/admin/notification-channels/test",
            new { id = channelId });

        tested.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadCodeAsync(tested)).Should().Be(ErrorCodes.NotificationChannelTestFailed);
    }

    [Fact]
    public async Task 通知_列表_已读_删除()
    {
        using var scope = _factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

        await writer.CreateAsync(new NotificationDraft(
            NotificationSourceKind.AlertFired,
            AlertSeverity.Warning,
            "notification.alert.hostOffline",
            new Dictionary<string, object?> { ["host"] = "local" },
            HostId: null,
            HostName: "local",
            DedupeKey: null,
            DateTime.UtcNow));

        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var listed = await client.PostAsJsonAsync(
            "/api/v1/notifications/list",
            new { pageIndex = 0, pageSize = 20 });
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<JsonElement>(listed);
        result.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        result.GetProperty("unread").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        using var read = await client.PostAsJsonAsync(
            "/api/v1/notifications/read",
            new { ids = (string[]?)null, all = true });
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        using var afterRead = await client.PostAsJsonAsync(
            "/api/v1/notifications/list",
            new { pageIndex = 0, pageSize = 20 });
        (await ReadDataAsync<JsonElement>(afterRead)).GetProperty("unread").GetInt32().Should().Be(0);

        using var removed = await client.PostAsJsonAsync(
            "/api/v1/notifications/remove",
            new { ids = (string[]?)null, all = true });
        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var afterRemove = await client.PostAsJsonAsync(
            "/api/v1/notifications/list",
            new { pageIndex = 0, pageSize = 20 });
        (await ReadDataAsync<JsonElement>(afterRemove)).GetProperty("total").GetInt32().Should().Be(0);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string username)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await ReadDataAsync<JsonElement>(login);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            data.GetProperty("accessToken").GetString());

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
}
