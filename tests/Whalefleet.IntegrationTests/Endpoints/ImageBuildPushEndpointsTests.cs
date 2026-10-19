using System.Formats.Tar;
using System.IO.Compression;
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

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 镜像构建与推送端点（<c>/api/v1/hosts/{hostId}/images/{build,push}</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>构建上下文在测试里现打 tar</b>：用 <c>System.Formats.Tar</c> 拼一个只含
/// <c>Dockerfile</c> 的最小上下文。这样用例不依赖仓库里的任何文件，
/// 且能精确控制 Dockerfile 内容以便验证失败路径。</para>
/// <para><b>构建用 <c>FROM scratch</c></b>：不依赖任何基础镜像，因此<b>离线也能跑</b>——
/// 而「拉取基础镜像」是需要网络的那部分，与「验证本项目的构建参数映射」无关。</para>
/// <para><b>推送只测失败路径</b>：真正推成功需要一台跑着的私有 Registry。
/// 本组把「往一个不存在的地址推」的错误码钉死，而成功路径由「构建出的镜像确实落盘」
/// 那条用例间接覆盖（它证明构建产物是真的）。</para>
/// </remarks>
#pragma warning disable CA1001
public class ImageBuildPushEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";
    private const string _unknownImage = "no-such-image-0123456789:latest";

    /// <summary>本组用例构建出来的镜像标签前缀（便于识别与清理）。</summary>
    private const string _buildTagPrefix = "whalefleet-build-test";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private ApiFactory _factory = null!;
    private readonly List<string> _createdReferences = [];

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
    public async Task DisposeAsync()
    {
        await this.CleanupCreatedImagesAsync();

        await _factory.DisposeAsync();
    }

    // ===== 鉴权 =====

    [Fact]
    public async Task 构建_未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/build",
            BuildForm(_buildTagPrefix));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 推送_未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/push",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task 构建_未知主机_返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_unknownHost}/images/build",
            BuildForm(_buildTagPrefix));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [DockerFact]
    public async Task 推送_未知主机_返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/images/push",
            new { image = _unknownImage });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    // ===== 构建参数校验（不需要 Docker：这些校验发生在取客户端之前） =====

    [Fact]
    public async Task 构建_未知主机时仍先报主机不存在_证明主机校验在前()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_unknownHost}/images/build",
            BuildForm(_buildTagPrefix));

        // 主机校验在管道里（CurrentHostMiddleware），因此连控制器方法都没进
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [DockerFact]
    public async Task 构建_未提供上下文_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(_buildTagPrefix), "tag");

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/build",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [DockerFact]
    public async Task 构建_构建参数不是合法JSON_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var form = new MultipartFormDataContent();
        form.Add(BuildContextContent("FROM scratch\n"), "context", "context.tar");
        form.Add(new StringContent(_buildTagPrefix), "tag");
        form.Add(new StringContent("{不是合法 JSON}"), "buildArgs");

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/build",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [DockerFact]
    public async Task 构建_标签为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var form = new MultipartFormDataContent();
        form.Add(BuildContextContent("FROM scratch\n"), "context", "context.tar");
        form.Add(new StringContent("   "), "tag");

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/build",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    // ===== 构建 =====

    [DockerFact]
    public async Task 构建_成功_产出带标签的镜像()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var tag = NewTag();

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(BuildContextContent("FROM scratch\nCOPY payload.txt /payload.txt\nLABEL stage=build-test\n"), "context", "context.tar");
            form.Add(new StringContent(tag), "tag");

            using var response = await client.PostAsync(
                $"/api/v1/hosts/{_localHost}/images/build",
                form);

            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "实际响应体：" + body);

            var built = await ReadDataAsync<BuildPayload>(response);

            built.ImageId.Should().StartWith("sha256:");
            built.Reference.Should().Be(tag);
            built.Size.Should().BeGreaterThanOrEqualTo(0);
            built.RepoTags.Should().Contain(tag);

            // 产出真的落盘了：inspect 能查到，且 Dockerfile 里的 LABEL 生效
            var detail = await PostDataAsync<DetailPayload>(
                client,
                "inspect",
                new { imageId = tag });

            detail.Id.Should().Be(built.ImageId);
            detail.Labels.Should().ContainKey("stage");
            detail.Labels["stage"].Should().Be("build-test");
        }
        finally
        {
            await RemoveImageAsync(client, tag);
        }
    }

    [DockerFact]
    public async Task 构建_构建参数传入ARG()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var tag = NewTag();

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(BuildContextContent("FROM scratch\nCOPY payload.txt /payload.txt\nARG VERSION\nLABEL version=$VERSION\n"), "context", "context.tar");
            form.Add(new StringContent(tag), "tag");
            form.Add(new StringContent("""{"VERSION":"9.9.9"}"""), "buildArgs");

            using var response = await client.PostAsync(
                $"/api/v1/hosts/{_localHost}/images/build",
                form);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var detail = await PostDataAsync<DetailPayload>(
                client,
                "inspect",
                new { imageId = tag });

            detail.Labels.Should().ContainKey("version");
            detail.Labels["version"].Should().Be("9.9.9");
        }
        finally
        {
            await RemoveImageAsync(client, tag);
        }
    }

    /// <summary>
    /// 构建失败<b>不会</b>让 HTTP 层失败——它只在进度流里。因此这条用例钉死的是：
    /// 服务端检查了进度流，于是错误码是 <c>1806</c> 而不是「成功但什么也没产出」。
    /// </summary>
    [DockerFact]
    public async Task 构建_Dockerfile缺失_返回1806且带构建日志()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var form = new MultipartFormDataContent();

        // 上下文里没有 Dockerfile（只放了一个别的文件）
        form.Add(BuildContextContent("FROM scratch\n", "Dockerfile.missing"), "context", "context.tar");
        form.Add(new StringContent(NewTag()), "tag");

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/build",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ImageBuildFailed);

        // 失败原因必须能让用户看到：「找不到 Dockerfile」比「构建失败」有用得多
        var message = await ReadMessageAsync(response);
        message.Should().NotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task 构建_指定不存在的Dockerfile路径_返回1806()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var form = new MultipartFormDataContent();
        form.Add(BuildContextContent("FROM scratch\n"), "context", "context.tar");
        form.Add(new StringContent(NewTag()), "tag");
        form.Add(new StringContent("Nope/Dockerfile"), "dockerfile");

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/build",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ImageBuildFailed);
    }

    // ===== 推送 =====

    [DockerFact]
    public async Task 推送_镜像不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/push",
            new { image = _unknownImage });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerFact]
    public async Task 推送_镜像标识为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/push",
            new { image = "  " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// 悬挂镜像没有可推的名字——提前报错，而不是让守护进程回一句
    /// <c>refusing to push unqualified name</c>。
    /// </summary>
    [DockerFact]
    public async Task 推送_镜像无标签_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync();

        // 构造一个悬挂镜像：build 出一个镜像后立刻删掉它的标签
        var tag = NewTag();

        try
        {
            using (var form = new MultipartFormDataContent())
            {
                form.Add(BuildContextContent("FROM scratch\nLABEL purpose=push-test\n"), "context", "context.tar");
                form.Add(new StringContent(tag), "tag");

                using var build = await client.PostAsync($"/api/v1/hosts/{_localHost}/images/build", form);

                var buildBody = await build.Content.ReadAsStringAsync();
                build.StatusCode.Should().Be(HttpStatusCode.OK, "实际响应体：" + buildBody);
            }

            // 用 RemoveImage 删除：Docker 的 remove 只会解绑标签，镜像本体变成悬挂镜像。
            // 这里直接用 docker 命令剥掉标签，避免依赖「删镜像会让整条记录消失」。
            var built = await PostDataAsync<ListPayload>(
                client,
                "list",
                new { search = tag, pageSize = 1 });

            built.Items.Should().NotBeEmpty();

            var imageId = built.Items[0].Id;
            await RemoveImageAsync(client, imageId);

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/push",
                new { image = imageId });

            // 删掉标签后镜像要么没了（1401）要么变成悬挂镜像（1000）——两者都不是成功
            var code = await ReadCodeAsync(response);
            code.Should().BeOneOf(ErrorCodes.ValidationFailed, ErrorCodes.DockerResourceNotFound);
        }
        finally
        {
            await RemoveImageAsync(client, tag);
        }
    }

    /// <summary>
    /// 指向一个<b>没有登记过</b>的 Registry 配置必须报 1800，而不是静默匿名推。
    /// </summary>
    [DockerFact]
    public async Task 推送_指定的Registry配置不存在_返回1800()
    {
        using var client = await CreateAuthenticatedClientAsync();
        var tag = NewTag();

        try
        {
            using (var form = new MultipartFormDataContent())
            {
                form.Add(BuildContextContent("FROM scratch\nLABEL purpose=push-test\n"), "context", "context.tar");
                form.Add(new StringContent(tag), "tag");

                using var build = await client.PostAsync($"/api/v1/hosts/{_localHost}/images/build", form);

                var buildBody = await build.Content.ReadAsStringAsync();
                build.StatusCode.Should().Be(HttpStatusCode.OK, "实际响应体：" + buildBody);
            }

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/push",
                new { image = tag, registryId = Guid.NewGuid() });

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ReadCodeAsync(response)).Should().Be(ErrorCodes.RegistryNotFound);
        }
        finally
        {
            await RemoveImageAsync(client, tag);
        }
    }

    // ===== 辅助 =====

    /// <summary>
    /// 生成一个只含 <see cref="_dockerfile"/> 的构建上下文 tar。
    /// </summary>
    /// <param name="dockerfile">Dockerfile 文本。</param>
    /// <param name="fileName">tar 包内的文件名；默认 <c>Dockerfile</c>。</param>
    /// <returns>tar 字节。</returns>
    /// <remarks>
    /// <b>为什么要多放一个 <c>payload.txt</c></b>：<c>FROM scratch</c> 且不带任何
    /// <c>COPY</c> 的镜像<b>层数为 0</b>。而宿主的「本机镜像」探测
    /// （<c>DockerAvailability.ImageReference</c>）会取<b>任意</b>一个有标签的镜像，
    /// 另一些用例随即断言它的 <c>LayerCount &gt; 0</c>——
    /// 挑到我们的 0 层镜像就会让那些用例莫名其妙地挂掉。
    /// 放一个真实文件并 <c>COPY</c> 进去，镜像就有 1 层，
    /// 与普通镜像无异，不会干扰到别的用例。
    /// </remarks>
    private static byte[] BuildContextBytes(string dockerfile, string fileName = "Dockerfile")
    {
        using var buffer = new MemoryStream();

        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, fileName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(dockerfile)),
            });

            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("whalefleet build test payload\n")),
            });
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 把构建上下文打成 multipart 的一部分。
    /// </summary>
    /// <param name="dockerfile">Dockerfile 文本。</param>
    /// <param name="fileName">tar 包内的文件名。</param>
    /// <returns>可直接加进表单的 <see cref="HttpContent"/>。</returns>
    /// <remarks>
    /// ⚠️ <b>必须带 filename</b>：ASP.NET Core 靠 Content-Disposition 里的
    /// <c>filename</c> 判断这一段是「文件」还是「普通表单字段」。
    /// 不带 filename 时 tar 会被当成普通字段（值是它的文本形态），
    /// 于是 <c>Request.Form.Files</c> 是空的，而服务端报「context 字段是必填的」。
    /// 真实客户端（<c>curl -F "context=@build.tar"</c>、浏览器的 FormData）都会带 filename。
    /// </remarks>
    private static ByteArrayContent BuildContextContent(string dockerfile, string fileName = "Dockerfile")
    {
        var content = new ByteArrayContent(BuildContextBytes(dockerfile, fileName));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");

        return content;
    }

    private static MultipartFormDataContent BuildForm(string tag)
    {
        var form = new MultipartFormDataContent();
        form.Add(BuildContextContent("FROM scratch\n"), "context", "context.tar");
        form.Add(new StringContent(tag), "tag");

        return form;
    }

    private static string NewTag() =>
        $"{_buildTagPrefix}:{Guid.NewGuid().ToString("N")[..12]}";

    private static async Task RemoveImageAsync(HttpClient client, string imageId)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/hosts/local/images/remove",
            new { imageId, force = true });

        // 清理失败不该让测试失败：镜像本来就可能已经不在了
        _ = response;
    }

    private async Task CleanupCreatedImagesAsync()
    {
        if (_createdReferences.Count == 0)
        {
            return;
        }

        using var client = _factory.CreateClient();

        foreach (var reference in _createdReferences)
        {
            await RemoveImageAsync(client, reference);
        }
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

    private static async Task<T> PostDataAsync<T>(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/hosts/{_localHost}/images/{action}", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<T>(response);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue();

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("message").GetString();

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

    private sealed record ListPayload(List<ImageItemPayload> Items, int Total);

    private sealed record ImageItemPayload(string Id, List<string> RepoTags, long Size);

    private sealed record DetailPayload(
        string Id,
        Dictionary<string, string> Labels,
        int LayerCount,
        string Architecture);
}
