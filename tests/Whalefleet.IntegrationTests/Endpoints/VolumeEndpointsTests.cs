using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
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
/// 数据卷端点（<c>/api/v1/hosts/{hostId}/volumes/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>只碰自己造的卷</b>：删除是本组唯一有破坏性的操作，而本机上已有的卷
/// 不是本项目的测试夹具。所有删除用例的夹具都是带唯一后缀的自造卷，
/// 并在 <c>finally</c> 里强制清理——即便断言中途失败也不会留下垃圾。</para>
/// <para><b>验收项「创建具名卷并挂进容器后能读到内容」由
/// <c>创建_挂进容器后能读到内容</c> 覆盖</b>：它不只验证卷被创建了，
/// 而是让两个容器先后挂同一个卷，用第二个容器读出第一个容器写下的内容——
/// 只有真正落到宿主目录上的数据才可能通过。</para>
/// </remarks>
#pragma warning disable CA1001
public class VolumeEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";

    /// <summary>没有任何角色分配的账号：用于验证 fail-closed。</summary>
    private const string _noRoleName = "norole";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";
    private const string _unknownVolume = "no-such-volume-0123456789";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] _blanks = ["  ", "", "\t"];

    /// <summary>非法卷名样例（抛 1000）。</summary>
    private static readonly string[] _badNames =
        ["  ", "", "\t", "-bad-start", "bad space", "bad/slash"];

    /// <summary>只求占位的单元素名（用于验证 1300 早于业务校验）。</summary>
    private static readonly string[] _singleName = ["x"];

    /// <summary>验证卷被占用时用的常驻容器命令（与镜像测试同一套）。</summary>
    private static readonly string[] _sleepCommand = ["sleep", "600"];

    /// <summary>读回探针文件的命令。</summary>
    private static readonly string[] _readProbe = ["cat", "/data/probe.txt"];

    /// <summary>全批不存在的批量删除夹具。</summary>
    private static readonly string[] _ghostVolumes = ["ghost-a", "ghost-b"];

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

        var passwordHash = hasher.Hash(_password);

        await TestAccounts.CreateUserAsync(users, assignments, _adminName, passwordHash, adminRole);
        await TestAccounts.CreateUserAsync(users, assignments, _noRoleName, passwordHash, null);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("list")]
    [InlineData("inspect")]
    [InlineData("create")]
    [InlineData("remove")]
    [InlineData("batch-remove")]
    public async Task 未认证_返回401(string action)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/{action}",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("create")]
    [InlineData("batch-remove")]
    public async Task 主机不存在_返回1300(string action)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/volumes/{action}",
            new { name = "x", names = _singleName });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 无主机读权限_返回1501()
    {
        using var client = await this.LoginAsync(_noRoleName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/list",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostForbidden);
    }

    [DockerFact]
    public async Task 列出数据卷_返回条目与告警数组()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/list",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<VolumeListPayload>(response);

        // warnings 必须恒为数组而不是 null：前端模板里直接 v-for 遍历它，
        // 少了这个字段就是「列表页整块空白」。
        result.Warnings.Should().NotBeNull();
        result.Items.Should().NotBeNull();

        // 内置卷必然存在，缺失说明映射把名字弄丢了。
        result.Items.Should().NotBeEmpty();

        result.Items.Should().AllSatisfy(item =>
        {
            item.Name.Should().NotBeNullOrWhiteSpace();
            item.Driver.Should().NotBeNullOrWhiteSpace();

            // 字典恒为非 null：模板里直接遍历，少了就是「列表页整块空白」。
            item.Labels.Should().NotBeNull();
        });

        // Docker 19.03 起固定提供这三个内置网络/卷之外的内置卷命名习惯：
        // 卷侧没有内置卷，但绝大多数镜像层会带出至少一个匿名卷 —— 不作断言，
        // 只确保「没有重复名字」，那是映射层最容易犯的错。
        result.Items.Select(item => item.Name).Should().OnlyHaveUniqueItems();

        // 分页字段必须回带：界面用 `total` 算页数，用 `pageIndex` / `pageSize` 校准页码。
        result.Total.Should().BeGreaterThanOrEqualTo(result.Items.Count);
        result.PageIndex.Should().Be(0);
        result.PageSize.Should().Be(20);
        result.Items.Count.Should().BeLessThanOrEqualTo(result.PageSize);
    }

    [DockerFact]
    public async Task 列出数据卷_分页参数生效且页码越界返回空页()
    {
        using var client = await this.LoginAsync();

        using var first = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/list",
            new { pageIndex = 0, pageSize = 1 });

        var firstPage = await ReadDataAsync<VolumeListPayload>(first);

        firstPage.PageSize.Should().Be(1);
        firstPage.Items.Should().HaveCount(1);

        using var beyond = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/list",
            new { pageIndex = 100_000, pageSize = 100 });

        var beyondPage = await ReadDataAsync<VolumeListPayload>(beyond);

        // 越界页返回「空的 items + 不变的 total」：界面据此停在页脚而不是整页报错
        beyondPage.Items.Should().BeEmpty();
        beyondPage.Total.Should().Be(firstPage.Total);
    }

    [DockerFact]
    public async Task 列出数据卷_搜索命中不到时总数为零()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/list",
            new { search = "zzz-绝不可能命中的卷名-zzz" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<VolumeListPayload>(response);

        // 搜索发生在**分页之前**：total 必须是过滤后的条数，
        // 否则页脚会显示一个与翻页结果对不上的数字。
        result.Total.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    [DockerFact]
    public async Task 列出数据卷_搜索按名称命中()
    {
        using var client = await this.LoginAsync();

        using var all = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/list",
            new { pageSize = 100 });

        var allResult = await ReadDataAsync<VolumeListPayload>(all);

        allResult.Items.Should().NotBeEmpty();

        var target = allResult.Items[0].Name;

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/list",
            new { search = target });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<VolumeListPayload>(response);

        result.Items.Should().Contain(item => item.Name == target);
    }

    [DockerImageFact]
    public async Task 创建_挂进容器后能读到内容()
    {
        using var client = await this.LoginAsync();
        var volume = NewName("vol");
        var writer = NewName("w");
        var reader = NewName("r");

        // DockerImageFact 已保证本机有可用的带标签镜像，运行时不会再是 null。
        var image = DockerAvailability.ImageReference!;
        var probe = "whalefleet-probe-" + Guid.NewGuid().ToString("N")[..8];
        var docker = await GetDockerClientAsync();

        try
        {
            // 1) 建卷（走本项目 API，验证的是「能创建一个可挂载的具名卷」）
            using var created = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/create",
                new { name = volume });

            created.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadDataAsync<VolumeSummaryPayload>(created)).Name.Should().Be(volume);

            // 2) 第一个容器挂上它并写入内容
            var writerId = await CreateProbeContainerAsync(docker, image, writer, volume);

            try
            {
                (await ExecAsync(docker, writerId, new[] { "sh", "-c", $"echo {probe} > /data/probe.txt" }))
                    .Should().Be(0, "写入卷的容器内 shell 必须可执行");
            }
            finally
            {
                await RemoveContainerAsync(docker, writerId);
            }

            // 3) 第二个容器挂**同一个**卷读出内容
            var readerId = await CreateProbeContainerAsync(docker, image, reader, volume);

            try
            {
                var output = await CaptureExecAsync(docker, readerId, _readProbe);

                output.Trim().Should().Be(probe, "具名卷必须让数据跨容器可见");
            }
            finally
            {
                await RemoveContainerAsync(docker, readerId);
            }

            // 4) 详情里能看到 local 驱动的挂载点（排障时最常被问到的信息）
            using var inspected = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/inspect",
                new { name = volume });

            inspected.StatusCode.Should().Be(HttpStatusCode.OK);

            var detail = await ReadDataAsync<VolumeDetailPayload>(inspected);

            detail.Driver.Should().Be("local");
            detail.Mountpoint.Should().StartWith("/");
            detail.Options.Should().NotBeNull();
        }
        finally
        {
            await RemoveVolumeAsync(client, volume);
        }
    }

    [DockerFact]
    public async Task 创建_同名卷不报错且幂等()
    {
        using var client = await this.LoginAsync();
        var volume = NewName("dup");

        try
        {
            using var first = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/create",
                new { name = volume });

            first.StatusCode.Should().Be(HttpStatusCode.OK);

            // Docker 的既定行为：同名创建返回已存在的那一个，而不是 409。
            // 这条用例把这个行为钉死——若将来改成报错，界面与测试都要知道。
            using var second = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/create",
                new { name = volume });

            second.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadDataAsync<VolumeSummaryPayload>(second)).Name.Should().Be(volume);
        }
        finally
        {
            await RemoveVolumeAsync(client, volume);
        }
    }

    [DockerFact]
    public async Task 删除_之后查应如实变成1401()
    {
        using var client = await this.LoginAsync();
        var volume = NewName("del");

        using (var created = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/create",
            new { name = volume }))
        {
            created.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var removed = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/remove",
            new { name = volume }))
        {
            removed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var gone = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/inspect",
            new { name = volume });

        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(gone)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerImageFact]
    public async Task 删除_仍被容器引用时返回1404()
    {
        using var client = await this.LoginAsync();
        var volume = NewName("busy");
        var container = NewName("busy");

        // DockerImageFact 已保证本机有可用的带标签镜像。
        var image = DockerAvailability.ImageReference!;
        var docker = await GetDockerClientAsync();

        try
        {
            using var created = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/create",
                new { name = volume });

            created.StatusCode.Should().Be(HttpStatusCode.OK);

            var containerId = await CreateProbeContainerAsync(docker, image, container, volume);

            try
            {
                // 不带 force：守护进程拒绝，报 409 / 1404。
                // 归成 1402/500 会让「先把容器删掉再删卷」看起来像服务端故障。
                using var refused = await client.PostAsJsonAsync(
                    $"/api/v1/hosts/{_localHost}/volumes/remove",
                    new { name = volume });

                refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await ReadCodeAsync(refused)).Should().Be(ErrorCodes.DockerResourceConflict);

                // ⚠️ 带 force 仍然 409 —— 已实测：只要还有容器挂着这个卷，
                // 守护进程就拒绝删除，与容器是否运行无关。
                // 把它做成「能强行删掉在用卷」的开关，会让人点了没反应还不知道原因。
                using var forced = await client.PostAsJsonAsync(
                    $"/api/v1/hosts/{_localHost}/volumes/remove",
                    new { name = volume, force = true });

                forced.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await ReadCodeAsync(forced)).Should().Be(ErrorCodes.DockerResourceConflict);
            }
            finally
            {
                await RemoveContainerAsync(docker, containerId);
            }

            // 容器删掉之后才真的删得掉——这才是界面上该提示的顺序。
            using var removed = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/remove",
                new { name = volume });

            removed.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await RemoveVolumeAsync(client, volume);
        }
    }

    [Fact]
    public async Task 删除_force只忽略不存在不会掩盖主机不可达()
    {
        using var client = await this.LoginAsync();

        // force=true 时我们会先探一次存在性；那一步若把「连不上」误判成「不存在」，
        // 用户就会看到 200 而实际一个卷都没删——这是最危险的静默失败，必须钉死。
        using var unreachable = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/volumes/remove",
            new { name = NewName("x"), force = true });

        unreachable.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(unreachable)).Should().Be(ErrorCodes.HostNotFound);
    }

    [DockerFact]
    public async Task 删除_force让不存在的卷变成幂等成功()
    {
        using var client = await this.LoginAsync();
        var ghost = NewName("ghost");

        // 不带 force：卷不存在 → 404。
        using (var plain = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/remove",
            new { name = ghost }))
        {
            plain.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ReadCodeAsync(plain)).Should().Be(ErrorCodes.DockerResourceNotFound);
        }

        // 带 force：这是 force 对卷**唯一**的作用——让重试变安全。
        // 批量删除的前端会把上一轮已删项再提交一次，这时不该整批报错。
        using (var forced = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/remove",
            new { name = ghost, force = true }))
        {
            forced.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task 详情_卷名非法返回1000()
    {
        using var client = await this.LoginAsync();

        foreach (var invalid in _badNames)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/inspect",
                new { name = invalid });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task 创建_卷名非法返回1000()
    {
        using var client = await this.LoginAsync();

        foreach (var invalid in _badNames)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/create",
                new { name = invalid });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
        }
    }

    [DockerFact]
    public async Task 详情_卷不存在返回1401()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/inspect",
            new { name = _unknownVolume });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerFact]
    public async Task 删除_卷不存在返回1401()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/remove",
            new { name = _unknownVolume });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    /// <summary>批量删除夹具：重复项会被去重，因此<b>不算</b>入参非法。</summary>
    /// <remarks>
    /// 列表去重是 <c>BatchRequest.NormalizeIds</c> 的既定行为：
    /// 「删 1 个，成功 1 个」正是用户期望的语义，把重复项当错误反而会让人以为系统在胡说。
    /// </remarks>
    private static readonly string[] _dupVolumes = ["a", "a", " a "];

    [Theory]
    [InlineData("list")]
    [InlineData("blanks")]
    public async Task 批量删除_入参非法返回1000(string kind)
    {
        using var client = await this.LoginAsync();

        string[] names = kind switch
        {
            "list" => [],
            _ => _blanks,
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/batch-remove",
            new { names });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [DockerFact]
    public async Task 批量删除_部分成功也返回200并逐条汇报()
    {
        using var client = await this.LoginAsync();
        var real = NewName("ok");

        try
        {
            using (var created = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/create",
                new { name = real }))
            {
                created.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/batch-remove",
                new { names = new[] { real, _unknownVolume } });

            // 关键断言：有一个失败**不代表**整批是错误响应。
            // 报 4xx 的话界面既不知道哪条失败，也不知道那条真的删掉没删掉。
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var result = await ReadDataAsync<BatchPayload>(response);

            result.Requested.Should().Be(2);
            result.Succeeded.Should().Be(1);
            result.Failures.Should().ContainSingle();
            result.Failures[0].ResourceId.Should().Be(_unknownVolume);

            // 错误码必须与单条端点一致，界面才能复用同一套提示文案。
            result.Failures[0].Code.Should().Be(ErrorCodes.DockerResourceNotFound);
        }
        finally
        {
            await RemoveVolumeAsync(client, real);
        }
    }

    [DockerFact]
    public async Task 批量删除_重复项按去重后计数()
    {
        using var client = await this.LoginAsync();

        // 三个条目归一后只剩一个，因此 requested 必须报 1 而不是 3。
        // 不去重的话，「删 1 个成功 1 个失败 2 个，失败的还都是同一个」看起来像系统在胡说。
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/batch-remove",
            new { names = _dupVolumes });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<BatchPayload>(response);

        result.Requested.Should().Be(1);
        result.Succeeded.Should().Be(0);
        result.Failures.Should().ContainSingle();
        result.Failures[0].ResourceId.Should().Be("a");
    }

    [DockerFact]
    public async Task 批量删除_全批不存在也返回200()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/batch-remove",
            new { names = _ghostVolumes });

        // 逐条失败原因都在 failures 里，没有哪一个能代表整批的状态码。
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<BatchPayload>(response);

        result.Succeeded.Should().Be(0);
        result.Failures.Should().HaveCount(2);
    }

    /// <summary>
    /// 生成一个带唯一后缀的资源名（Docker 名字只允许字母数字与 <c>_ . -</c>）。
    /// </summary>
    /// <param name="prefix">前缀（用于人眼辨认用途）。</param>
    /// <returns>合法且不会撞上其他用例的名字。</returns>
    /// <remarks>
    /// 刻意用 GUID 后缀而不是固定名字：集成测试<b>并行</b>跑，固定名字会让两个用例
    /// 互相删掉对方的夹具，表现为随机的 1401 / 1404，排查成本极高。
    /// <br/>
    /// <b>不截断</b>：卷名没有长度上限，而无条件 <c>[..40]</c> 会在前缀较短时
    /// （如 <c>wf-t-w-</c> + 32 位 GUID = 39 字符）对不足 40 的串抛
    /// <see cref="ArgumentOutOfRangeException"/>——一个与被测行为毫无关系的崩溃。
    /// </remarks>
    private static string NewName(string prefix) => $"wf-t-{prefix}-{Guid.NewGuid():N}";

    private async Task<HttpClient> LoginAsync(string user = _adminName)
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = user, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (await ReadDataAsync<LoginPayload>(login)).AccessToken);

        return client;
    }

    private async Task<IDockerClient> GetDockerClientAsync()
    {
        using var scope = _factory.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IDockerHostAccessor>();

        return await accessor.GetClientAsync(_localHost, CancellationToken.None);
    }

    /// <summary>
    /// 尽力删掉一个自造卷（清理用，任何失败都不该让测试变红）。
    /// </summary>
    /// <param name="client">已认证的客户端。</param>
    /// <param name="name">卷名。</param>
    private static async Task RemoveVolumeAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/remove",
            new { name, force = true });
    }

    /// <summary>
    /// 直接用 SDK 建一个挂载了指定卷的探针容器，并启动它。
    /// </summary>
    /// <param name="docker">Docker 客户端。</param>
    /// <param name="image">镜像引用。</param>
    /// <param name="name">容器名。</param>
    /// <param name="volume">要挂载的卷名。</param>
    /// <returns>容器 ID。</returns>
    /// <remarks>
    /// <para><b>为什么绕开本项目的 <c>containers/create</c></b>：本用例要验的是
    /// <b>卷</b>，不是容器的创建参数映射。走 SDK 建夹具能让失败只指向卷本身，
    /// 而不是「两个特性碰巧都有问题」时无法分辨。</para>
    /// <para><b>为什么强制 <c>User = "root"</c></b>：具名卷的根目录由 root 拥有且权限 0755，
    /// 而 <see cref="DockerAvailability.ImageReference"/> 挑到的是本机第一张有标签的镜像
    /// ——在这台机器上恰好是本项目自己的 API 镜像，它以非 root 的 <c>app</c> 用户运行，
    /// 于是 <c>echo &gt; /data/x</c> 会以 <c>Permission denied</c> 失败。
    /// 那是环境属性，不是卷坏了。固定用 root 让用例在任何镜像下都成立。</para>
    /// </remarks>
    private static async Task<string> CreateProbeContainerAsync(
        IDockerClient docker,
        string image,
        string name,
        string volume)
    {
        var container = await docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = image,
                Name = name,
                User = "root",
                Cmd = _sleepCommand,
                HostConfig = new HostConfig { Binds = [$"{volume}:/data"] },
            },
            CancellationToken.None);

        // 必须启动：exec 只能对**运行中**的容器执行，
        // 对已创建的容器会回 409「container … is not running」。
        await docker.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters(),
            CancellationToken.None);

        return container.ID;
    }

    /// <summary>
    /// 直接用 SDK 删容器（清理用）。
    /// </summary>
    /// <param name="docker">Docker 客户端。</param>
    /// <param name="id">容器 ID。</param>
    /// <remarks>不走本项目 API：清理逻辑与本次验证无关，不该给它添变量。</remarks>
    private static async Task RemoveContainerAsync(IDockerClient docker, string id)
    {
        try
        {
            await docker.Containers.RemoveContainerAsync(
                id,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                CancellationToken.None);
        }
        catch (Exception)
        {
            // 清理失败不应掩盖真正的断言失败。
        }
    }

    /// <summary>
    /// 在容器内执行一条命令，只关心退出码。
    /// </summary>
    /// <param name="docker">Docker 客户端。</param>
    /// <param name="containerId">容器 ID。</param>
    /// <param name="command">命令与参数。</param>
    /// <returns>退出码；守护进程尚未回填时为 <c>-1</c>。</returns>
    private static async Task<long> ExecAsync(
        IDockerClient docker,
        string containerId,
        IReadOnlyList<string> command)
    {
        var exec = await docker.Exec.CreateContainerExecAsync(
            containerId,
            new ContainerExecCreateParameters
            {
                Cmd = [.. command],
                AttachStdout = true,
                AttachStderr = true,
            },
            CancellationToken.None);

        var stream = await docker.Exec.StartContainerExecAsync(
            exec.ID,
            new ContainerExecStartParameters { Detach = false },
            CancellationToken.None);

        // 必须把输出读完才拿得到退出码：进程还在往管道里写时，
        // Inspect 回来的 Running 仍是 true、ExitCode 是 null。
        using (stream)
        {
            await stream.ReadOutputToEndAsync(CancellationToken.None);
        }

        var inspected = await docker.Exec.InspectContainerExecAsync(exec.ID, CancellationToken.None);

        return inspected.ExitCode ?? -1;
    }

    /// <summary>
    /// 在容器内执行一条命令并收集标准输出。
    /// </summary>
    /// <param name="docker">Docker 客户端。</param>
    /// <param name="containerId">容器 ID。</param>
    /// <param name="command">命令与参数。</param>
    /// <returns>标准输出文本。</returns>
    /// <remarks>
    /// <b>直接用 SDK 的 <c>ReadOutputToEndAsync</c> 而不是自己解帧</b>：
    /// Docker 的 exec 输出是「8 字节头 + 负载」的帧流（首字节 1=stdout、2=stderr），
    /// 手写解析器既要处理半截帧，又容易把 stderr 混进结果里。SDK 已经封装了这一层。
    /// </remarks>
    private static async Task<string> CaptureExecAsync(
        IDockerClient docker,
        string containerId,
        IReadOnlyList<string> command)
    {
        var exec = await docker.Exec.CreateContainerExecAsync(
            containerId,
            new ContainerExecCreateParameters
            {
                Cmd = [.. command],
                AttachStdout = true,
                AttachStderr = false,
            },
            CancellationToken.None);

        var stream = await docker.Exec.StartContainerExecAsync(
            exec.ID,
            new ContainerExecStartParameters { Detach = false },
            CancellationToken.None);

        using (stream)
        {
            var (stdout, _) = await stream.ReadOutputToEndAsync(CancellationToken.None);

            return stdout;
        }
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue(
            "响应体：" + body.GetRawText());

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

    private sealed record VolumeListPayload(
        List<VolumeSummaryPayload> Items,
        int Total,
        int PageIndex,
        int PageSize,
        List<string> Warnings);

    private sealed record VolumeSummaryPayload(
        string Name,
        string Driver,
        string Mountpoint,
        string Scope,
        // ⚠️ 可空：守护进程不报用量时后端给 null（不是 0）
        long? Size,
        long? RefCount,
        Dictionary<string, string> Labels);

    private sealed record VolumeDetailPayload(
        string Name,
        string Driver,
        string Mountpoint,
        string Scope,
        Dictionary<string, string> Options,
        Dictionary<string, string> Labels);

    private sealed record BatchPayload(
        int Requested,
        int Succeeded,
        List<FailurePayload> Failures);

    private sealed record FailurePayload(string ResourceId, int Code, string Message);
}
