using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Realtime;

/// <summary>
/// 容器日志端点（<c>logs</c> / <c>logs/download</c>）在 0.5.0 之后的契约。
/// </summary>
/// <remarks>
/// <para><b>破坏性变更</b>：<c>logs</c> 的响应从 <c>{ containerId, content }</c>
/// 变成 <c>{ containerId, lines[], truncated }</c>。这是为了让前端能
/// ①按行渲染 ②向上加载更早的日志 ③显示行号与时间戳。</para>
/// <para><b>为什么下载要单独一个端点</b>：一次性接口受 tail 上限（5000 行）约束，
/// 导出的文件会缺头；而用户点「下载」要的通常就是<b>完整</b>日志。</para>
/// </remarks>
#pragma warning disable CA1001
public sealed class ContainerRealtimeEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _unknownContainer = "no-such-container-0123456789";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // 镜像自带的入口优先，因此这里只保证容器会跑起来并产生输出，
    // **不断言具体输出内容**（镜像里跑的是什么不是本用例的考点）。
    private static readonly string[] _keepAliveCommand = ["sleep", "30"];

    /// <summary>
    /// 覆盖入口点，让容器只输出一行然后继续存活。
    /// </summary>
    /// <remarks>
    /// <c>cmd</c> 会被追加在 <c>Entrypoint</c> 之后，因此 <c>sh -c</c> 收到的额外参数只是
    /// <c>$0</c> / <c>$1</c>，不影响脚本本身。
    /// </remarks>
    private static readonly string[] _singleLineEntrypoint = ["/bin/sh", "-c", "echo line-one; sleep 30"];

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
    public async Task 未认证读取日志_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/logs",
            new { containerId = _unknownContainer });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task tail越界_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        // 上限是 5000：5001 越界 → 报错，而不是静默截断。
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/logs",
            new { containerId = _unknownContainer, tail = 5001 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 容器不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/logs",
            new { containerId = _unknownContainer });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [Fact]
    public async Task 下载日志_容器不存在_返回JSON错误而不是文本()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/logs/download",
            new { containerId = _unknownContainer });

        // 错误在开始写正文**之前**就能确定，因此响应仍是统一的 ApiResult JSON。
        // 前端据此才能区分「下载失败」与「下载到一个空文件」。
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [Fact]
    public async Task 未认证下载日志_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/logs/download",
            new { containerId = _unknownContainer });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerShellImageFact]
    public async Task 日志_返回结构化行()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var image = DockerAvailability.ShellImageReference!;
        var name = $"dm-rt-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            // 显式覆盖入口点，让容器**恰好输出一行**。
            // 宿主上第一张有标签的镜像的默认入口点不可控：没有入口点的镜像跑 sleep 时一行都不输出，
            // 而本项目自己的镜像（入口点是 API 宿主）会输出上百行启动日志——
            // 两种情况都让「按行返回」这件事无从断言。
            var created = await PostDataAsync<CreatedPayload>(
                client,
                "create",
                new { image, name, cmd = _keepAliveCommand, entrypoint = _singleLineEntrypoint });

            containerId = created.ContainerId;

            await PostOkAsync(client, "start", new { containerId });

            // 等容器把那一行吐出来。
            await Task.Delay(TimeSpan.FromSeconds(2));

            var logs = await PostDataAsync<LogsPayload>(
                client,
                "logs",
                new { containerId, tail = 50 });

            logs.ContainerId.Should().Be(containerId);
            logs.Lines.Should().NotBeEmpty("容器已经输出了一行");
            logs.Truncated.Should().BeFalse("50 行足够装下全部输出");

            // 序号从 1 起连续递增。
            logs.Lines[0]!.Seq.Should().Be(1);
            for (var i = 1; i < logs.Lines.Count; i++)
            {
                logs.Lines[i]!.Seq.Should().Be(logs.Lines[i - 1]!.Seq + 1);
            }

            // 每行都有独立的时间戳字段（守护进程给的时间戳被拆出来了）。
            logs.Lines.Should().OnlyContain(item => item.Timestamp != null);

            // 正文里**不含** RFC3339 时间戳前缀——前缀被拆到独立字段了。
            // 断言「不长得像时间戳」而不是「以 20 开头」：容器输出什么内容不是本用例的考点。
            logs.Lines.Should().OnlyContain(item =>
                !System.Text.RegularExpressions.Regex.IsMatch(
                    item.Line,
                    @"^\d{4}-\d{2}-\d{2}T",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        }
        finally
        {
            if (containerId is not null)
            {
                await TryRemoveAsync(client, containerId);
            }
        }
    }

    [DockerImageFact]
    public async Task 下载日志_返回纯文本()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var image = DockerAvailability.ImageReference!;
        var name = $"dm-dl-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            var created = await PostDataAsync<CreatedPayload>(
                client,
                "create",
                new { image, name, cmd = _keepAliveCommand });

            containerId = created.ContainerId;

            await PostOkAsync(client, "start", new { containerId });
            await Task.Delay(TimeSpan.FromSeconds(2));

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/logs/download",
                new { containerId });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");
            response.Content.Headers.ContentDisposition.Should().NotBeNull("下载必须带文件名");

            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotBeEmpty("容器有输出，下载到的内容就不该是空的");

            // 下载到的是**原始文本**：与结构化接口的行内容一致，但不带时间戳前缀。
            var logs = await PostDataAsync<LogsPayload>(client, "logs", new { containerId, tail = 50 });
            logs.Lines.Should().NotBeEmpty();
            body.Should().Contain(logs.Lines[0]!.Line);
        }
        finally
        {
            if (containerId is not null)
            {
                await TryRemoveAsync(client, containerId);
            }
        }
    }

    /// <summary>
    /// 日志响应（0.5.0 的结构化形状）。
    /// </summary>
    /// <param name="ContainerId">容器标识。</param>
    /// <param name="Lines">日志行。</param>
    /// <param name="Truncated">是否可能还有更早的日志。</param>
    private sealed record LogsPayload(
        string ContainerId,
        IReadOnlyList<LogLinePayload> Lines,
        bool Truncated);

    /// <summary>
    /// 单条日志行。
    /// </summary>
    /// <param name="Seq">序号。</param>
    /// <param name="Timestamp">时间戳。</param>
    /// <param name="Line">正文。</param>
    private sealed record LogLinePayload(int Seq, string? Timestamp, string Line);

    /// <summary>
    /// 创建容器的响应。
    /// </summary>
    /// <param name="ContainerId">容器标识。</param>
    private sealed record CreatedPayload(string ContainerId);

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await login.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        var accessToken = document.RootElement
            .GetProperty("data")
            .GetProperty("accessToken")
            .GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    private static async Task<T> PostDataAsync<T>(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
            body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);

        return document.RootElement.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task PostOkAsync(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
            body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task TryRemoveAsync(HttpClient client, string containerId)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/remove",
                new { containerId, force = true });

            _ = response;
        }
        catch (HttpRequestException)
        {
            // 清理失败不影响断言。
        }
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        return document.RootElement.GetProperty("code").GetInt32();
    }
}
