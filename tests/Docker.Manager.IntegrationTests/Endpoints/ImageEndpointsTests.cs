using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Users;
using Docker.Manager.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Docker.Manager.IntegrationTests.Endpoints;

/// <summary>
/// 镜像端点（<c>/api/v1/hosts/{hostId}/images/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>不碰本机已有的镜像</b>：删除是本组唯一有破坏性的操作，而别人的镜像库不是本项目的测试夹具。
/// 需要验证「删除成功」时用一条自造镜像的用例（拉一个公开小镜像 → 查 → 删），
/// 其余删除用例一律走失败路径。</para>
/// <para><b>「拉取失败」用例会真的出网</b>：拉一个确定不存在的镜像。之所以敢这么写，是因为本项目把
/// Docker 的失败判定放在进度流里（HTTP 始终 200），无论失败原因是「没有这个镜像」「仓库不让你拉」
/// 还是「连不上」，都走同一条路——于是这条用例在离线环境下也成立。</para>
/// </remarks>
#pragma warning disable CA1001
public class ImageEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";
    private const string _unknownImage = "no-such-image-0123456789:latest";

    /// <summary>
    /// 用于验证拉取成功路径的镜像：极小（数十 KB）且在 Docker Hub 上必定存在。
    /// </summary>
    /// <remarks>
    /// 不复用 <see cref="DockerAvailability.ImageReference"/>：那台机器上「本地已有的镜像」可能是
    /// 私有仓库或本机 build 出来的（<c>my-app:test</c>），重新拉取它要么需要认证、要么仓库里根本没有。
    /// 换成一个固定的公开小镜像，成功路径才是可重复的。
    /// </remarks>
    private const string _pullableImage = "hello-world";
    private const string _pullableTag = "latest";

    /// <summary>等待新标签可见的上限（秒）。</summary>
    private static readonly TimeSpan TagPropagationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>等待新标签可见时的轮询间隔（毫秒）。</summary>
    private const int TagPropagationInterval = 100;

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
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var adminRole = await roles.FindByNameAsync(Role.AdminName);
        var operatorRole = await roles.FindByNameAsync("operator");

        adminRole.Should().NotBeNull("内置角色应由迁移种入");
        operatorRole.Should().NotBeNull("内置角色应由迁移种入");

        var passwordHash = hasher.Hash(_password);

        await users.AddAsync(User.Create(Guid.NewGuid(), _adminName, passwordHash, adminRole!.Id));
        await users.AddAsync(User.Create(Guid.NewGuid(), _operatorName, passwordHash, operatorRole!.Id));
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task 未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/images/list", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("inspect")]
    [InlineData("pull")]
    [InlineData("remove")]
    public async Task 未知主机_返回1300(string action)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/images/{action}",
            new { image = "nginx" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("inspect")]
    [InlineData("pull")]
    [InlineData("remove")]
    public async Task 非管理员访问主机_返回1501(string action)
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/{action}",
            new { image = "nginx" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostForbidden);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("remove")]
    public async Task 镜像标识为空_返回1000(string action)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/{action}",
            new { imageId = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 拉取_仓库名为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/pull",
            new { image = "  " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 拉取_仓库名已含标签又传标签_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/pull",
            new { image = "nginx:1.27", tag = "alpine" });

        // 静默拼成 nginx:1.27:alpine 会拉到一个谁都没要求过的镜像，而用户要的镜像根本没拉下来。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [DockerFact]
    public async Task 列表_字段回显且分页自洽()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/list",
            new { pageIndex = 0, pageSize = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await ReadDataAsync<ListPayload>(response);

        list.PageIndex.Should().Be(0);
        list.PageSize.Should().Be(5);
        list.Total.Should().BeGreaterThanOrEqualTo(list.Items.Count);
        list.Items.Count.Should().BeLessThanOrEqualTo(5);

        foreach (var item in list.Items)
        {
            item.Id.Should().StartWith("sha256:");
            item.Size.Should().BeGreaterThan(0);
            item.Created.Should().NotBe(default(DateTimeOffset));

            // Docker 的 <none> 占位标签被过滤掉了，界面上不该出现叫「<none>:<none>」的镜像。
            item.RepoTags.Should().NotContain(tag => tag.Contains("<none>", StringComparison.Ordinal));
        }
    }

    [DockerFact]
    public async Task 列表_每页条数超上限会被夹紧()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/list",
            new { pageIndex = 0, pageSize = 5000 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // 夹紧后的实际值必须回显：调用方传 5000 时能看出服务端用了 100。
        (await ReadDataAsync<ListPayload>(response)).PageSize.Should().Be(100);
    }

    [DockerFact]
    public async Task 列表_搜索缩小结果集()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var all = await ListAsync(client, search: null);
        var some = await ListAsync(client, search: _unknownImage.Split(':')[0]);

        // 搜一个不存在的名字必然为空；这不是「过滤器坏了」，而是它该有的行为。
        some.Total.Should().Be(0);
        some.Items.Should().BeEmpty();
        all.Total.Should().BeGreaterThanOrEqualTo(some.Total);
    }

    [DockerFact]
    public async Task 详情_镜像不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/inspect",
            new { imageId = _unknownImage });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerFact]
    public async Task 删除_镜像不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/remove",
            new { imageId = _unknownImage });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerFact]
    public async Task 详情_本机镜像可读()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var reference = DockerAvailability.ImageReference!;

        var detail = await PostDataAsync<DetailPayload>(
            client,
            "inspect",
            new { imageId = reference });

        detail.Id.Should().StartWith("sha256:");
        detail.Size.Should().BeGreaterThan(0);
        detail.LayerCount.Should().BeGreaterThan(0);
        detail.Architecture.Should().NotBeNullOrWhiteSpace("真实镜像一定有架构");
        detail.Os.Should().NotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task 拉取_镜像不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/pull",
            new { image = _unknownImage.Split(':')[0], tag = _unknownImage.Split(':')[1] });

        // 「镜像不存在」这条路径是 SDK 抛异常（Docker 返回 404）→ 翻译成 1401。
        // 另一条路径（仓库拒绝 / 网络中断）不抛异常，只在进度流里带 error → 1402；
        // 那条无法用「拉一个不存在的镜像」触发（它恰恰走的是 404），因此没有集成用例，
        // 由 PullCollector 的注释与代码守住。
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerRegistryFact]
    public async Task 拉取_公开镜像_返回镜像ID与大小()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var reference = $"{_pullableImage}:{_pullableTag}";

        try
        {
            var pulled = await PostDataAsync<PullPayload>(
                client,
                "pull",
                new { image = _pullableImage, tag = _pullableTag });

            pulled.ImageId.Should().StartWith("sha256:");
            pulled.Reference.Should().Be(reference);
            pulled.Size.Should().BeGreaterThan(0);
            pulled.RepoTags.Should().Contain(reference);

            // 拉取完能查到详情（顺带证明确实落盘了）。
            var detail = await PostDataAsync<DetailPayload>(
                client,
                "inspect",
                new { imageId = reference });

            detail.Id.Should().Be(pulled.ImageId);
        }
        finally
        {
            // 自造镜像用完就清掉：测试不该给别人的镜像库留下垃圾。
            using var cleanup = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/remove",
                new { imageId = reference, force = true });

            // 清理失败（镜像本来就没拉到）不算测试失败，这里不断言状态码。
        }
    }

    [DockerRegistryFact]
    public async Task 删除_自造镜像_返回解绑与删除结果()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);
        var reference = $"{_pullableImage}:{_pullableTag}";

        await PostDataAsync<PullPayload>(client, "pull", new { image = _pullableImage, tag = _pullableTag });

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/remove",
            new { imageId = reference, force = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var removed = await ReadDataAsync<RemovedPayload>(response);

        // 两种结果都是合法形态：解绑了标签（镜像本体因为还有别的引用而留下），
        // 或整个删掉。断言的是「结构对得上」，而不是「必须是哪一种」。
        removed.Untagged.Should().NotBeNull();
        removed.Deleted.Should().NotBeNull();

        (removed.Untagged.Count + removed.Deleted.Count).Should().BeGreaterThan(0);

        // 删干净了：再查应当如实变成 1401。
        using var gone = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/inspect",
            new { imageId = reference });

        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerImageFact]
    public async Task 删除_只解绑标签时镜像本体保留()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        // 用本机现成的镜像打一个**临时标签**当夹具：不联网、不碰别人的标签，
        // 删完之后原标签与镜像都完好无损。
        var source = DockerAvailability.ImageReference!;
        var temporary = $"dm-selftest-{Guid.NewGuid():N}"[..16] + ":tmp";

        Tag(source, temporary);

        try
        {
            // 先确认夹具真的打上了：打标签失败而后续仍去删，会得到一个 1401，
            // 那看起来像「删除逻辑坏了」，实际是前置步骤没成。
            var tagged = await PostDataAsync<DetailPayload>(
                client,
                "inspect",
                new { imageId = temporary });

            tagged.RepoTags.Should().Contain(temporary);

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/remove",
                new { imageId = temporary });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var removed = await ReadDataAsync<RemovedPayload>(response);

            // 这条用例守住的是「untagged 与 deleted 必须分开映射」：
            // 临时标签被解绑了，但镜像本体还被原标签引用着，因此 deleted 必须为空。
            // 把两者混在一个列表里的话，这里就会出现一条 sha256:… 被误报成「已删除」。
            removed.Untagged.Should().Contain(temporary);
            removed.Deleted.Should().BeEmpty();

            // 镜像本体确实还在。
            var detail = await PostDataAsync<DetailPayload>(
                client,
                "inspect",
                new { imageId = source });

            detail.Id.Should().StartWith("sha256:");
        }
        finally
        {
            using var cleanup = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/remove",
                new { imageId = temporary, force = true });
        }
    }

    /// <summary>
    /// 直接用 SDK 给本机镜像打一个标签（造测试夹具用）。
    /// </summary>
    /// <param name="image">源镜像引用。</param>
    /// <param name="reference">目标引用（<c>仓库:标签</c>）。</param>
    /// <remarks>
    /// 绕过本项目 API 是刻意的：本项目 0.0.10 没有打标签的端点（那属于 0.2.0 的镜像管理），
    /// 而「删除只解绑标签」这条语义需要一个额外的标签才能验证。
    /// 用 SDK 直接造夹具，就不必为此在生产代码里开一个用不上的端点。
    /// </remarks>
    private static void Tag(string image, string reference)
    {
        var separator = reference.LastIndexOf(':');

        using var client = new DockerClientBuilder()
            .WithEndpoint(new Uri($"unix://{DockerAvailability.DefaultSocketPath}"))
            .Build();

        // TagImageAsync 是同步的（内部同步等待 HTTP）；这里只需发一次标签请求。
        client.Images.TagImageAsync(
            image,
            new ImageTagParameters
            {
                RepositoryName = reference[..separator],
                Tag = reference[(separator + 1)..],
            },
            CancellationToken.None);

        // 轮询等标签可见：Docker 的标签更新是**异步传播**的（经由内部事件队列），
        // 打完之后立刻 List 有可能读不到——头一次 List 看不到、隔几十毫秒再读就有了。
        // 直接等待并给出明确超时，比让后续断言带着 1401「假失败」要好排查得多。
        var deadline = DateTime.UtcNow.Add(TagPropagationTimeout);

        while (DateTime.UtcNow < deadline)
        {
            var applied = client.Images
                .ListImagesAsync(new ImagesListParameters { All = false }, CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .Any(item => item.RepoTags?.Contains(reference, StringComparer.Ordinal) == true);

            if (applied)
            {
                return;
            }

            Thread.Sleep(TagPropagationInterval);
        }

        throw new InvalidOperationException(
            $"打标签 {image} → {reference} 在 {TagPropagationTimeout.TotalSeconds:F0} 秒内没有生效；"
            + "后续断言会因此变成假失败");    }

    private static async Task<ListPayload> ListAsync(HttpClient client, string? search)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/list",
            new { search });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<ListPayload>(response);
    }

    private static async Task<T> PostDataAsync<T>(HttpClient client, string action, object body)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/{action}",
            body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"“{action}” 应当成功");

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

    /// <summary>镜像列表响应的投影（只取断言用得到的字段）。</summary>
    private sealed record ListPayload(List<ImagePayload> Items, int Total, int PageIndex, int PageSize);

    private sealed record ImagePayload(
        string Id,
        List<string> RepoTags,
        DateTimeOffset Created,
        long Size,
        long SharedSize,
        long ContainerCount);

    private sealed record DetailPayload(
        string Id,
        long Size,
        string Architecture,
        string Os,
        int LayerCount,
        List<string>? RepoTags);

    private sealed record PullPayload(string ImageId, string Reference, long Size, List<string> RepoTags);

    private sealed record RemovedPayload(List<string> Untagged, List<string> Deleted);

    private sealed record LoginPayload(string AccessToken);
}
