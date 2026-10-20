using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Abstractions;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Users;
using Whalefleet.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 镜像推送到私有仓库 的<b>成功路径</b>测试。
/// </summary>
/// <remarks>
/// <para><b>这是 0.4.0 的验收标准</b>：「配置一个私有仓库 后能 push 成功」。
/// 因此本组用例必须真的把镜像推出去、并且真的把它拉回来——
/// 只断言「接口返回 200」是不够的：那可能只是「请求没被受理」。</para>
/// <para><b>为什么单独一个文件</b>：本组用例有前置条件（本机跑着 仓库），
/// 与其它镜像用例的前置条件（宿主 Docker 可用）不同，混在一起会让「跳过原因」变得含糊。</para>
/// <para><b>不需要认证</b>：<c>registry:2</c> 默认接受匿名写入，
/// 因此本组覆盖的是「地址识别 + 打标签 + 推送」这条主路径。
/// 带凭据的路径由 <c>RepositoryEndpointsTests</c> 覆盖到「凭据被正确加密存储」这一层。</para>
/// </remarks>
#pragma warning disable CA1001
public class ImagePushToRepositoryTests : IAsyncLifetime
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

    /// <summary>
    /// 推镜像到本机 仓库，然后从 Registry <b>拉回来</b>验证产物确实到位。
    /// </summary>
    [DockerRegistryServerFact]
    public async Task 推送_到本机Registry_成功且产物能拉回来()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var repository = $"whalefleet-test/{Guid.NewGuid():N}";
        var reference = $"{DockerAvailability.DefaultRegistryEndpoint}/{repository}:v1";

        try
        {
            // 先构建一个带本地标签的镜像
            var build = await BuildAsync(client, reference);

            build.ImageId.Should().StartWith("sha256:");
            build.Reference.Should().Be(reference);
            build.RepoTags.Should().Contain(reference);

            // 推出去
            using var pushResponse = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/push",
                new { image = reference });

            pushResponse.StatusCode.Should().Be(HttpStatusCode.OK, await ReadBodyAsync(pushResponse));

            var pushed = await ReadDataAsync<PushPayload>(pushResponse);

            pushed.ImageId.Should().StartWith("sha256:");
            pushed.Reference.Should().Be(reference);

            // 目标地址必须是本机 Registry：推到了别处就毫无意义
            pushed.RepositoryServerAddress.Should().Be(DockerAvailability.DefaultRegistryEndpoint);

            // 产物确实在 Registry 里：把标签删掉再从 Registry 拉回来。
            // 若推送其实没成功，拉取会失败。
            await RemoveImageAsync(client, reference);

            using var pullResponse = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/pull",
                new
                {
                    image = $"{DockerAvailability.DefaultRegistryEndpoint}/{repository}",
                    tag = "v1",
                });

            pullResponse.StatusCode.Should().Be(HttpStatusCode.OK, await ReadBodyAsync(pullResponse));

            var pulled = await ReadDataAsync<PullPayload>(pullResponse);

            pulled.ImageId.Should().Be(pushed.ImageId, "拉回来的应是刚推上去的那个镜像");
            pulled.Reference.Should().Be(reference);
        }
        finally
        {
            await RemoveImageAsync(client, reference);
        }
    }

    /// <summary>
    /// 推成功后，<b>本地标签仍然保留</b>——推不是删。
    /// </summary>
    [DockerRegistryServerFact]
    public async Task 推送_成功后本地镜像仍在()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var repository = $"whalefleet-test/{Guid.NewGuid():N}";
        var reference = $"{DockerAvailability.DefaultRegistryEndpoint}/{repository}:v1";

        try
        {
            await BuildAsync(client, reference);

            using var pushResponse = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/push",
                new { image = reference });

            pushResponse.StatusCode.Should().Be(HttpStatusCode.OK, await ReadBodyAsync(pushResponse));

            // 推送不应动本地镜像
            using var inspect = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/inspect",
                new { imageId = reference });

            inspect.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await RemoveImageAsync(client, reference);
        }
    }

    // ===== 辅助 =====

    private static async Task<BuildPayload> BuildAsync(HttpClient client, string reference)
    {
        using var form = new MultipartFormDataContent();

        var tar = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(tar, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            // COPY 一个真实文件：只有 FROM scratch + LABEL 的镜像层数为 0，
            // 而宿主的「本机镜像」探测会取任意有标签的镜像，
            // 另一些用例断言它的 LayerCount > 0——0 层镜像会让那些用例莫名失败。
            writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(
                System.Formats.Tar.TarEntryType.RegularFile,
                "Dockerfile")
            {
                DataStream = new MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes(
                        "FROM scratch\nCOPY payload.txt /payload.txt\nLABEL purpose=push-to-registry\n")),
            });

            writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(
                System.Formats.Tar.TarEntryType.RegularFile,
                "payload.txt")
            {
                DataStream = new MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes("whalefleet push test payload\n")),
            });
        }

        var content = new ByteArrayContent(tar.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");

        // ⚠️ 必须带 filename：ASP.NET Core 靠它区分「文件」与「普通表单字段」
        form.Add(content, "context", "context.tar");
        form.Add(new StringContent(reference), "tag");

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/build",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await ReadBodyAsync(response));

        return await ReadDataAsync<BuildPayload>(response);
    }

    private static async Task RemoveImageAsync(HttpClient client, string imageId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/remove",
            new { imageId, force = true });

        _ = response;
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

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed record LoginPayload(string AccessToken, string RefreshToken);

    private sealed record BuildPayload(
        string ImageId,
        string Reference,
        long Size,
        List<string> RepoTags,
        List<string> BuildLog);

    private sealed record PushPayload(
        string ImageId,
        string Reference,
        string RepositoryServerAddress,
        bool Authenticated,
        List<string> PushLog);

    private sealed record PullPayload(
        string ImageId,
        string Reference,
        long Size,
        List<string> RepoTags);
}
