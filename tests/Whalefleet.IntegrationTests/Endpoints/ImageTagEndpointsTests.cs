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
/// 镜像打标签端点（<c>POST /api/v1/hosts/{hostId}/images/tag</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>必须清理自己打的标签</b>：<c>tag</c> 不复制镜像，只是在同一个镜像 ID 上多挂一个引用。
/// 测试打完不删，下一次跑时目标标签已存在——而那会让「打标签成功」这条路变成幂等分支，
/// 于是真正的成功路径反而没被测到。</para>
/// <para><b>不碰本机已有的镜像</b>：只给自己打的标签加引用，打完立刻解绑，
/// 本体与原有标签一律不动。</para>
/// </remarks>
#pragma warning disable CA1001
public class ImageTagEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _noRoleName = "norole";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";
    private const string _unknownImage = "no-such-image-0123456789:latest";

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

        var passwordHash = hasher.Hash(_password);

        await TestAccounts.CreateUserAsync(users, assignments, _adminName, passwordHash, adminRole);
        await TestAccounts.CreateUserOnlyAsync(users, _noRoleName, passwordHash);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ==================== 守卫链 ====================

    [Fact]
    public async Task 打标签_未认证_返回401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/images/tag", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 打标签_未知主机_返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/images/tag",
            new { imageId = "nginx", repository = "repo/app", tag = "v1" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 打标签_无任何角色分配_返回1501()
    {
        using var client = await CreateAuthenticatedClientAsync(_noRoleName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/tag",
            new { imageId = "nginx", repository = "repo/app", tag = "v1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostForbidden);
    }

    // ==================== 参数校验 ====================

    [Fact]
    public async Task 打标签_镜像标识为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/tag",
            new { imageId = "  ", repository = "repo/app", tag = "v1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 打标签_仓库名为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/tag",
            new { imageId = "nginx", repository = "   ", tag = "v1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 打标签_仓库名里含标签_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/tag",
            new { imageId = "nginx", repository = "nginx:1.27", tag = "alpine" });

        // 用户很可能照着「源镜像」那一栏把 nginx:1.27 整个粘进 repository。
        // 本地拒掉并说清该改哪个字段，比拼成 nginx:1.27:alpine 好。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("1.27")]
    [InlineData("-v1")]
    [InlineData("有/斜杠")]
    [InlineData("有:冒号")]
    public async Task 打标签_标签非法_返回1000(string tag)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/tag",
            new { imageId = "nginx", repository = "repo/app", tag });

        // 标签必须以字母或下划线开头（Docker 的文法）；斜杠属于仓库名部分
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task 打标签_标签省略或空串_按latest处理(string? tag)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        // 与 pull 端点保持一致：空串与省略都取 latest。
        // 「同一个概念在两个端点上有两种含义」比「悄悄给默认值」更难排查。
        var reference = "whalefleet-test:latest";

        try
        {
            var tagged = await TagAsync<TaggedPayload>(
                client,
                new { imageId = DockerAvailability.ImageReference, repository = "whalefleet-test", tag });

            tagged.Reference.Should().Be(reference);
        }
        finally
        {
            await CleanupTagAsync(client, reference);
        }
    }

    // ==================== 真实守护进程路径 ====================

    [DockerFact]
    public async Task 打标签_镜像不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/tag",
            new { imageId = _unknownImage, repository = "repo/app", tag = "v1" });

        // 「镜像没拉过」是可预期状态，不能落到兜底的 1402/500
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerImageFact]
    public async Task 打标签后新标签可见且与原标签同属一个镜像()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var source = DockerAvailability.ImageReference;
        source.Should().NotBeNullOrWhiteSpace("DockerImageFact 已保证宿主上有现成镜像");

        var reference = NewTagReference();

        try
        {
            var tagged = await TagAsync<TaggedPayload>(
                client,
                new { imageId = source, repository = TagRepository, tag = TagOf(reference) });

            tagged.Reference.Should().Be(reference);
            tagged.ImageId.Should().NotBeNullOrWhiteSpace();

            // tag 不复制镜像：新标签与原标签必须指向同一个镜像 ID。
            // 这条断言是本端点的核心语义——界面据此把行更新成「同一镜像的另一个标签」
            // 而不是「多了一行镜像」（后者会让用户以为副本可以随便删）。
            tagged.RepoTags.Should().Contain(reference);

            var viaNewTag = await InspectAsync(client, reference);
            var viaOldTag = await InspectAsync(client, source!);

            viaNewTag.Id.Should().Be(viaOldTag.Id);
        }
        finally
        {
            await CleanupTagAsync(client, reference);
        }
    }

    [DockerImageFact]
    public async Task 删除新标签不影响原镜像ID()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var source = DockerAvailability.ImageReference;
        source.Should().NotBeNullOrWhiteSpace("DockerImageFact 已保证宿主上有现成镜像");

        var reference = NewTagReference();

        try
        {
            var tagged = await TagAsync<TaggedPayload>(
                client,
                new { imageId = source, repository = TagRepository, tag = TagOf(reference) });

            // 删掉新标签：这是「解绑引用」，不是「删镜像」
            await RemoveAsync(client, reference);

            var stillThere = await InspectAsync(client, source!);

            stillThere.Id.Should().Be(tagged.ImageId, "只删新标签不应影响镜像本体");
        }
        finally
        {
            await CleanupTagAsync(client, reference);
        }
    }

    // ==================== 辅助方法 ====================

    /// <summary>测试统一使用的目标仓库名。</summary>
    /// <remarks>
    /// 固定成一个明显是测试产物的名字：万一清理失败，下一个在界面上看到的人
    /// 也能一眼认出「这是测试留下的」而不是误当成真实镜像。
    /// </remarks>
    private const string TagRepository = "whalefleet-test/tag";

    /// <summary>
    /// 生成一个唯一的测试标签引用。
    /// </summary>
    /// <returns>形如 <c>whalefleet-test/tag:t-xxxxxxxx</c> 的完整引用。</returns>
    /// <remarks>
    /// 标签里带随机串：固定标签会让「上一次跑遗留的标签」把这次变成幂等分支，
    /// 于是真正的成功路径反而没被测到。
    /// </remarks>
    private static string NewTagReference() =>
        $"{TagRepository}:t-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>从完整引用里取出标签部分。</summary>
    /// <param name="reference">形如 <c>repo/name:tag</c> 的引用。</param>
    /// <returns>标签部分。</returns>
    /// <remarks>
    /// 从<b>最后一个冒号</b>切，而不是从最后一个斜杠切：
    /// <c>whalefleet-test/tag:t-xxx</c> 的斜杠在仓库名里，标签在冒号后面。
    /// </remarks>
    private static string TagOf(string reference)
    {
        var colon = reference.LastIndexOf(':');

        return reference[(colon + 1)..];
    }

    /// <summary>
    /// 清理测试打出来的标签（只解绑引用，不动镜像本体）。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="reference">要清理的完整引用。</param>
    /// <remarks>
    /// ⚠️ <b>这里绝不能断言</b>：它在 <c>finally</c> 里，而 <c>finally</c> 抛出的异常会
    /// <b>顶替</b>掉用例真正的失败原因。清理失败（标签已不存在、镜像已被删）都是预期内的，
    /// 断言它们只会让「打标签为什么失败」这条信息彻底消失。
    /// </remarks>
    private static async Task CleanupTagAsync(HttpClient client, string reference)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/remove",
            new { imageId = reference });
    }

    /// <summary>
    /// 调 <c>images/remove</c>（用于解绑测试标签）。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="imageId">镜像标识。</param>
    /// <returns>删除结果。</returns>
    private static async Task<RemovedPayload> RemoveAsync(HttpClient client, string imageId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/remove",
            new { imageId });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"“remove” 应当成功：{await response.Content.ReadAsStringAsync()}");

        return await ReadDataAsync<RemovedPayload>(response);
    }

    /// <summary>
    /// 调 <c>images/inspect</c>。
    /// </summary>
    /// <param name="client">已认证客户端。</param>
    /// <param name="imageId">镜像标识。</param>
    /// <returns>镜像详情投影。</returns>
    private static async Task<InspectPayload> InspectAsync(HttpClient client, string imageId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/inspect",
            new { imageId });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"“inspect” 应当成功：{await response.Content.ReadAsStringAsync()}");

        return await ReadDataAsync<InspectPayload>(response);
    }

    /// <summary>
    /// 调 <c>images/tag</c>。
    /// </summary>
    /// <typeparam name="T">结果类型。</typeparam>
    /// <param name="client">已认证客户端。</param>
    /// <param name="body">请求体。</param>
    /// <returns>打标签结果。</returns>
    /// <remarks>
    /// ⚠️ <b>三个端点必须各写一个 helper</b>：早先图省事只写了一个「发到 tag 的」
    /// helper，于是 inspect / remove 的请求也被打到了 tag 端点上——
    /// 报错是「imageId 不能为空」（tag 的必填校验），
    /// 而真正的原因是请求打错了地址。
    /// </remarks>
    private static async Task<T> TagAsync<T>(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync($"/api/v1/hosts/{_localHost}/images/tag", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"“tag” 应当成功：{await response.Content.ReadAsStringAsync()}");

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

    private sealed record TaggedPayload(string ImageId, string Reference, List<string> RepoTags);

    private sealed record RemovedPayload(List<string> Untagged, List<string> Deleted);

    /// <summary>
    /// 镜像详情投影（只取断言用得到的字段）。
    /// </summary>
    /// <remarks>
    /// 属性名是 <c>Id</c> 而不是 <c>ImageId</c>——对外契约里镜像详情的字段就叫 <c>id</c>。
    /// 写成 <c>ImageId</c> 不会编译报错，只会在反序列化时静默拿到 <see langword="null"/>，
    /// 而断言会在「比对了 null」之后给出完全指错方向的报错。
    /// </remarks>
    private sealed record InspectPayload(string Id);
}
