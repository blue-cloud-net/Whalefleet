using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// 网络端点（<c>/api/v1/hosts/{hostId}/networks/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>只碰自己造的网络</b>：内置网络（<c>bridge</c> / <c>host</c> / <c>null</c>）
/// 不属于本项目，而用户自建网络才是夹具。删除用例一律用唯一后缀的自造网络，
/// 并在 <c>finally</c> 里清理。</para>
/// <para><b>验收项「创建自定义网络并把容器接入」由
/// <c>创建_容器接入后可断开并删除</c> 覆盖</b>：
/// 它把完整生命周期走一遍——建网 → 容器接入 → 删网被拒（1404）→ 断开 → 删得掉。
/// 只验「能建出来」是不够的：接入与断开才是网络最容易出错的地方。</para>
/// <para><b>子网重叠用例刻意避开了默认地址池</b>：守护进程默认从 <c>172.17.0.0/16</c> 起分配，
/// 用例先建一个 <c>bridge</c> 网络拿到实际网段，再拿它去撞，这样无论机器怎么配都能复现。</para>
/// </remarks>
#pragma warning disable CA1001
public class NetworkEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";

    /// <summary>没有任何角色分配的账号：用于验证 fail-closed。</summary>
    private const string _noRoleName = "norole";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";
    private const string _unknownNetwork = "no-such-network-0123456789";

    /// <summary>Docker 固定提供的内置网络。</summary>
    /// <remarks>
    /// <b>只列当前守护进程上真实存在的</b>：Docker 26 把「禁用网络」从 <c>null</c>
    /// 改名为 <c>none</c>，而 26 之前的版本两者行为不同。硬列全部三个会让
    /// 其中一条拿到 404（资源不存在），而不是本用例真正要验的 1404。
    /// 服务端的保护名单两个都收，这里只跟本机实际有的对齐。
    /// </remarks>
    private static readonly string[] _builtIn = ["bridge", "host", "none"];

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] _blanks = ["  ", "", "\t"];

    /// <summary>非法网络名样例（抛 1000）。</summary>
    private static readonly string[] _badNames =
        ["  ", "", "\t", "-bad-start", "bad space", "bad/slash"];

    /// <summary>验证网络被占用时用的常驻容器命令。</summary>
    private static readonly string[] _sleepCommand = ["sleep", "600"];

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
    public async Task 未认证_返回401(string action)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/{action}",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("create")]
    public async Task 主机不存在_返回1300(string action)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/networks/{action}",
            new { name = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 无主机读权限_返回1501()
    {
        using var client = await this.LoginAsync(_noRoleName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/list",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostForbidden);
    }

    [DockerFact]
    public async Task 列出网络_含内置网络且字段齐全()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/list",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<NetworkListPayload>(response);

        result.Items.Should().NotBeNull();

        // 内置网络必然在：缺失说明映射丢了名字。
        result.Items.Should().Contain(item => _builtIn.Contains(item.Name));

        // 每一项都要能拿去用：删除/详情走 ID，展示走名字。
        result.Items.Should().AllSatisfy(item =>
        {
            // 网络 ID 是裸的 64 位十六进制，**没有** sha256: 前缀（镜像 ID 才有）。
            // 这条断言守住的就是那个前缀——写成 StartsWith("sha256:") 会一直红，
            // 而照着镜像的解析逻辑去剥前缀会得到一个不存在的 ID（表现为 404）。
            item.Id.Should().MatchRegex("^[0-9a-f]{64}$");
            item.Driver.Should().NotBeNullOrWhiteSpace();
            item.Subnets.Should().NotBeNull();
            item.Gateways.Should().NotBeNull();
            item.Labels.Should().NotBeNull();
        });

        // 名字唯一：重复会让界面上出现两行同名、只有 ID 能分辨的网络。
        result.Items.Select(item => item.Name).Should().OnlyHaveUniqueItems();
    }

    [DockerFact]
    public async Task 创建_子网与网关写入IPAM()
    {
        using var client = await this.LoginAsync();
        var network = NewName("net");
        var subnet = $"172.{Random.Shared.Next(20, 30)}.0.0/16";

        try
        {
            using var created = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/create",
                new { name = network, driver = "bridge", subnet, attachable = true });

            created.StatusCode.Should().Be(HttpStatusCode.OK);

            var summary = await ReadDataAsync<NetworkSummaryPayload>(created);

            summary.Name.Should().Be(network);
            summary.Attachable.Should().BeTrue();
            summary.Subnets.Should().Contain(subnet);

            // 详情里必须能看到 IPAM 三元组，而不只是「建成功了」。
            using var inspected = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/inspect",
                new { networkId = network });

            inspected.StatusCode.Should().Be(HttpStatusCode.OK);

            var detail = await ReadDataAsync<NetworkDetailPayload>(inspected);

            detail.Ipam.Should().ContainSingle();
            detail.Ipam[0].Subnet.Should().Be(subnet);
            detail.Ipam[0].Gateway.Should().NotBeNullOrWhiteSpace();
            detail.Ipam[0].IpRange.Should().BeNull("未指定时必须回 null 而不是空串");
            detail.Containers.Should().NotBeNull("空数组而不是 null");
            detail.Options.Should().NotBeNull();
        }
        finally
        {
            await RemoveNetworkAsync(client, network);
        }
    }

    [DockerFact]
    public async Task 创建_IPv6子网自动启用IPv6()
    {
        using var client = await this.LoginAsync();
        var network = NewName("v6");
        var subnet = "fd00:dead:beef::/64";

        try
        {
            using var created = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/create",
                new { name = network, driver = "bridge", subnet });

            created.StatusCode.Should().Be(HttpStatusCode.OK);

            using var inspected = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/inspect",
                new { networkId = network });

            // 不自动打开 EnableIPv6 的话，守护进程会建出一个「有 IPv6 地址池却不通」的网，
            // 而界面上看不出任何异常——这类坑必须在服务端堵。
            (await ReadDataAsync<NetworkDetailPayload>(inspected)).EnableIPv6.Should().BeTrue();
        }
        finally
        {
            await RemoveNetworkAsync(client, network);
        }
    }

    [DockerFact]
    public async Task 创建_同名网络返回1404()
    {
        using var client = await this.LoginAsync();
        var network = NewName("dup");

        try
        {
            using (var first = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/create",
                new { name = network }))
            {
                first.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            using var second = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/create",
                new { name = network });

            // 守护进程对「同名」回 403；不翻译就是 1402/500——用户改个名就能解决的事
            // 被报成服务端故障。
            second.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadCodeAsync(second)).Should().Be(ErrorCodes.DockerResourceConflict);
            (await ReadMessageAsync(second)).Should().Contain(network);
        }
        finally
        {
            await RemoveNetworkAsync(client, network);
        }
    }

    [DockerFact]
    public async Task 创建_子网与现有网络重叠返回1404()
    {
        using var client = await this.LoginAsync();
        var first = NewName("sub-a");
        var second = NewName("sub-b");
        var subnet = $"172.{Random.Shared.Next(31, 40)}.0.0/16";

        try
        {
            using var created = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/create",
                new { name = first, driver = "bridge", subnet });

            created.StatusCode.Should().Be(HttpStatusCode.OK);

            using var conflict = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/create",
                new { name = second, driver = "bridge", subnet });

            // 与「同名」同属 403，但消息不同——保留守护进程的原因文本，
            // 用户据此知道该改名字还是改子网。
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadCodeAsync(conflict)).Should().Be(ErrorCodes.DockerResourceConflict);
            (await ReadMessageAsync(conflict)).Should().Contain(second);
        }
        finally
        {
            await RemoveNetworkAsync(client, first);
            await RemoveNetworkAsync(client, second);
        }
    }

    [DockerImageFact]
    public async Task 创建_容器接入后可断开并删除()
    {
        using var client = await this.LoginAsync();
        var network = NewName("att");
        var containerName = NewName("netc");

        // DockerImageFact 已保证本机有可用的带标签镜像。
        var image = DockerAvailability.ImageReference!;
        var docker = await GetDockerClientAsync();

        string containerId = string.Empty;

        try
        {
            using var created = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/create",
                new { name = network, driver = "bridge" });

            created.StatusCode.Should().Be(HttpStatusCode.OK);

            // 容器**直接**建在这个网络上（而不是建完再 connect）：
            // 这正是 0.2.0 要交付的「创建容器时可选网络」能力。
            using var containerCreated = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/create",
                new
                {
                    image,
                    name = containerName,
                    cmd = _sleepCommand,
                    network,
                });

            containerCreated.StatusCode.Should().Be(HttpStatusCode.OK);
            containerId = (await ReadDataAsync<CreatedPayload>(containerCreated)).ContainerId;

            // ⚠️ 必须先 start：实测 `docker create` 之后、`docker start` 之前，
            // 容器**不会**出现在 `GET /networks/{id}` 的 Containers 里（已用 SDK 核对过）。
            // 跳过这一步的话断言看到的是「0 个容器」，而那并不是本用例要验的行为。
            await docker.Containers.StartContainerAsync(
                containerId,
                new ContainerStartParameters(),
                CancellationToken.None);

            // 网络详情里必须能看到这个端点及其 IP——这是「接入成功」的唯一凭据。
            using var inspected = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/inspect",
                new { networkId = network });

            var detail = await ReadDataAsync<NetworkDetailPayload>(inspected);

            detail.Containers.Should().ContainSingle();
            detail.Containers[0].Name.Should().Be(containerName);
            detail.Containers[0].Ipv4Address.Should().NotBeNullOrWhiteSpace();

            // 列表里不应出现任何「容器数」字段：Docker 的列表接口不返回 Containers，
            // 硬补一个会恒为 0，让界面把正在用的网络显示成「0 个容器」。
            // 这里查原始 JSON 而不是反序列化后的对象——记录类型上没这个字段，
            // 反序列化会把「字段不存在」与「值为 0」同样地抹平。
            var listedBody = await ReadAsync(
                await client.PostAsJsonAsync(
                    $"/api/v1/hosts/{_localHost}/networks/list",
                    new { }));

            foreach (var item in listedBody.GetProperty("data").GetProperty("items").EnumerateArray())
            {
                item.TryGetProperty("containerCount", out _).Should().BeFalse(
                    "列表接口无法提供真实容器数，给一个恒为 0 的字段比不给更糟");
            }

            listedBody.GetProperty("data").GetProperty("items")
                .EnumerateArray()
                .Should().Contain(item => item.GetProperty("name").GetString() == network);

            // 仍有容器接入时删不掉，且消息要说清「有几个、先断开」。
            using var refused = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/remove",
                new { networkId = network });

            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadCodeAsync(refused)).Should().Be(ErrorCodes.DockerResourceConflict);
            (await ReadMessageAsync(refused)).Should().Contain("1");

            // 先停再删：运行中的容器会占着端点，断开时守护进程才肯放行。
            await docker.Containers.StopContainerAsync(
                containerId,
                new ContainerStopParameters(),
                CancellationToken.None);

            await docker.Networks.DisconnectNetworkAsync(
                network,
                new NetworkDisconnectParameters { Container = containerId, Force = true },
                CancellationToken.None);

            using var removed = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/remove",
                new { networkId = network });

            removed.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            if (!String.IsNullOrEmpty(containerId))
            {
                await RemoveContainerAsync(docker, containerId);
            }

            await RemoveNetworkAsync(client, network);
        }
    }

    [DockerTheory]
    [InlineData("bridge")]
    [InlineData("host")]
    [InlineData("none")]
    public async Task 删除_内置网络返回1404(string name)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/remove",
            new { networkId = name });

        // 内置网络删掉会波及主机上所有容器，且守护进程本身也会拒绝。
        // 提前拦下是为了让界面拿到一个可翻译的错误码分支，而不是一句英文 403。
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceConflict);
        (await ReadMessageAsync(response)).Should().Contain(name);
    }

    [DockerFact]
    public async Task 删除_之后查应如实变成1401()
    {
        using var client = await this.LoginAsync();
        var network = NewName("del");

        using (var created = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/create",
            new { name = network }))
        {
            created.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var removed = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/remove",
            new { networkId = network }))
        {
            removed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var gone = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/inspect",
            new { networkId = network });

        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(gone)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("create")]
    public async Task 详情与创建_名字非法返回1000(string action)
    {
        using var client = await this.LoginAsync();

        foreach (var invalid in _badNames)
        {
            object body = action == "inspect"
                ? new { networkId = invalid }
                : new { name = invalid };

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/networks/{action}",
                body);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
        }
    }

    [Theory]
    [InlineData("172.18.0.0")]         // 缺前缀长度
    [InlineData("172.18.0.0/33")]      // IPv4 前缀越界
    [InlineData("999.1.1.1/16")]       // 地址不合法
    [InlineData("fd00::/200")]         // IPv6 前缀越界
    [InlineData("not-an-ip/16")]       // 完全不是网段
    public async Task 创建_网段格式非法返回1000(string subnet)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/create",
            new { name = NewName("bad"), subnet });

        // 守护进程对格式错误回 400，归成 1402/500 会让「自己填的网段打错一个字符」
        // 显示成服务端故障。在本层挡下还能把提示落到对应输入框上。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
        (await ReadMessageAsync(response)).Should().Contain("subnet");
    }

    [DockerFact]
    public async Task 详情_网络不存在返回1401()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/inspect",
            new { networkId = _unknownNetwork });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerFact]
    public async Task 删除_网络不存在返回1401()
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/remove",
            new { networkId = _unknownNetwork });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
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
    /// <b>不截断</b>：网络名没有长度上限，而无条件 <c>[..40]</c> 会在前缀较短时
    /// 对不足 40 的串抛 <see cref="ArgumentOutOfRangeException"/>。
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
    /// 尽力删掉一个自造网络（清理用，任何失败都不该让测试变红）。
    /// </summary>
    /// <param name="client">已认证的客户端。</param>
    /// <param name="name">网络名。</param>
    private static async Task RemoveNetworkAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/networks/remove",
            new { networkId = name });
    }

    /// <summary>
    /// 直接用 SDK 删容器（清理用）。
    /// </summary>
    /// <param name="docker">Docker 客户端。</param>
    /// <param name="id">容器 ID。</param>
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

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue(
            "响应体：" + body.GetRawText());

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

    private sealed record NetworkListPayload(List<NetworkSummaryPayload> Items);

    private sealed record NetworkSummaryPayload(
        string Id,
        string Name,
        string Driver,
        string Scope,
        bool Attachable,
        bool Internal,
        List<string> Subnets,
        List<string> Gateways,
        Dictionary<string, string> Labels);

    private sealed record NetworkDetailPayload(
        string Id,
        string Name,
        string Driver,
        bool Attachable,
        bool Internal,
        bool EnableIPv4,
        bool EnableIPv6,
        List<IpamPayload> Ipam,
        List<EndpointPayload> Containers,
        Dictionary<string, string> Options,
        Dictionary<string, string> Labels);

    private sealed record IpamPayload(string? Subnet, string? Gateway, string? IpRange);

    private sealed record EndpointPayload(
        string Id,
        string Name,
        string? MacAddress,
        string? Ipv4Address,
        string? Ipv6Address);

    private sealed record BatchPayload(
        int Requested,
        int Succeeded,
        List<FailurePayload> Failures);

    private sealed record FailurePayload(string ResourceId, int Code, string Message);
}
