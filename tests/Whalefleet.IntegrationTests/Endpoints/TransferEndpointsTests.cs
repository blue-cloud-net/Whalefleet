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
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;

using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 导入导出端点（0.14.0）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>为什么自造镜像而不复用宿主上已有的</b>：导出测试要验证「导出的 tar 能 load 回来」，
/// 而宿主上已有的镜像可能是别人的、体积几个 GB——导一次要好几分钟，
/// 还会在别人的镜像库里留下一堆临时产物。因此这里用 <c>docker import</c> 造一个
/// <b>几 KB 的空镜像</b>，跑完立刻删掉。</para>
/// <para><b>为什么导出走「轮询状态」而不是等推送</b>：SignalR 只是提示，
/// 权威状态在 <c>export/status</c>。测试走轮询因此不依赖实时通道。</para>
/// </remarks>
#pragma warning disable CA1001
public class TransferEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _probeImage = "whalefleet-test-transfer-probe:latest";
    private const string _probeImageId = "whalefleet-test-transfer-probe-id";

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

        // ⚠️ 配置必须在**宿主构建之前**叠加：而访问 factory.Services 就会构建宿主。
        // 因此这里先设配置再建账号——顺序反了的话，导出目录会回落到默认的 data/exports
        // （相对工作目录），也就是**仓库里**，而且多个测试宿主会互相删对方的产物。
        _factory.WithSetting("Files:TempDirectory", Path.Combine(Path.GetTempPath(), "whalefleet-tests", "exports"));

        await CreateAdminUserAsync(_factory);
    }

    /// <summary>
    /// 在指定宿主里建一个带管理员角色的账号。
    /// </summary>
    /// <param name="factory">测试宿主工厂。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>为什么每个宿主都要建一遍</b>：每个 <see cref="ApiFactory"/> 有自己独立的数据库，
    /// 因此账号不会跨宿主共享——需要第二个宿主的用例（比如改配置的那些）
    /// 必须自己把账号建起来，否则登录会 401。
    /// </remarks>
    private static async Task CreateAdminUserAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
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
    public async Task DisposeAsync()
    {
        try
        {
            using var client = new DockerClientBuilder()
                .WithEndpoint(new Uri("unix:///var/run/docker.sock"))
                .Build();

            // 先删容器再删镜像：镜像被容器占着时强制删除也只是不完整的行为，
            // 而且留着容器会让下一次跑测试看到一堆同名的东西。
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
    public async Task 查询不存在的导出任务_返回1410()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/export/status",
            new { jobId = "no-such-job" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.TransferJobNotFound);
    }

    [Fact]
    public async Task 无效的下载票据_返回1408()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/v1/hosts/{_localHost}/images/download?token=not-a-real-ticket");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DownloadTicketInvalid);
    }

    [Fact]
    public async Task 导出时镜像标识为空_返回1000()
    {
        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/export",
            new { resourceId = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 导入超过上限的上传_返回1407()
    {
        // 上限压到 1 字节：任何非空请求体都超限
        var limited = new ApiFactory()
            .WithSetting("Files:MaxUploadBytes", "1")
            .WithSetting("Files:TempDirectory", Path.Combine(Path.GetTempPath(), "whalefleet-tests", "exports"))
            .WithSetting("Jwt:Secret", ApiFactory.TestJwtSecret);

        await CreateAdminUserAsync(limited);

        using var limitedClient = await CreateAuthenticatedClientAsync(limited, _adminName);

        using var content = new ByteArrayContent([1, 2, 3, 4, 5]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await limitedClient.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/import",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.UploadTooLarge);

        limited.Dispose();
    }

    [DockerFact]
    public async Task 导出镜像_下载到有效tar且能再导入()
    {
        await this.EnsureProbeImageAsync();

        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        // 1) 发起导出
        using var exportResponse = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/export",
            new { resourceId = _probeImage });

        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var started = await ReadDataAsync<ExportPayload>(exportResponse);
        started.JobId.Should().NotBeNullOrWhiteSpace();

        // 2) 轮询直到完成
        var status = await WaitForExportAsync(client, started.JobId!);

        status.Status.Should().Be("succeeded", $"导出失败：{status.ErrorMessage}");
        status.DownloadUrl.Should().NotBeNullOrWhiteSpace();
        status.SizeBytes.Should().BeGreaterThan(0);

        // 3) 凭票据下载（**匿名**，不带 Authorization 头）
        using var downloadClient = _factory.CreateClient();

        using var download = await downloadClient.GetAsync(status.DownloadUrl!);

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("application/x-tar");

        var tarBytes = await download.Content.ReadAsByteArrayAsync();

        // 4) 下载到的确实是 tar，且里面有 manifest.json
        using var memory = new MemoryStream(tarBytes);
        using var reader = new TarReader(memory);
        TarEntry? entry;
        var manifest = String.Empty;

        while ((entry = await reader.GetNextEntryAsync()) is not null)
        {
            if (entry.Name == "manifest.json")
            {
                manifest = await new StreamReader(entry.DataStream!).ReadToEndAsync();
            }
        }

        manifest.Should().Contain("RepoTags");
        manifest.Should().Contain(_probeImage);

        // 5) 把它再导入回去（验证 tar 确实是可 load 的）
        using var importContent = new ByteArrayContent(tarBytes);
        importContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var importResponse = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/import",
            importContent);

        importResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "导入响应体：{0}",
            await importResponse.Content.ReadAsStringAsync());
    }

    [DockerFact]
    public async Task 导出同一个镜像两次_复用同一个任务()
    {
        await this.EnsureProbeImageAsync();

        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var first = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/export",
            new { resourceId = _probeImage });

        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var firstPayload = await ReadDataAsync<ExportPayload>(first);

        using var second = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/export",
            new { resourceId = _probeImage });

        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var secondPayload = await ReadDataAsync<ExportPayload>(second);

        secondPayload.JobId.Should().NotBeNull();

        if (secondPayload.Reused)
        {
            secondPayload.JobId.Should().Be(firstPayload.JobId, "同一资源的导出应当复用进行中的任务");
        }
        else
        {
            // 探针镜像是几 KB 的空镜像，第一次导出在机器空闲时可能**已经跑完**——
            // 那时第二次启动新任务是正确行为，硬要求「一定复用」会让这条用例
            // 随机器负载时绿时红（实测全量跑时偶发失败）。
            // 因此这里只守住「未复用时确实拿到的是另一个任务」。
            secondPayload.JobId.Should().NotBe(firstPayload.JobId);
        }

        // 等两个任务都结束，避免留下悬着的导出任务
        await WaitForExportAsync(client, firstPayload.JobId!);
        await WaitForExportAsync(client, secondPayload.JobId!);
    }

    /// <summary>
    /// 轮询容器导出任务直到它结束。
    /// </summary>
    /// <param name="client">已认证的客户端。</param>
    /// <param name="jobId">任务标识。</param>
    /// <returns>最终的任务状态。</returns>
    private static async Task<StatusPayload> WaitForContainerExportAsync(HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/containers/export/status",
                new { jobId });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var status = await ReadDataAsync<StatusPayload>(response);

            if (status.Status is "succeeded" or "failed")
            {
                return status;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"导出任务 {jobId} 在两分钟内没有结束。");
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
            new ImagesCreateParameters { FromSrc = "-", Repo = "whalefleet-test-transfer-probe", Tag = "latest" },
            tar,
            null,
            new SyncCollector(),
            CancellationToken.None);

        _probeImageCreated = true;
    }

    /// <summary>
    /// 造一个探针容器（<b>不启动</b>——导出与提交对已创建的容器同样可用）。
    /// </summary>
    /// <returns>容器 ID。</returns>
    private async Task<string> CreateProbeContainerAsync()
    {
        await this.EnsureProbeImageAsync();

        using var client = new DockerClientBuilder()
            .WithEndpoint(new Uri("unix:///var/run/docker.sock"))
            .Build();

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = _probeImage,
            Cmd = ["/nonexistent"],
            Name = $"whalefleet-test-transfer-{Guid.NewGuid().ToString("N")[..12]}",
        });

        _probeContainerIds.Add(created.ID);

        return created.ID;
    }

    [DockerFact]
    public async Task 导出容器_下载到tar()
    {
        var containerId = await this.CreateProbeContainerAsync();

        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var exportResponse = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/export",
            new { resourceId = containerId });

        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var started = await ReadDataAsync<ExportPayload>(exportResponse);

        var status = await WaitForContainerExportAsync(client, started.JobId!);

        status.Status.Should().Be("succeeded", $"导出失败：{status.ErrorMessage}");
        status.DownloadUrl.Should().NotBeNullOrWhiteSpace();

        using var downloadClient = _factory.CreateClient();

        using var download = await downloadClient.GetAsync(status.DownloadUrl!);

        download.StatusCode.Should().Be(HttpStatusCode.OK);

        var tarBytes = await download.Content.ReadAsByteArrayAsync();

        tarBytes.Should().NotBeEmpty();

        // 产物是合法的 tar：能读出条目
        using var memory = new MemoryStream(tarBytes);
        using var reader = new TarReader(memory);
        var names = new List<string>();

        TarEntry? entry;

        while ((entry = await reader.GetNextEntryAsync()) is not null)
        {
            names.Add(entry.Name);
        }

        names.Should().Contain("probe.txt", "导出产物里应当有容器文件系统中的文件");
    }

    [DockerFact]
    public async Task 提交容器为镜像_产出带标签的镜像()
    {
        var containerId = await this.CreateProbeContainerAsync();

        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/commit",
            new
            {
                containerId,
                repository = "whalefleet-test-commit",
                tag = "v1",
                comment = "集成测试产物",
                pause = false,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await ReadDataAsync<CommitPayload>(response);

        result.ImageId.Should().NotBeNullOrWhiteSpace();
        result.Repository.Should().Be("whalefleet-test-commit");

        // 产出的镜像真的在本地，且带上了我们给的标签
        using var docker = new DockerClientBuilder()
            .WithEndpoint(new Uri("unix:///var/run/docker.sock"))
            .Build();

        var image = await docker.Images.InspectImageAsync("whalefleet-test-commit:v1");

        image.RepoTags.Should().Contain("whalefleet-test-commit:v1");

        await docker.Images.DeleteImageAsync(
            "whalefleet-test-commit:v1",
            new ImageDeleteParameters { Force = true });
    }

    [DockerFact]
    public async Task 导入容器文件系统_产出镜像()
    {
        // 先导出一个容器，拿到 tar
        var containerId = await this.CreateProbeContainerAsync();

        using var client = await this.CreateAuthenticatedClientAsync(_adminName);

        using var exportResponse = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/export",
            new { resourceId = containerId });

        var started = await ReadDataAsync<ExportPayload>(exportResponse);
        var status = await WaitForContainerExportAsync(client, started.JobId!);

        status.Status.Should().Be("succeeded");

        using var downloadClient = _factory.CreateClient();

        using var download = await downloadClient.GetAsync(status.DownloadUrl!);

        var tarBytes = await download.Content.ReadAsByteArrayAsync();

        // 再把它导入成一个新镜像
        using var importContent = new ByteArrayContent(tarBytes);
        importContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var importResponse = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/containers/import?repository=whalefleet-test-import&tag=v1",
            importContent);

        importResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "导入响应体：{0}",
            await importResponse.Content.ReadAsStringAsync());

        using var docker = new DockerClientBuilder()
            .WithEndpoint(new Uri("unix:///var/run/docker.sock"))
            .Build();

        var image = await docker.Images.InspectImageAsync("whalefleet-test-import:v1");

        image.ID.Should().NotBeNullOrWhiteSpace();

        await docker.Images.DeleteImageAsync(
            "whalefleet-test-import:v1",
            new ImageDeleteParameters { Force = true });
    }

    /// <summary>
    /// 轮询导出任务直到它结束。
    /// </summary>
    /// <param name="client">已认证的客户端。</param>
    /// <param name="jobId">任务标识。</param>
    /// <returns>最终的任务状态。</returns>
    private static async Task<StatusPayload> WaitForExportAsync(HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/export/status",
                new { jobId });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var status = await ReadDataAsync<StatusPayload>(response);

            if (status.Status is "succeeded" or "failed")
            {
                return status;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"导出任务 {jobId} 在两分钟内没有结束。");
    }

    /// <summary>
    /// 创建一个已认证的客户端。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <returns>带 Bearer 头的客户端。</returns>
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

    /// <summary>
    /// 在给定的工厂上创建一个已认证的客户端。
    /// </summary>
    /// <param name="factory">测试宿主工厂。</param>
    /// <param name="username">用户名。</param>
    /// <returns>带 Bearer 头的客户端。</returns>
    private static async Task<HttpClient> CreateAuthenticatedClientAsync(ApiFactory factory, string username)
    {
        var client = factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username, password = _password });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = await ReadDataAsync<LoginPayload>(login);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.AccessToken);

        return client;
    }

    /// <summary>
    /// 读取响应里的 <c>data</c>。
    /// </summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="response">响应。</param>
    /// <returns>数据主体。</returns>
    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue();

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    /// <summary>
    /// 读取响应里的业务错误码。
    /// </summary>
    /// <param name="response">响应。</param>
    /// <returns>错误码。</returns>
    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    /// <summary>
    /// 读取响应体 JSON。
    /// </summary>
    /// <param name="response">响应。</param>
    /// <returns>JSON 根节点。</returns>
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonElement.Parse(json);
    }

    /// <summary>
    /// 登录结果。
    /// </summary>
    /// <param name="AccessToken">访问令牌。</param>
    private sealed record LoginPayload(string AccessToken);

    /// <summary>
    /// 发起导出的响应。
    /// </summary>
    /// <param name="JobId">任务标识。</param>
    /// <param name="Status">任务状态。</param>
    /// <param name="Reused">是否复用了进行中的任务。</param>
    private sealed record ExportPayload(string JobId, string Status, bool Reused);

    /// <summary>
    /// 提交容器为镜像的响应。
    /// </summary>
    /// <param name="ImageId">新镜像 ID。</param>
    /// <param name="Repository">镜像仓库名。</param>
    /// <param name="Tag">镜像标签。</param>
    private sealed record CommitPayload(string ImageId, string Repository, string Tag);

    /// <summary>
    /// 导出任务状态。
    /// </summary>
    /// <param name="Status">任务状态。</param>
    /// <param name="DownloadUrl">下载地址。</param>
    /// <param name="SizeBytes">产物大小。</param>
    /// <param name="ErrorMessage">失败原因。</param>
    private sealed record StatusPayload(
        string Status,
        string? DownloadUrl,
        long? SizeBytes,
        string? ErrorMessage);

    /// <summary>
    /// 同步收集 Docker 的 JSON 消息（顺序不能乱）。
    /// </summary>
    private sealed class SyncCollector : IProgress<JSONMessage>
    {
        /// <inheritdoc />
        public void Report(JSONMessage value)
        {
        }
    }
}
