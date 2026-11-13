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
/// 容器 / 卷文件浏览端点（0.14.0）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>卷浏览真的会用辅助容器</b>：因此这里把 <c>Files:HelperImage</c> 指向测试自造的
/// 空镜像——它没有任何可执行文件，正好证明「辅助容器不需要启动」。它也确实<b>没有</b>被启动：
/// 若哪天实现改成「必须启动辅助容器」，这条用例会以一个含义明确的失败告诉后来者。</para>
/// <para><b>镜像与卷都在用例内自造、用完即删</b>：不动宿主机上别人的东西。</para>
/// </remarks>
#pragma warning disable CA1001
public class FileBrowseEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _probeImage = "whalefleet-test-filebrowse:latest";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private ApiFactory _factory = null!;
    private bool _imageCreated;
    private readonly List<string> _containerIds = [];
    private readonly List<string> _volumeNames = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        // 辅助镜像必须先存在：卷浏览会用它建一次性容器。
        // 而配置也必须在宿主构建之前叠加，因此这里先造镜像、再设配置、最后建账号。
        await this.EnsureProbeImageAsync();

        _factory = new ApiFactory()
            .WithSetting("Files:HelperImage", _probeImage)
            .WithSetting("Files:TempDirectory", Path.Combine(Path.GetTempPath(), "whalefleet-tests", "exports"));

        await CreateAdminUserAsync(_factory);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        try
        {
            using var client = CreateDockerClient();

            foreach (var containerId in _containerIds)
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

            foreach (var volumeName in _volumeNames)
            {
                try
                {
                    await client.Volumes.RemoveAsync(volumeName);
                }
                catch (Exception)
                {
                    // 同上
                }
            }

            if (_imageCreated)
            {
                try
                {
                    await client.Images.DeleteImageAsync(
                        _probeImage,
                        new ImageDeleteParameters { Force = true });
                }
                catch (Exception)
                {
                    // 同上
                }
            }
        }
        catch (Exception)
        {
            // 守护进程不可用时也走到这里
        }

        await _factory.DisposeAsync();
    }

    [DockerFact]
    public async Task 列举容器根目录_能看到镜像里的文件()
    {
        var containerId = await this.CreateContainerAsync();

        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/files/list",
            new { containerId, path = "/" });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "响应体：{0}",
            await response.Content.ReadAsStringAsync());

        var listing = await ReadDataAsync<ListingPayload>(response);

        listing.Path.Should().Be("/");
        listing.Entries.Should().Contain(entry => entry.Name == "probe.txt");
    }

    [DockerFact]
    public async Task 下载容器内的文件_内容一致()
    {
        var containerId = await this.CreateContainerAsync();

        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/files/download",
            new { containerId, path = "/probe.txt" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();

        content.Should().Be("probe\n");
    }

    [DockerFact]
    public async Task 写入容器文件_能被读回()
    {
        var containerId = await this.CreateContainerAsync();

        using var client = await this.CreateAuthenticatedClientAsync();

        using var form = new MultipartFormDataContent
        {
            { new StringContent(containerId), "containerId" },
            { new StringContent("/"), "directoryPath" },
        };

        var payload = new ByteArrayContent(Encoding.UTF8.GetBytes("uploaded-content"));
        payload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(payload, "file", "uploaded.txt");

        using var write = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/containers/files/write",
            form);

        write.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "响应体：{0}",
            await write.Content.ReadAsStringAsync());

        using var download = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/files/download",
            new { containerId, path = "/uploaded.txt" });

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync()).Should().Be("uploaded-content");
    }

    [DockerFact]
    public async Task 卷文件浏览_借助辅助容器完成读写()
    {
        var volumeName = await this.CreateVolumeAsync();

        using var client = await this.CreateAuthenticatedClientAsync();

        // 1) 写入一个文件（会建一个 rw 的辅助容器）
        using var form = new MultipartFormDataContent
        {
            { new StringContent(volumeName), "volumeName" },
            { new StringContent("/"), "directoryPath" },
        };

        var payload = new ByteArrayContent(Encoding.UTF8.GetBytes("volume-file-content"));
        payload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(payload, "file", "inside.txt");

        using var write = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/volumes/files/write",
            form);

        write.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "响应体：{0}",
            await write.Content.ReadAsStringAsync());

        // 2) 列举（ro 的辅助容器）
        using var list = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/files/list",
            new { volumeName, path = "/" });

        list.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "响应体：{0}",
            await list.Content.ReadAsStringAsync());

        var listing = await ReadDataAsync<ListingPayload>(list);

        listing.Entries.Should().Contain(entry => entry.Name == "inside.txt");
        listing.Entries.Single(entry => entry.Name == "inside.txt").Path.Should().Be("/inside.txt");

        // 3) 下载
        using var download = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/files/download",
            new { volumeName, path = "/inside.txt" });

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync()).Should().Be("volume-file-content");

        // 4) 辅助容器都被清掉了（不留孤儿）
        using var docker = CreateDockerClient();
        var leftovers = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
            {
                ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                {
                    [$"{Whalefleet.Application.Files.VolumeFileService.RoleLabel}={Whalefleet.Application.Files.VolumeFileService.RoleValue}"] = true,
                },
            },
        });

        leftovers.Should().BeEmpty("辅助容器用完必须删掉，不能留下孤儿");
    }

    [Fact]
    public async Task 路径含上级引用_返回1405()
    {
        using var client = await this.CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/files/list",
            new { containerId = "whatever", path = "/../../etc" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.InvalidFilePath);
    }

    [Fact]
    public async Task 文件写入被禁用_返回1409()
    {
        var factory = new ApiFactory()
            .WithSetting("Files:AllowWrite", "false")
            .WithSetting("Files:TempDirectory", Path.Combine(Path.GetTempPath(), "whalefleet-tests", "exports"));

        await CreateAdminUserAsync(factory);

        using var client = factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

        var data = await ReadDataAsync<LoginPayload>(login);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.AccessToken);

        using var form = new MultipartFormDataContent
        {
            { new StringContent("whatever"), "containerId" },
            { new StringContent("/"), "directoryPath" },
        };

        var payload = new ByteArrayContent([1, 2, 3]);
        payload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(payload, "file", "x.txt");

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/containers/files/write",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.FileWriteDisabled);

        factory.Dispose();
    }

    /// <summary>
    /// 造一个只含 <c>probe.txt</c> 的空镜像。
    /// </summary>
    /// <returns>表示异步操作的任务。</returns>
    private async Task EnsureProbeImageAsync()
    {
        if (_imageCreated)
        {
            return;
        }

        using var client = CreateDockerClient();

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
            new ImagesCreateParameters
            {
                FromSrc = "-",
                Repo = "whalefleet-test-filebrowse",
                Tag = "latest",
            },
            tar,
            null,
            new NoOpProgress(),
            CancellationToken.None);

        _imageCreated = true;
    }

    /// <summary>
    /// 建一个探针容器（<b>不启动</b>）。
    /// </summary>
    /// <returns>容器 ID。</returns>
    private async Task<string> CreateContainerAsync()
    {
        using var client = CreateDockerClient();

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = _probeImage,
            Cmd = ["/nonexistent"],
            Name = $"whalefleet-test-files-{Guid.NewGuid().ToString("N")[..12]}",
        });

        _containerIds.Add(created.ID);

        return created.ID;
    }

    /// <summary>
    /// 建一个测试卷。
    /// </summary>
    /// <returns>卷名。</returns>
    private async Task<string> CreateVolumeAsync()
    {
        using var client = CreateDockerClient();

        var name = $"whalefleet-test-vol-{Guid.NewGuid().ToString("N")[..12]}";

        await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = name });

        _volumeNames.Add(name);

        return name;
    }

    /// <summary>
    /// 建一个已认证的客户端。
    /// </summary>
    /// <returns>带 Bearer 头的客户端。</returns>
    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

        var data = await ReadDataAsync<LoginPayload>(login);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.AccessToken);

        return client;
    }

    /// <summary>
    /// 建 Docker 客户端。
    /// </summary>
    /// <returns>Docker 客户端。</returns>
    private static DockerClient CreateDockerClient() =>
        new DockerClientBuilder()
            .WithEndpoint(new Uri("unix:///var/run/docker.sock"))
            .Build();

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

        await TestAccounts.CreateUserAsync(
            users,
            assignments,
            _adminName,
            hasher.Hash(_password),
            adminRole);
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

    /// <summary>登录结果。</summary>
    /// <param name="AccessToken">访问令牌。</param>
    private sealed record LoginPayload(string AccessToken);

    /// <summary>目录清单。</summary>
    /// <param name="Path">当前路径。</param>
    /// <param name="ParentPath">父目录路径。</param>
    /// <param name="Entries">条目。</param>
    /// <param name="Truncated">是否被截断。</param>
    private sealed record ListingPayload(
        string Path,
        string? ParentPath,
        IReadOnlyList<FileEntryPayload> Entries,
        bool Truncated);

    /// <summary>目录条目。</summary>
    /// <param name="Name">名称。</param>
    /// <param name="Path">完整路径。</param>
    /// <param name="Kind">类型。</param>
    /// <param name="SizeBytes">大小。</param>
    /// <param name="LinkTarget">链接目标。</param>
    /// <param name="Mode">权限位。</param>
    private sealed record FileEntryPayload(
        string Name,
        string Path,
        string Kind,
        long SizeBytes,
        string? LinkTarget,
        string? Mode);

    /// <summary>忽略进度消息。</summary>
    private sealed class NoOpProgress : IProgress<JSONMessage>
    {
        /// <inheritdoc />
        public void Report(JSONMessage value)
        {
        }
    }
}
