using System.Text.Json;
using Docker.Manager.IntegrationTests;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.IntegrationTests.Endpoints;

/// <summary>
/// <c>GET /healthz</c> 的端到端测试（0.0.15 由「固定 200」增强为 SQLite 连通性检查）。
/// </summary>
/// <remarks>
/// 这几条守护的是<b>容器编排契约</b>：<c>docker run --health-cmd</c> 与 Kubernetes
/// 的 liveness probe 都只看状态码与响应体。若探针在数据库不可写时仍报 healthy，
/// 编排器会持续把流量打进来，而每个业务端点其实都在 500。</remarks>
public sealed class HealthzEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    /// <summary>
    /// 初始化测试类。
    /// </summary>
    /// <param name="factory">测试宿主工厂。</param>
    public HealthzEndpointsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 数据库可用时返回 200，且明确给出 sqlite 检查项为 healthy。
    /// </summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task Healthz_WhenDatabaseAvailable_ShouldReportSqliteHealthy()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        // 0.0.2 决策：/healthz 是基础设施端点，裸 JSON 不包 ApiResult。
        root.GetProperty("status").GetString().Should().Be("ok");
        root.TryGetProperty("success", out _).Should().BeFalse();

        var sqlite = root.GetProperty("checks").GetProperty("sqlite");
        sqlite.GetProperty("status").GetString().Should().Be("healthy");

        // 耗时必须真的被测量，而不是占位 0——否则「变慢了」这件事永远看不出来。
        sqlite.GetProperty("durationMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// 数据库不可访问时返回 503，并带上可诊断的错误信息。
    /// </summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task Healthz_WhenDatabaseUnreachable_ShouldReturn503WithReason()
    {
        // ⚠️ 必须用**独立**工厂：`WithSetting` 是就地修改（返回 this），
        // 打在类共享的 IClassFixture 上会把坏路径留给本类的其他用例，
        // 表现为「随机顺序下健康检查莫名 unhealthy」。
        var root = Path.Combine(Path.GetTempPath(), $"dm-healthz-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(root, "data");
        var databasePath = Path.Combine(dataDirectory, "docker-manager.db");

        using var factory = new ApiFactory().WithSetting("Persistence:DatabasePath", databasePath);
        using var client = factory.CreateClient();

        // 宿主已启动、迁移已跑完——此刻数据库是健康的。
        (await client.GetAsync("/healthz")).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        // 再制造真实故障：整个 data 目录消失（等价于容器里 data 卷没挂上）。
        // 之所以在宿主起来之后才删，是因为 SqliteConnectionFactory 在**构造时**
        // 就会建目录——目录压根建不出来的话是启动失败，而不是探针 unhealthy，
        // 那属于另一种故障（且已被另一条用例覆盖）。
        Directory.Delete(dataDirectory, recursive: true);

        using var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var payload = document.RootElement;

        payload.GetProperty("status").GetString().Should().Be("degraded");

        var sqlite = payload.GetProperty("checks").GetProperty("sqlite");
        sqlite.GetProperty("status").GetString().Should().Be("unhealthy");

        // 排查时最需要的就是这句：为什么连不上。
        sqlite.GetProperty("error").ValueKind.Should().Be(System.Text.Json.JsonValueKind.String);
    }

    /// <summary>
    /// 健康检查不应被状态码页面包装成 ApiResult。
    /// </summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task Healthz_ShouldNotBeWrappedInApiResult()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz");
        var raw = await response.Content.ReadAsStringAsync();

        raw.Should().NotContain("\"code\"");
        raw.Should().NotContain("\"message\"");
    }

    /// <summary>
    /// 未配置反代白名单时，伪造的 <c>X-Forwarded-For</c> 必须被忽略。
    /// </summary>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 守护的是「0.0.15 修好了反代限流，但没有顺手开成信任一切」。
    /// 若中间件被默认信任任意来源，攻击者换一个伪造 IP 就能绕过登录限流。
    /// </remarks>
    [Fact]
    public async Task ForwardedFor_WithoutConfiguredProxy_ShouldNotAffectHealthz()
    {
        using var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        using var response = await client.SendAsync(request);

        // 探针仍应正常（该头不影响连通性检查），
        // 关键是它不会导致 400 / 500——被忽略的头不应产生任何副作用。
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}