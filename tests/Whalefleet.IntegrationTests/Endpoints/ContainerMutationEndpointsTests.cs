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
/// 容器基础操作端点（rename / update / duplicate / add-volumes）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>两类用例，各证明一件事</b>：不带 <see cref="DockerFactAttribute"/> 的证明
/// 「守卫链」（认证 → 主机存在 → 可见性 → 参数校验）生效且错误码分得对；
/// 带 <see cref="DockerImageFactAttribute"/> 的证明「真在守护进程上按预期生效」。</para>
/// <para><b>每个重建类用例都必须清理两代容器</b>：<c>duplicate</c> 留 2 个，
/// <c>add-volumes</c> 留 2 个（旧的让位容器 + 新的）——只清理新容器会让下一次跑时
/// 名字冲突，从而把这个用例变成「随机失败」。</para>
/// </remarks>
#pragma warning disable CA1001
public class ContainerMutationEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
    private const string _noRoleName = "norole";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";
    private const string _unknownContainer = "no-such-container-0123456789";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] _sleepCommand = ["sleep", "60"];

    /// <summary>单个 capability：既测「既增又删同一项」也用作合法对照项。</summary>
    private static readonly string[] _netAdmin = ["NET_ADMIN"];

    /// <summary>含空项的 capability 列表：模拟前端把空输入框也提交了。</summary>
    private static readonly string[] _capWithBlank = ["", "NET_ADMIN"];

    /// <summary>空的 healthcheck 命令列表。</summary>
    private static readonly string[] _emptyTest = [];

    /// <summary>关闭健康检查。</summary>
    private static readonly string[] _noneTest = ["NONE"];

    /// <summary>一个确定会失败的健康检查命令（这里只看能否写入，判定结果与用例无关）。</summary>
    private static readonly string[] _failingTest = ["CMD-SHELL", "true"];

    /// <summary>capDrop ALL：验证高级能力参数能真的写进去。</summary>
    private static readonly string[] _dropAll = ["ALL"];

    /// <summary>环境变量：验证复制出的容器保留了原容器的 Env。</summary>
    private static readonly string[] _keepEnv = ["KEEP=1"];

    /// <summary>内存下限：6 MiB（Docker 守护进程的硬性下限，实测 6291455 会被拒、6291456 才过）。</summary>
    private const long _sixMiB = 6L * 1024 * 1024;

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

        adminRole.Should().NotBeNull("内置角色应由迁移种入");
        operatorRole.Should().NotBeNull("内置角色应由迁移种入");

        var passwordHash = hasher.Hash(_password);

        await TestAccounts.CreateUserAsync(users, assignments, _adminName, passwordHash, adminRole);
        await TestAccounts.CreateUserAsync(users, assignments, _operatorName, passwordHash, operatorRole);
        await TestAccounts.CreateUserOnlyAsync(users, _noRoleName, passwordHash);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ==================== 守卫链 ====================

    [Theory]
    [InlineData("rename")]
    [InlineData("update")]
    [InlineData("duplicate")]
    [InlineData("add-volumes")]
    public async Task 基础操作_未认证_返回401(string action)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/containers/{action}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("update")]
    [InlineData("duplicate")]
    [InlineData("add-volumes")]
    public async Task 基础操作_未知主机_返回1300(string action)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/containers/{action}",
            new { containerId = "anything", name = "x", restartPolicy = "always" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("update")]
    [InlineData("duplicate")]
    [InlineData("add-volumes")]
    public async Task 基础操作_无任何角色分配_返回1501(string action)
    {
        using var client = await CreateAuthenticatedClientAsync(_noRoleName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
            new { containerId = "anything", name = "x", restartPolicy = "always" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostForbidden);
    }

    // ==================== rename 的参数校验 ====================

    [Fact]
    public async Task 重命名_容器标识为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/rename",
            new { containerId = "  ", name = "ok-name" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-不能以横杠开头")]
    [InlineData("有/斜杠")]
    public async Task 重命名_新名称非法_返回1000(string name)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/rename",
            new { containerId = "anything", name });

        // 名字非法必须本地挡：守护进程对非法名称只回一句 400，不说是哪个字符不合法
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    // ==================== update 的参数校验 ====================

    [Fact]
    public async Task 更新_三个字段全省略_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/update",
            new { containerId = "anything" });

        // 全省略等于什么都不改。返回 200 会让用户以为「保存成功了」
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 更新_重启策略非法_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/update",
            new { containerId = "anything", restartPolicy = "sometimes" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 更新_内存上限为负_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/update",
            new { containerId = "anything", memoryLimitBytes = -1L });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 更新_内存上限低于守护进程下限_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/update",
            new { containerId = "anything", memoryLimitBytes = 1024L });

        // 低于 4 MiB 时守护进程会在**启动容器时**失败，报出来的是 OOM-killed，
        // 完全指不出真正原因。这里提前挡下才能给出可操作的提示。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 更新_CPU配额超过上限_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        // 上限是 1024 核 × 10^9。这里传 2000 核——它**不是**一个常见的错值，
        // 因此这条用例守的是「上限确实存在」而不是某个具体数字的校验。
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/update",
            new { containerId = "anything", nanoCpus = 2_000_000_000_000L });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    // ==================== duplicate 的参数校验 ====================

    [Fact]
    public async Task 复制_目标名称非法_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/duplicate",
            new { containerId = "anything", name = "不合法/名字" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    // ==================== add-volumes 的参数校验 ====================

    [Fact]
    public async Task 追加挂载_挂载列表为null_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/add-volumes",
            new { containerId = "anything" });

        // 空列表意味着「重建了容器但什么也没加」——一次没有任何收益的重建
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 追加挂载_挂载列表为空数组_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/add-volumes",
            new { containerId = "anything", volumes = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 追加挂载_挂载项缺目标路径_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/add-volumes",
            new { containerId = "anything", volumes = new[] { new { source = "/tmp" } } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    // ==================== 高级创建参数的本地校验 ====================

    [Fact]
    public async Task 创建_能力既增又删同一项_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new { image = "nginx:latest", capAdd = _netAdmin, capDrop = _netAdmin });

        // Docker 不报错（两个列表都会发过去），生效哪一个取决于 daemon 内部的合并顺序。
        // 这种请求几乎总是拼错了，本地拒掉比让用户猜结果可靠。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_能力列表含空项_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new { image = "nginx:latest", capAdd = _capWithBlank });

        // 静默跳过空项会让用户以为「我勾的那项生效了」，而真正的问题永远不会被发现
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_健康检查命令为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new { image = "nginx:latest", healthCheck = new { test = _emptyTest } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_健康检查关闭却带时间参数_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new
            {
                image = "nginx:latest",
                healthCheck = new { test = _noneTest, intervalSeconds = 10 },
            });

        // NONE 已经让其余字段失去意义，放行会让用户以为配了宽限期而实际一条都不执行
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_健康检查重试次数为零_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new
            {
                image = "nginx:latest",
                healthCheck = new { test = _failingTest, retries = 0 },
            });

        // retries=0 在 Docker 里的含义是「永不判定失败」（一直 starting），那是不检查
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_设备权限含非法字符_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new
            {
                image = "nginx:latest",
                devices = new[] { new { hostPath = "/dev/snd", permissions = "rwx" } },
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_ulimit缺名称_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new { image = "nginx:latest", ulimits = new[] { new { soft = 1024L, hard = 1024L } } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_ulimit软限制为负_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new { image = "nginx:latest", ulimits = new[] { new { name = "nofile", soft = -1L, hard = 1024L } } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_日志驱动类型为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new { image = "nginx:latest", logDriver = new { options = new Dictionary<string, string> { ["max-size"] = "10m" } } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 创建_内存上限低于守护进程下限_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/create",
            new { image = "nginx:latest", memoryLimitBytes = 1024L });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    // ==================== 真实守护进程路径 ====================

    [DockerTheory]
    [InlineData("rename")]
    [InlineData("update")]
    [InlineData("duplicate")]
    public async Task 基础操作_容器不存在_返回1401(string action)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
            new { containerId = _unknownContainer, name = "whatever", restartPolicy = "always" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerImageFact]
    public async Task 重命名后详情能读到新名字()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-ren-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(client, name);

            var renamed = await PostDataAsync<RenamedPayload>(
                client,
                "rename",
                new { containerId, name = name + "-x" });

            renamed.ContainerId.Should().Be(containerId);
            renamed.Name.Should().Be(name + "-x");

            var detail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId });
            detail.Name.Should().Be(name + "-x");
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 重命名到已占用名称_返回1404()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-busy-{Guid.NewGuid():N}"[..16];
        var other = name + "-b";
        string? containerId = null;
        string? otherId = null;

        try
        {
            containerId = await CreateAsync(client, name);
            otherId = await CreateAsync(client, other);

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/rename",
                new { containerId, name = other });

            // 名字冲突是「改一下就能成功」的状态，必须与 1401/500 分开
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceConflict);
        }
        finally
        {
            await CleanupAsync(client, containerId, otherId);
        }
    }

    [DockerImageFact]
    public async Task 更新重启策略后详情读到新值()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-upd-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(client, name);

            var updated = await PostDataAsync<UpdatedPayload>(
                client,
                "update",
                new { containerId, restartPolicy = "unless-stopped", memoryLimitBytes = _sixMiB });

            updated.RestartPolicy.Should().Be("unless-stopped");
            updated.MemoryLimitBytes.Should().Be(_sixMiB);

            var detail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId });
            detail.HostConfig.RestartPolicy.Should().Be("unless-stopped");
            detail.HostConfig.MemoryLimitBytes.Should().Be(_sixMiB);
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 只改重启策略时内存上限保持不变()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-keep-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(client, name, new { memoryLimitBytes = _sixMiB });

            // 回归防线：只传 restartPolicy 时，memoryLimitBytes 必须回显原来的值而不是 null。
            // Docker 的 update 对「未传字段」的处理随 API 版本变化，老版本会写回 0。
            var updated = await PostDataAsync<UpdatedPayload>(
                client,
                "update",
                new { containerId, restartPolicy = "always" });

            updated.RestartPolicy.Should().Be("always");
            updated.MemoryLimitBytes.Should().Be(_sixMiB);
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 更新_内存上限为零_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/update",
            new { containerId = "anything", memoryLimitBytes = 0L });

        // 实测：`docker update --memory=0` 返回 200，而容器上的限制**根本没变**
        // （守护进程对 update 是「字段非 0 才应用」，0 与「不传」同义）。
        // 放行 0 的后果是用户看到「保存成功」而容器仍跑在旧限制下——那正是限制本该避免的。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 更新_CPU配额为零_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/update",
            new { containerId = "anything", nanoCpus = 0L });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 更新_未限制内存的容器时只给重启策略仍返回无限制()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-nolim-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(client, name);

            // 未设限的容器（Memory=0）：只改重启策略后，回显的内存上限仍应是 null
            var updated = await PostDataAsync<UpdatedPayload>(
                client,
                "update",
                new { containerId, restartPolicy = "always" });

            updated.MemoryLimitBytes.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 复制出的容器与原容器配置一致且原容器仍在()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-dup-{Guid.NewGuid():N}"[..16];
        string? sourceId = null;
        string? copyId = null;

        try
        {
            sourceId = await CreateAsync(
                client,
                name,
                new { env = _keepEnv, cmd = _sleepCommand, restartPolicy = "unless-stopped" });

            var copied = await PostDataAsync<RecreatedPayload>(client, "duplicate", new { containerId = sourceId });

            copyId = copied.ContainerId;
            copied.Name.Should().Be(name + "-copy");
            copied.ReplacedContainerId.Should().BeNull("复制不是重建：原容器不受影响");
            copied.Recreated.Should().BeFalse();

            // 原容器必须还在，且仍是同一个 ID
            var sourceDetail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId = sourceId });
            sourceDetail.Name.Should().Be(name);

            var copyDetail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId = copyId });
            copyDetail.Name.Should().Be(name + "-copy");
            copyDetail.Image.Should().Be(sourceDetail.Image);
            copyDetail.Config.Cmd.Should().BeEquivalentTo(_sleepCommand);
            copyDetail.Config.Env.Should().Contain("KEEP=1");
            copyDetail.HostConfig.RestartPolicy.Should().Be("unless-stopped");
        }
        finally
        {
            await CleanupAsync(client, sourceId, copyId);
        }
    }

    [DockerImageFact]
    public async Task 复制到已占用名称时自动避让()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-avoid-{Guid.NewGuid():N}"[..16];
        string? sourceId = null;
        string? first = null;
        string? second = null;

        try
        {
            sourceId = await CreateAsync(client, name);

            // 第一次用默认名（name-copy），第二次再复制同名容器就会撞上——
            // 连点两次必须都能成功，而不是第二次失败。
            first = (await PostDataAsync<RecreatedPayload>(client, "duplicate", new { containerId = sourceId }))
                .ContainerId;
            second = (await PostDataAsync<RecreatedPayload>(client, "duplicate", new { containerId = sourceId }))
                .ContainerId;

            first.Should().NotBeNullOrWhiteSpace();
            first.Should().NotBe(second);

            var firstDetail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId = first });
            var secondDetail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId = second });

            firstDetail.Name.Should().Be(name + "-copy");
            secondDetail.Name.Should().Be(name + "-copy-2");
        }
        finally
        {
            await CleanupAsync(client, sourceId, first, second);
        }
    }

    [DockerImageFact]
    public async Task 追加挂载后新容器多出该挂载且旧容器让位保留()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-vol-{Guid.NewGuid():N}"[..16];
        string? oldId = null;
        string? newId = null;

        try
        {
            oldId = await CreateAsync(client, name);

            var recreated = await PostDataAsync<RecreatedPayload>(
                client,
                "add-volumes",
                new
                {
                    containerId = oldId,
                    volumes = new[] { new { source = "/tmp", destination = "/mnt/extra" } },
                    start = false,
                });

            newId = recreated.ContainerId;
            recreated.Recreated.Should().BeTrue();
            recreated.ReplacedContainerId.Should().Be(oldId);
            recreated.Running.Should().BeFalse("原本没运行的容器不该被擅自启动");

            // 新容器的 ID 必须与旧的不同——这正是「会重建」的可观察证据
            newId.Should().NotBe(oldId);

            var detail = await PostDataAsync<MountsPayload>(client, "inspect", new { containerId = newId });
            detail.Mounts.Should().Contain(mount => mount.Destination == "/mnt/extra");

            // 旧容器仍在（改名保留），用户可以捞回配置
            var old = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId = oldId });
            old.Name.Should().StartWith("whalefleet-replaced-");
        }
        finally
        {
            await CleanupAsync(client, oldId, newId);
        }
    }

    [DockerImageFact]
    public async Task 追加挂载到已挂载的同一目标路径_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-dupvol-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = await CreateAsync(
                client,
                name,
                new { volumes = new[] { new { source = "/tmp", destination = "/mnt/x" } } });

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/add-volumes",
                new
                {
                    containerId,
                    volumes = new[] { new { source = "/var/tmp", destination = "/mnt/x" } },
                });

            // 同一目标挂两次会让守护进程行为不确定，而这种请求几乎总是配置错了
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 高级参数创建出的容器能inspect到内存上限与能力()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-adv-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = (await PostDataAsync<CreatedPayload>(
                client,
                "create",
                new
                {
                    image = DockerAvailability.ImageReference,
                    name,
                    cmd = _sleepCommand,
                    memoryLimitBytes = 64L * 1024 * 1024,
                    capDrop = _dropAll,
                    ulimits = new[] { new { name = "nofile", soft = 1024L, hard = 1024L } },
                    logDriver = new { type = "json-file", @options = new Dictionary<string, string> { ["max-size"] = "1m" } },
                })).ContainerId;

            var detail = await PostDataAsync<DetailPayload>(client, "inspect", new { containerId });

            detail.HostConfig.MemoryLimitBytes.Should().Be(64L * 1024 * 1024);
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    [DockerImageFact]
    public async Task 健康检查关闭后inspect返回none()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var name = $"dm-hc-{Guid.NewGuid():N}"[..16];
        string? containerId = null;

        try
        {
            containerId = (await PostDataAsync<CreatedPayload>(
                client,
                "create",
                new
                {
                    image = DockerAvailability.ImageReference,
                    name,
                    cmd = _sleepCommand,
                    healthCheck = new { test = _noneTest },
                })).ContainerId;

            // 0.6.0 起：关掉健康检查时 HealthStatus 是 "none" 而不是 null。
            // 「没配」与「查不到」是两件事——混成一个值，界面就没法把两者分开表达，
            // 而「没配」永远不会变、「查不到」会变。
            var detail = await PostDataAsync<StatePayload>(client, "inspect", new { containerId });
            detail.State.HealthStatus.Should().Be("none");
        }
        finally
        {
            await CleanupAsync(client, containerId);
        }
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 建一个容器，返回其 ID。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="name">容器名。</param>
    /// <param name="extra">额外的创建参数。</param>
    /// <returns>容器 ID。</returns>
    private static async Task<string> CreateAsync(HttpClient client, string name, object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["image"] = DockerAvailability.ImageReference,
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
    /// 清理容器（已删掉的会 404，属于预期，不断言状态码）。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="containerIds">要清理的容器 ID（可为 <see langword="null"/>）。</param>
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

    private static async Task<T> PostDataAsync<T>(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/{action}",
            body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"“{action}” 应当成功：{await response.Content.ReadAsStringAsync()}");

        return await ReadDataAsync<T>(response);
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
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed record LoginPayload(string AccessToken);

    private sealed record CreatedPayload(string ContainerId, List<string> Warnings);

    private sealed record RenamedPayload(string ContainerId, string Name);

    private sealed record UpdatedPayload(string ContainerId, string RestartPolicy, long? MemoryLimitBytes, long? NanoCpus);

    private sealed record RecreatedPayload(
        string ContainerId,
        string Name,
        string? ReplacedContainerId,
        bool Running,
        List<string> Warnings,
        bool Recreated);

    private sealed record DetailPayload(string Id, string Name, string Image, ConfigPayload Config, HostConfigPayload HostConfig);

    private sealed record ConfigPayload(List<string> Cmd, List<string> Env);

    private sealed record HostConfigPayload(string RestartPolicy, long? MemoryLimitBytes, long? NanoCpus);

    private sealed record MountsPayload(List<MountPayload> Mounts);

    private sealed record MountPayload(string Type, string Source, string Destination, bool ReadWrite);

    private sealed record StatePayload(StateDetail State);

    private sealed record StateDetail(string? HealthStatus);
}
