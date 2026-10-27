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
/// 容器生命周期端点（<c>kill</c> / <c>pause</c> / <c>resume</c> / <c>recreate</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>这些用例必须在真守护进程上跑</b>：暂停/恢复/终止的语义全在守护进程那边
/// （哪些状态算「未运行」、哪些信号会真的让进程退出），用假实现测等于把
/// 「自己写错了状态判定」也一起测成绿的。</para>
/// <para><b>重建类用例必须清理两代容器</b>：<c>recreate</c> 会留下「新的」与
/// 「被让位的旧容器」两个——只清理新的会让下一次跑时名字冲突，
/// 从而把这个用例变成「随机失败」。</para>
/// </remarks>
#pragma warning disable CA1001
public class ContainerLifecycleEndpointsTests : IAsyncLifetime
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

    /// <summary>
    /// 一个<b>能捕获 SIGTERM</b>的常驻命令（需要能跑 shell 的镜像）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么不能用单纯的 <c>sleep</c></b>：容器里的 PID 1 是<b>内核特判</b>的——
    /// 默认动作会终止进程的信号（SIGTERM 就是）对 PID 1 <b>不生效</b>，
    /// 除非进程<b>自己安装了处理器</b>。因此 <c>sleep</c> 作为 PID 1 时
    /// <c>docker kill -s SIGTERM</c> 会<b>被送达但不被执行</b>，容器照旧活着。</para>
    /// <para>而 PID 1 是不是 <c>sleep</c> 取决于<b>镜像的入口点</b>：
    /// 入口点是 <c>/bin/sh -c …</c> 时 PID 1 是 shell（能收到信号），
    /// 入口点是应用二进制时 PID 1 是那个应用（行为由应用决定）。
    /// 因此「SIGTERM 能不能终止容器」本质上是<b>镜像相关</b>的，
    /// 用例必须显式指定入口点才能得到确定结论。</para>
    /// </remarks>
    private static readonly string[] _signalCatchingEntrypoint =
    [
        "/bin/sh",
        "-c",
        "trap 'exit 0' TERM; while true; do sleep 1; done",
    ];

    /// <summary>重建前后的环境变量对照（一个字段一个值，避免内联数组触发 CA1861）。</summary>
    private static readonly string[] _keepEnv = ["KEEP=1"];

    private static readonly string[] _oldEnv = ["OLD=1"];

    private static readonly string[] _newEnv = ["NEW=1"];

    private static readonly string[] _newCommand = ["sleep", "120"];

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

    // ==================== pause / resume ====================

    [DockerImageFact]
    public async Task 暂停后详情显示已暂停_恢复后回到运行中()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-pause-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(client, name);
            await PostOkAsync(client, "start", new { containerId });

            await PostOkAsync(client, "pause", new { containerId });

            var paused = await PostDataAsync<StatePayload>(client, "inspect", new { containerId });
            paused.State.Paused.Should().BeTrue("守护进程应把容器标成已暂停");
            paused.State.Running.Should().BeTrue("暂停的容器进程仍在，因此 running 仍为 true");

            // 幂等：已经暂停了再点一次「暂停」不应报错——
            // 守护进程那边回的是 409，而「已经在目标状态」是成功语义。
            await PostOkAsync(client, "pause", new { containerId });

            await PostOkAsync(client, "resume", new { containerId });

            var resumed = await PostDataAsync<StatePayload>(client, "inspect", new { containerId });
            resumed.State.Paused.Should().BeFalse();

            // 同样幂等：没暂停时点「恢复」按成功处理
            await PostOkAsync(client, "resume", new { containerId });
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 暂停未运行的容器_返回1404()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-nopause-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            // 只创建不启动：容器处于「已创建」状态，暂停没有意义
            containerId = await CreateAsync(client, name);

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/pause",
                new { containerId });

            // 409 才是对的：这是「资源当前状态不允许」，不是「参数非法」也不是「服务端坏了」
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceConflict);
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 恢复未运行的容器_返回1404()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-noresume-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(client, name);

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/resume",
                new { containerId });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceConflict);
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    // ==================== kill ====================

    [DockerImageFact]
    public async Task 终止运行中的容器_进程退出()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-kill-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(client, name);
            await PostOkAsync(client, "start", new { containerId });

            await PostOkAsync(client, "kill", new { containerId });

            (await WaitUntilStoppedAsync(client, containerId)).Should().BeTrue(
                "SIGKILL 之后容器不应还在运行");
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerShellImageFact]
    public async Task 终止_自定义信号_被守护进程接受()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-signal-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            // ⚠️ **必须显式覆盖入口点**：否则 PID 1 是什么由镜像决定，
            // 而 PID 1 默认不响应 SIGTERM（见 _signalCatchingEntrypoint 的说明）。
            // 入口点要用 shell，因此镜像也得是「能跑 shell」的那张。
            containerId = await CreateAsync(
                client,
                name,
                new { entrypoint = _signalCatchingEntrypoint },
                DockerAvailability.ShellImageReference);

            await PostOkAsync(client, "start", new { containerId });

            // 与 `docker kill -s SIGTERM` 同义：信号名原样透传给守护进程
            await PostOkAsync(client, "kill", new { containerId, signal = "SIGTERM" });

            (await WaitUntilStoppedAsync(client, containerId)).Should().BeTrue(
                "SIGTERM 被送达后进程应当退出");
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 终止未运行的容器_返回1404()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-nokill-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(client, name);

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/kill",
                new { containerId });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceConflict);
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    // ==================== recreate ====================

    [DockerImageFact]
    public async Task 重建_不给配置时按原配置重建()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-recreate-{Guid.NewGuid():N}"[..16];
        string? containerId = null;
        string? newId = null;

        try
        {
            containerId = await CreateAsync(client, name, new { env = _keepEnv });
            await PostOkAsync(client, "start", new { containerId });

            var recreated = await PostDataAsync<RecreatedPayload>(client, "recreate", new { containerId });

            recreated.ContainerId.Should().NotBe(containerId, "重建必然产生新的容器 ID");
            recreated.Name.Should().Be(name, "重建沿用原名");
            recreated.ReplacedContainerId.Should().Be(containerId, "旧容器被让位而不是删除");
            recreated.Recreated.Should().BeTrue();
            recreated.Running.Should().BeTrue("start 省略时沿用原容器的运行状态");

            newId = recreated.ContainerId;

            // 新容器继承了原配置
            var detail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId = newId });
            detail.Name.Should().Be(name);
            detail.Config.Env.Should().Contain("KEEP=1");

            // 旧容器仍在，只是改了名——那是回滚的后手
            var old = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId });
            old.Name.Should().StartWith("whalefleet-replaced-");
        }
        finally
        {
            // 两代容器都要清理：只清理新的会让下一次跑时名字冲突
            await CleanupAsync(client, newId, containerId);
        }
    }

    [DockerImageFact]
    public async Task 重建_给新配置时按新配置重建()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-edit-{Guid.NewGuid():N}"[..16];
        string? containerId = null;
        string? newId = null;

        try
        {
            containerId = await CreateAsync(client, name, new { env = _oldEnv });

            var recreated = await PostDataAsync<RecreatedPayload>(
                client,
                "recreate",
                new
                {
                    containerId,
                    config = new
                    {
                        image = DockerAvailability.ImageReference,
                        cmd = _newCommand,
                        env = _newEnv,
                    },
                });

            newId = recreated.ContainerId;
            recreated.Recreated.Should().BeTrue();

            // config 是**全量替换**语义：没写的字段就是「不要」，
            // 因此旧的环境变量不该被带过去。
            var detail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId = newId });
            detail.Config.Env.Should().Contain("NEW=1");
            detail.Config.Env.Should().NotContain("OLD=1");
            detail.Config.Cmd.Should().Contain("120");
        }
        finally
        {
            await CleanupAsync(client, newId, containerId);
        }
    }

    [DockerImageFact]
    public async Task 重建_只改环境变量时其余字段保持原样()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-patch-{Guid.NewGuid():N}"[..16];
        string? containerId = null;
        string? newId = null;

        try
        {
            containerId = await CreateAsync(client, name, new { env = _oldEnv });

            // 只给 env：**没给**的字段必须沿用原容器。
            // 否则「我只是改了个环境变量」就会顺手把命令、端口、能力集全清空——
            // 而那些字段界面上根本没展示，用户连自己丢了什么都不知道。
            var recreated = await PostDataAsync<RecreatedPayload>(
                client,
                "recreate",
                new { containerId, config = new { env = _newEnv } });

            newId = recreated.ContainerId;

            var detail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId = newId });
            detail.Config.Env.Should().Contain("NEW=1");
            detail.Config.Env.Should().NotContain("OLD=1");

            // 命令没给 → 保持原样（仍是 sleep 60）
            detail.Config.Cmd.Should().Contain("60");
        }
        finally
        {
            await CleanupAsync(client, newId, containerId);
        }
    }

    [DockerImageFact]
    public async Task 重建_未运行的容器_保持未运行()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var name = $"lc-nostart-{Guid.NewGuid():N}"[..16];
        string? containerId = null;
        string? newId = null;

        try
        {
            // 只创建不启动
            containerId = await CreateAsync(client, name);

            var recreated = await PostDataAsync<RecreatedPayload>(client, "recreate", new { containerId });
            newId = recreated.ContainerId;

            // 擅自启动一个本来就没运行的容器是用户没要求的副作用
            recreated.Running.Should().BeFalse();
        }
        finally
        {
            await CleanupAsync(client, newId, containerId);
        }
    }

    // ==================== 辅助 ====================

    private static async Task<string> CreateAsync(
        HttpClient client,
        string name,
        object? extra = null,
        string? image = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["image"] = image ?? DockerAvailability.ImageReference,
            ["name"] = name,
            ["cmd"] = _sleepCommand,
        };

        if (extra is not null)
        {
            foreach (var property in extra.GetType().GetProperties())
            {
                body[property.Name] = property.GetValue(extra);
            }
        }

        return (await PostDataAsync<CreatedPayload>(client, "create", body)).ContainerId;
    }

    /// <summary>
    /// 等容器真正停下来（最多 10 秒）。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="containerId">容器标识。</param>
    /// <returns>已停止返回 <see langword="true"/>；超时返回 <see langword="false"/>。</returns>
    /// <remarks>
    /// <b>为什么要轮询</b>：<c>kill</c> 返回 204 只意味着「信号已送达」，
    /// 进程真正退出是<b>异步</b>的——立刻 inspect 很可能看到它还在 running。
    /// 而「偶尔红一次」的集成测试比「偶尔红一次」的线上功能更让人不信任。
    /// </remarks>
    private static async Task<bool> WaitUntilStoppedAsync(HttpClient client, string containerId)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var state = await PostDataAsync<StatePayload>(client, "inspect", new { containerId });

            if (!state.State.Running) return true;

            await Task.Delay(250);
        }

        return false;
    }

    private static async Task CleanupAsync(HttpClient client, params string?[] containerIds)
    {
        foreach (var containerId in containerIds)
        {
            if (string.IsNullOrWhiteSpace(containerId))
            {
                continue;
            }

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/remove",
                new { containerId, force = true });
        }
    }

    private static async Task PostOkAsync(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
            body);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"“{action}” 应当成功：{await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<T> PostDataAsync<T>(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
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

    private sealed record LoginPayload(string AccessToken);

    private sealed record CreatedPayload(string ContainerId, List<string> Warnings);

    private sealed record RecreatedPayload(
        string ContainerId,
        string Name,
        string? ReplacedContainerId,
        bool Running,
        List<string> Warnings,
        bool Recreated);

    private sealed record DetailPayload(string Id, string Name, string Image, ConfigPayload Config);

    private sealed record ConfigPayload(List<string> Cmd, List<string> Env);

    private sealed record StatePayload(StateDetail State);

    private sealed record StateDetail(bool Running, bool Paused);
}
