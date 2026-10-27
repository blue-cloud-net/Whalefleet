using System.Formats.Tar;
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
/// 清理端点（<c>/api/v1/hosts/{hostId}/{images,volumes}/prune</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>每个清理用例都必须带 <c>label</c> 过滤</b>：清理是<b>不可逆</b>的，
/// 而 <c>POST /volumes/prune</c> 不带过滤时会清掉本机<b>全部</b>未被引用的卷
/// （连具名卷一起——服务端固定下发 <c>all=true</c>），
/// <c>POST /images/prune</c> 不带过滤时会清掉全部悬空镜像。
/// 在开发机上那等于删掉别的项目的数据。因此本组的夹具一律打上
/// <c>whalefleet.test</c> 标签，清理时也只按这个标签过滤。</para>
/// <para><b>标签过滤正是本组要验的东西</b>：它一旦没生效，用例会以
/// 「别人的悬空镜像 / 闲置卷被删了」的方式失败——这是本组唯一一处
/// 「测挂了就会丢数据」的地方，因此标签是<b>钉死的常量</b>而不是变量。</para>
/// <para><b>「具名卷也被清理」要专门验</b>：守护进程自 Engine API 1.42 起
/// 在没有 <c>all</c> 过滤时只清「匿名卷」（没有名字的那些），
/// 而本项目的预览（<c>system/df</c>）列的是全部未被引用的卷。
/// 服务端不补 <c>all=true</c> 的话，用户会看到「清单里有 5 个，删完还在」。</para>
/// </remarks>
#pragma warning disable CA1001
public class PruneEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";

    /// <summary>本组夹具专用的标签键。</summary>
    private const string _labelKey = "whalefleet.test";

    /// <summary>清理时的过滤表达式（目标夹具用它）。</summary>
    private const string _targetFilter = "whalefleet.test=1";

    /// <summary>目标夹具的标签值。</summary>
    private const string _targetValue = "1";

    /// <summary>对照组夹具的标签值：带它的资源<b>不该</b>被目标过滤碰到。</summary>
    private const string _controlValue = "2";

    private const string _imageTagPrefix = "whalefleet-prune-test";

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

    // ===== 鉴权与参数校验（不需要 Docker） =====

    [Theory]
    [InlineData("images")]
    [InlineData("volumes")]
    public async Task 清理_未认证_返回401(string resource)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/{resource}/prune",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("images")]
    [InlineData("volumes")]
    public async Task 清理_未知主机_返回1300(string resource)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/{resource}/prune",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Theory]
    [InlineData("images")]
    [InlineData("volumes")]
    public async Task 清理_标签过滤含控制字符_返回1000(string resource)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/{resource}/prune",
            new { label = "bad\nlabel" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData("images")]
    [InlineData("volumes")]
    public async Task 清理_标签过滤过长_返回1000(string resource)
    {
        using var client = await this.LoginAsync();

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/{resource}/prune",
            new { label = new string('a', 300) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    // ===== 卷清理 =====

    [DockerFact]
    public async Task 清理数据卷_只删带标签的未引用卷()
    {
        using var client = await this.LoginAsync();
        var target = NewName("target");
        var control = NewName("control");

        try
        {
            await CreateVolumeAsync(client, target, _targetValue);
            await CreateVolumeAsync(client, control, _controlValue);

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/prune",
                new { label = _targetFilter });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var result = await ReadDataAsync<PrunePayload>(response);

            result.Deleted.Should().Contain(target, "带匹配标签的卷应当被清理");
            result.Deleted.Should().NotContain(control, "标签不匹配的卷不该被清理");
            result.ReclaimedBytes.Should().BeGreaterThanOrEqualTo(0);

            // 目标卷真的没了
            using var inspected = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/inspect",
                new { name = target });

            inspected.StatusCode.Should().Be(HttpStatusCode.NotFound);

            // 对照组还在
            using var controlInspected = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/inspect",
                new { name = control });

            controlInspected.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await RemoveVolumeAsync(client, target);
            await RemoveVolumeAsync(client, control);
        }
    }

    [DockerFact]
    public async Task 清理数据卷_具名卷也被清理()
    {
        using var client = await this.LoginAsync();
        var volume = NewName("named");

        try
        {
            await CreateVolumeAsync(client, volume, _targetValue);

            // 守护进程自 API 1.42 起默认只清「匿名卷」（没有名字的那些）。
            // 本项目的预览（system/df）列的是全部未被引用的卷，因此服务端必须补 all=true。
            // 少了它，这里会一个都删不掉——而响应仍然是 200，界面看不出任何异常。
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/volumes/prune",
                new { label = _targetFilter });

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var result = await ReadDataAsync<PrunePayload>(response);

            result.Deleted.Should().Contain(
                volume,
                "具名卷必须也被清理，否则预览里列出来的卷会「删了还在」");
        }
        finally
        {
            await RemoveVolumeAsync(client, volume);
        }
    }

    // ===== 镜像清理 =====

    [DockerFact]
    public async Task 清理镜像_只清悬空镜像时不动带标签的镜像()
    {
        using var client = await this.LoginAsync();
        var docker = await this.GetDockerClientAsync();

        var targetTag = $"{_imageTagPrefix}:{Guid.NewGuid():N}";

        try
        {
            await BuildImageAsync(client, targetTag, _targetValue);

            // danglingOnly=true 只清「悬空镜像」（一个标签都不剩的）。
            // 目标镜像还带着标签，因此不该被碰到——
            // 这同时验证了 dangling 过滤确实下发了（没下发的话它会被当成「全部」而遭误删）。
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/prune",
                new { danglingOnly = true, label = _targetFilter });

            response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));

            var result = await ReadDataAsync<PrunePayload>(response);

            result.Deleted.Should().NotContain(targetTag);

            (await ImageExistsAsync(client, targetTag)).Should().BeTrue(
                "带标签的镜像不属于「悬空镜像」，只清悬空时它必须还在");
        }
        finally
        {
            await RemoveImageAsync(docker, targetTag);
        }
    }

    [DockerFact]
    public async Task 清理镜像_清所有未使用镜像时只删带标签的()
    {
        using var client = await this.LoginAsync();
        var docker = await this.GetDockerClientAsync();

        var targetTag = $"{_imageTagPrefix}:{Guid.NewGuid():N}";
        var controlTag = $"{_imageTagPrefix}:{Guid.NewGuid():N}";

        try
        {
            await BuildImageAsync(client, targetTag, _targetValue);
            await BuildImageAsync(client, controlTag, _controlValue);

            // danglingOnly=false 的语义是「不止悬空」（等价 docker image prune -a）：
            // 任何未被容器使用的镜像都进入候选，而标签过滤把范围收窄到只有目标那个。
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/hosts/{_localHost}/images/prune",
                new { danglingOnly = false, label = _targetFilter });

            response.StatusCode.Should().Be(HttpStatusCode.OK, await DescribeAsync(response));

            var result = await ReadDataAsync<PrunePayload>(response);

            result.Deleted.Should().Contain(targetTag);

            (await ImageExistsAsync(client, targetTag)).Should().BeFalse(
                "带匹配标签的未使用镜像应当被清理");

            (await ImageExistsAsync(client, controlTag)).Should().BeTrue(
                "标签不匹配的镜像不该被清理");
        }
        finally
        {
            await RemoveImageAsync(docker, targetTag);
            await RemoveImageAsync(docker, controlTag);
        }
    }

    /// <summary>
    /// 判断某个标签的镜像是否还在本机。
    /// </summary>
    /// <param name="client">已认证的客户端。</param>
    /// <param name="tag">镜像标签。</param>
    /// <returns>还在时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 用 <c>search</c> 缩小范围而不是拉全量再翻：镜像多的机器上，
    /// 新建的镜像完全可能不在第 0 页——那会让断言挂在与被测行为无关的地方。
    /// </remarks>
    private static async Task<bool> ImageExistsAsync(HttpClient client, string tag)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/images/list",
            new { search = tag, pageSize = 100 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadAsync(response);

        return body.GetProperty("data").GetProperty("items")
            .EnumerateArray()
            .SelectMany(item => item.GetProperty("repoTags").EnumerateArray())
            .Any(reference => reference.GetString() == tag);
    }

    // ===== 夹具与工具 =====

    /// <summary>
    /// 生成一个带唯一后缀的卷名。
    /// </summary>
    /// <param name="prefix">前缀（用于人眼辨认用途）。</param>
    /// <returns>合法且不会撞上其他用例的名字。</returns>
    private static string NewName(string prefix) => $"wf-t-prune-{prefix}-{Guid.NewGuid():N}";

    private static async Task CreateVolumeAsync(HttpClient client, string name, string labelValue)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/create",
            new
            {
                name,
                labels = new Dictionary<string, string> { [_labelKey] = labelValue },
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task RemoveVolumeAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/volumes/remove",
            new { name, force = true });

        // 清理失败不该让测试失败：卷本来就可能已经不在了
        _ = response;
    }

    /// <summary>
    /// 构建一个带指定标签的最小镜像（<c>FROM scratch</c> + 复制一个文件 + 一个 LABEL）。
    /// </summary>
    /// <param name="client">已认证的客户端。</param>
    /// <param name="tag">镜像标签。</param>
    /// <param name="labelValue">写入 <c>whalefleet.test</c> 标签的值。</param>
    /// <remarks>
    /// <para><b>必须 COPY 一个真实文件</b>：<c>FROM scratch</c> 且不带任何 <c>COPY</c>
    /// 的镜像<b>层数为 0</b>。而宿主的「本机镜像」探测会挑中<b>任意</b>带标签的镜像，
    /// 别的用例随即断言它的 <c>LayerCount &gt; 0</c>——挑到我们的 0 层镜像
    /// 会让那些用例莫名失败，表现为与本用例完全无关的红。</para>
    /// <para><b>标签写进 Dockerfile</b>而不是走构建参数：后者要额外依赖
    /// 「构建接口的 labels 字段怎么映射」这一层间接，测试就会同时依赖
    /// 两套映射；写进 Dockerfile 则只依赖 Docker 本身的行为。</para>
    /// <para><b>用 <c>FROM scratch</c> 是为了离线也能跑</b>：拉基础镜像需要网络，
    /// 而那与「验证清理参数怎么映射」无关。</para>
    /// </remarks>
    private static async Task BuildImageAsync(HttpClient client, string tag, string labelValue)
    {
        var dockerfile =
            $"FROM scratch\nCOPY marker.txt /marker.txt\nLABEL {_labelKey}={labelValue}\n";

        var form = new MultipartFormDataContent();
        form.Add(BuildContextContent(dockerfile), "context", "context.tar");
        form.Add(new StringContent(tag), "tag");

        using var response = await client.PostAsync(
            $"/api/v1/hosts/{_localHost}/images/build",
            form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 打包一个只含 <c>Dockerfile</c> 与被复制文件的构建上下文。
    /// </summary>
    /// <param name="dockerfile">Dockerfile 内容。</param>
    /// <returns>可直接加进表单的 <see cref="HttpContent"/>。</returns>
    /// <remarks>
    /// <b>文件段必须带 filename</b>：ASP.NET Core 靠 Content-Disposition 里的
    /// <c>filename</c> 判断这一段是「文件」还是「普通表单字段」；
    /// 不带 filename 时 tar 会被当成表单字段，服务端报「context 字段是必填的」。
    /// </remarks>
    private static ByteArrayContent BuildContextContent(string dockerfile)
    {
        using var buffer = new MemoryStream();

        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(dockerfile)),
            });

            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "marker.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("whalefleet prune test payload\n")),
            });
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");

        return content;
    }

    /// <summary>
    /// 尽力删掉一个自造镜像（清理用，任何失败都不该让测试变红）。
    /// </summary>
    /// <param name="docker">Docker 客户端。</param>
    /// <param name="reference">镜像引用（标签）。</param>
    private static async Task RemoveImageAsync(IDockerClient docker, string reference)
    {
        try
        {
            await docker.Images.DeleteImageAsync(
                reference,
                new ImageDeleteParameters { Force = true, NoPrune = true },
                CancellationToken.None);
        }
        catch (Exception)
        {
            // 清理失败不应掩盖真正的断言失败。
        }
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient();

        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = _adminName, password = _password });

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

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await ReadAsync(response);

        body.GetProperty("success").GetBoolean().Should().BeTrue("响应体：" + body.GetRawText());

        return body.GetProperty("data").Deserialize<T>(_jsonOptions)!;
    }

    private static async Task<int> ReadCodeAsync(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("code").GetInt32();

    /// <summary>
    /// 把响应体拼成一句可读的说明，供断言失败时展示。
    /// </summary>
    /// <param name="response">待描述的响应。</param>
    /// <returns>响应体文本。</returns>
    /// <remarks>
    /// <b>为什么要有这个</b>：清理端点失败时状态码本身说明不了问题——
    /// 404 可能是「主机不存在」（1300）、也可能是「Docker 说不认识」（1401），
    /// 两者要查的地方完全不同。只看状态码就等于蒙着眼睛排查。
    /// </remarks>
    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        "响应体：" + await response.Content.ReadAsStringAsync();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private sealed record LoginPayload(string AccessToken);

    private sealed record PrunePayload(List<string> Deleted, long ReclaimedBytes);
}