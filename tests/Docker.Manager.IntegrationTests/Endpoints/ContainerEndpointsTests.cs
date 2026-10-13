using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Users;
using Docker.Manager.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Docker.Manager.IntegrationTests.Endpoints;

/// <summary>
/// 容器查询端点（<c>/api/v1/hosts/{hostId}/containers/*</c>）的端到端测试。
/// </summary>
/// <remarks>
/// <para><b>两组用例，各证明一件事</b>：不带 <see cref="DockerFactAttribute"/> 的用例证明
/// 「主机作用域的守卫」（认证 → 主机存在 → 可见性 → 参数校验）确实生效；带它的用例证明
/// 「真实守护进程拿到的数据能被投影成对外契约」，且容器不存在时映射成 <c>1401</c> 而不是 500。</para>
/// <para><b>为什么不断言具体容器</b>：宿主机上有哪些容器不该是测试的前提——那会让用例在不同机器上
/// 时红时绿。这里只断言「形状与自洽性」（<c>total</c> 与 <c>items</c> 数量一致、分页字段回显）；
/// 需要具体容器才有意义的断言留给 0.0.10 起的创建接口用例（届时可以自己造容器）。</para>
/// </remarks>
#pragma warning disable CA1001
public class ContainerEndpointsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string _password = "StrongP@ss123";
    private const string _adminName = "admin";
    private const string _operatorName = "operator";
    private const string _localHost = "local";
    private const string _unknownHost = "no-such-host";
    private const string _unknownContainer = "no-such-container-0123456789";

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

        using var response = await client.PostAsync($"/api/v1/hosts/{_localHost}/containers/list", content: null);

        // 未认证必须在可见性校验之前被拦下：授权中间件跑在 CurrentHostMiddleware 之前，
        // 因此这里得到的是 401 而不是「UserId 为空 → 当作非管理员」的 403。
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 未知主机_返回1300()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_unknownHost}/containers/list",
            new { all = true });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task 非管理员访问主机_返回1501()
    {
        using var client = await CreateAuthenticatedClientAsync(_operatorName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/list",
            new { all = true });

        // 0.3.0 之前是 fail-closed：除管理员外一律拒绝，而不是默认放行。
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.HostForbidden);
    }

    [Fact]
    public async Task 详情_容器标识为空_返回1000()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/inspect",
            new { containerId = "  " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task 日志_tail越界_返回1000(int tail)
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/logs",
            new { containerId = "anything", tail });

        // 越界是报错而不是悄悄夹紧：调用方要 100 万行却只拿到 1 万行，会以为「日志就这么多」。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.ValidationFailed);
    }

    [DockerFact]
    public async Task 列表_字段回显且分页自洽()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/list",
            new { all = true, pageIndex = 0, pageSize = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await ReadDataAsync<ListPayload>(response);

        list.PageIndex.Should().Be(0);
        list.PageSize.Should().Be(5);
        list.Total.Should().BeGreaterThanOrEqualTo(list.Items.Count);
        list.Items.Count.Should().BeLessThanOrEqualTo(5);

        foreach (var item in list.Items)
        {
            item.Id.Should().NotBeNullOrWhiteSpace();
            item.Name.Should().NotStartWith("/", "对外契约里的名字不带 Docker 内部的前导斜杠");
            item.Image.Should().NotBeNullOrWhiteSpace();
        }
    }

    [DockerFact]
    public async Task 列表_每页条数超上限会被夹紧()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/list",
            new { pageIndex = 0, pageSize = 5000 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // 夹紧后的实际值必须回显：调用方传 5000 时能看出服务端用了 100。
        (await ReadDataAsync<ListPayload>(response)).PageSize.Should().Be(100);
    }

    [DockerFact]
    public async Task 列表_含已停止时总数不小于仅运行()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        var all = await ListAsync(client, all: true);
        var runningOnly = await ListAsync(client, all: false);

        // 默认只列运行中的（与 docker ps 一致）；all=true 是 docker ps -a。
        all.Total.Should().BeGreaterThanOrEqualTo(runningOnly.Total);
    }

    [DockerFact]
    public async Task 详情_容器不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/inspect",
            new { containerId = _unknownContainer });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    [DockerFact]
    public async Task 日志_容器不存在_返回1401()
    {
        using var client = await CreateAuthenticatedClientAsync(_adminName);

        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/logs",
            new { containerId = _unknownContainer, tail = 10 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).Should().Be(ErrorCodes.DockerResourceNotFound);
    }

    private static async Task<ListPayload> ListAsync(HttpClient client, bool all)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/hosts/{_localHost}/containers/list",
            new { all });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await ReadDataAsync<ListPayload>(response);
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

    /// <summary>容器列表响应的投影（只取断言用得到的字段）。</summary>
    /// <remarks>属性名是 PascalCase 而线上是 camelCase，靠 <see cref="_jsonOptions"/> 的大小写不敏感兜住。</remarks>
    private sealed record ListPayload(List<ContainerPayload> Items, int Total, int PageIndex, int PageSize);

    private sealed record ContainerPayload(string Id, string Name, string Image, string State, string Status);

    private sealed record LoginPayload(string AccessToken);
}
