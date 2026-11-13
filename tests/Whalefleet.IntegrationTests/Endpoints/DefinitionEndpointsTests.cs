using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Docker.DotNet;
using Docker.DotNet.Models;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Composes;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 反向生成端点（0.14.0）的端到端测试：容器定义（<c>containers/run-command</c>）
/// 与编排原文导出（<c>composes/export</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么容器定义要跑真守护进程</b>：生成的命令完全取决于 inspect 的字段，
/// 而「<c>Config.Image</c> 是可读引用、<c>Image</c> 是 ID」这类差别只有真实 inspect 才有。
/// 用假数据测等于把自己写的夹具测成绿的。</para>
/// <para><b>为什么编排导出不部署</b>：导出只读库里的 YAML——直接往仓储里写一条记录
/// 就能覆盖全部语义，而部署要拉镜像、建网络，慢且与「导出」无关。
/// 唯一需要真实部署的是「未受管的项目报 1903」，那条反过来<b>不</b>写记录即可。</para>
/// </remarks>
#pragma warning disable CA1001
public class DefinitionEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _probeImage = "whalefleet-test-definition-probe:latest";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private ApiFactory _factory = null!;
    private bool _probeImageCreated;

    /// <summary>
    /// 本次测试创建的探针容器（用完即删）。
    /// </summary>
    private readonly List<string> _probeContainerIds = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();

        await CreateAdminUserAsync(_factory);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            using var client = new DockerClientBuilder()
                .WithEndpoint(new Uri("unix:///var/run/docker.sock"))
                .Build();

            foreach (var containerId in _probeContainerIds)
            {
                try
                {
                    await client.Containers.RemoveContainerAsync(
                        containerId,
                        new ContainerRemoveParameters { Force = true });
                }
                catch (Exception)
                {
                    // 清理失败不影响测试结论
                }
            }

            if (_probeImageCreated)
            {
                try
                {
                    await client.Images.DeleteImageAsync(
                        _probeImage,
                        new ImageDeleteParameters { Force = true });
                }
                catch (Exception)
                {
                    // 清理失败不影响测试结论
                }
            }
        }
        catch (Exception)
        {
            // 守护进程不可用时也走到这里：清理不是测试断言的一部分
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task 生成容器定义时容器标识为空_返回1000()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/run-command",
            new { containerId = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 生成容器定义时格式不支持_返回1000()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/run-command",
            new { containerId = "whatever", format = "kubernetes" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 生成容器定义时容器不存在_返回1401()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/run-command",
            new { containerId = Guid.NewGuid().ToString("N") });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerFact]
    public async Task 生成docker_run_命令_含镜像引用与容器名()
    {
        var containerId = await this.CreateProbeContainerAsync("whalefleet-test-def-run");
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/run-command",
            new { containerId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await ReadDataAsync<RunSpecPayload>(response);

        payload.Format.Should().Be("dockerRun");
        payload.Content.Should().StartWith("docker run ");

        // 必须是可读的镜像引用而不是 sha256 ID：ID 写进命令虽能跑，但换台机器就没有了
        payload.Content.Should().Contain(_probeImage);
        payload.Content.Should().NotContain("sha256:");
        payload.Content.Should().Contain("--name whalefleet-test-def-run");
    }

    [DockerFact]
    public async Task 生成compose片段_含services与image()
    {
        var containerId = await this.CreateProbeContainerAsync("whalefleet-test-def-compose");
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/run-command",
            new { containerId, format = "composeYaml" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await ReadDataAsync<RunSpecPayload>(response);

        payload.Format.Should().Be("composeYaml");
        payload.Content.Should().StartWith("services:");
        payload.Content.Should().Contain($"image: {_probeImage}");
        payload.Content.Should().Contain("container_name:");
    }

    [Fact]
    public async Task 导出未受管的编排项目_返回1903()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/export",
            new { name = "not-deployed-by-us" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ComposeNotManaged);
    }

    [Fact]
    public async Task 导出编排项目_原样返回存下的YAML()
    {
        const string name = "shop";
        const string yaml = "services:\n  web:\n    image: nginx:1.27\n";

        await RecordComposeAsync(name, yaml);

        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/export",
            new { name });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await ReadDataAsync<ComposeExportPayload>(response);

        payload.Name.Should().Be(name);
        payload.Content.TrimEnd().Should().Be(
            yaml.TrimEnd(),
            "导出的是部署时存下的原文，不该被重新生成（重新生成会丢注释与键序）");
    }

    [Fact]
    public async Task 导出编排项目时名称为空_返回1000()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/composes/export",
            new { name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// 往仓储里写一条「本平台部署过」的编排记录（不真的部署）。
    /// </summary>
    /// <param name="name">项目名称。</param>
    /// <param name="content">YAML 原文。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task RecordComposeAsync(string name, string content)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IComposeProjectRepository>();
        var hosts = scope.ServiceProvider.GetRequiredService<IHostRepository>();

        var host = await hosts.FindByNameAsync(_localHost);

        host.Should().NotBeNull();

        await repository.AddAsync(ComposeProject.Create(Guid.NewGuid(), host!.Id, name, content));
    }

    /// <summary>
    /// 在指定宿主里建一个带管理员角色的账号。
    /// </summary>
    /// <param name="factory">测试宿主工厂。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task CreateAdminUserAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var assignments = scope.ServiceProvider.GetRequiredService<IUserRoleAssignmentRepository>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync(Role.AdminName);

        adminRole.Should().NotBeNull("内置角色应由迁移种入");

        await TestAccounts.CreateUserAsync(users, assignments, _adminName, hasher.Hash(_password), adminRole);
    }

    /// <summary>
    /// 确保探针镜像存在（几 KB 的空镜像；用完即删）。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    private async Task EnsureProbeImageAsync()
    {
        if (_probeImageCreated)
        {
            return;
        }

        using var client = new DockerClientBuilder()
            .WithEndpoint(new Uri("unix:///var/run/docker.sock"))
            .Build();

        using var tar = new MemoryStream();

        using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, leaveOpen: true))
        {
            await writer.WriteEntryAsync(new UstarTarEntry(TarEntryType.RegularFile, "probe.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("probe\n")),
            });
        }

        tar.Position = 0;

        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromSrc = "-", Repo = "whalefleet-test-definition-probe", Tag = "latest" },
            tar,
            null,
            new SyncCollector(),
            CancellationToken.None);

        _probeImageCreated = true;
    }

    /// <summary>
    /// 造一个探针容器（<b>不启动</b>——inspect 对已创建的容器同样可用）。
    /// </summary>
    /// <param name="name">容器名。</param>
    /// <returns>容器 ID。</returns>
    private async Task<string> CreateProbeContainerAsync(string name)
    {
        await this.EnsureProbeImageAsync();

        using var client = new DockerClientBuilder()
            .WithEndpoint(new Uri("unix:///var/run/docker.sock"))
            .Build();

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = _probeImage,
            Cmd = ["/nonexistent"],
            Name = $"{name}-{Guid.NewGuid().ToString("N")[..8]}",
        });

        _probeContainerIds.Add(created.ID);

        return created.ID;
    }

    /// <summary>
    /// 在本次测试的宿主上创建一个已认证的客户端。
    /// </summary>
    /// <returns>带 Bearer 头的客户端。</returns>
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

    /// <summary>
    /// 读取统一响应里的错误码。
    /// </summary>
    /// <param name="response">HTTP 响应。</param>
    /// <returns>错误码。</returns>
    private static async Task<int> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("code").GetInt32();
    }

    /// <summary>
    /// 读取统一响应里的 <c>data</c> 并反序列化。
    /// </summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="response">HTTP 响应。</param>
    /// <returns>反序列化结果。</returns>
    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(_jsonOptions);

        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();

        return envelope.Data!;
    }

    /// <summary>
    /// 统一响应信封。
    /// </summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="Success">是否成功。</param>
    /// <param name="Code">业务码。</param>
    /// <param name="Message">提示文案。</param>
    /// <param name="Data">数据。</param>
    private sealed record Envelope<T>(bool Success, int Code, string? Message, T? Data);

    /// <summary>
    /// 登录响应的投影。
    /// </summary>
    /// <param name="AccessToken">访问令牌。</param>
    private sealed record LoginPayload(string AccessToken);

    /// <summary>
    /// 丢弃进度的镜像拉取收集器（<c>docker import</c> 会回调，但我们不关心）。
    /// </summary>
    private sealed class SyncCollector : IProgress<JSONMessage>
    {
        /// <inheritdoc />
        public void Report(JSONMessage value)
        {
        }
    }

    /// <summary>
    /// 容器定义的响应投影。
    /// </summary>
    /// <param name="Format">实际格式。</param>
    /// <param name="Content">生成内容。</param>
    private sealed record RunSpecPayload(string Format, string Content);

    /// <summary>
    /// 编排导出的响应投影。
    /// </summary>
    /// <param name="Name">项目名称。</param>
    /// <param name="Content">YAML 原文。</param>
    private sealed record ComposeExportPayload(string Name, string Content);
}
