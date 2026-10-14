using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Whalefleet.Application.Common;
using FluentAssertions;
using Xunit;

namespace Whalefleet.IntegrationTests.Endpoints;

/// <summary>
/// 状态码页面（<c>UseStatusCodePages</c>）与 <c>/healthz</c> 响应格式的端到端测试。
/// </summary>
public class StatusCodePageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public StatusCodePageTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnknownRoute_ShouldReturnStandardApiResult()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse>();

        body.Should().NotBeNull();
        body!.Success.Should().BeFalse();
        body.Code.Should().Be(ErrorCodes.NotFound);
        body.Message.Should().Be(ErrorCodeCatalog.GetDefaultMessage(ErrorCodes.NotFound));
        body.Data.Should().BeNull();
    }

    [Fact]
    public async Task UnknownRoute_ShouldUseJsonContentType()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist");

        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
    }

    [Fact]
    public async Task HealthEndpoint_ShouldKeepRawJsonPayload()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        // 0.0.2 决策：/healthz 是基础设施端点，不属于 /api/v1 RPC 体系，
        // 成功响应保持裸 JSON（不包裹 ApiResult），以免 Docker HEALTHCHECK 等外部探针依赖业务信封。
        root.GetProperty("status").GetString().Should().Be("ok");
        root.TryGetProperty("timestamp", out _).Should().BeTrue();
        root.TryGetProperty("success", out _).Should().BeFalse();
    }

    [Fact]
    public async Task HealthEndpoint_ShouldNotBeAffectedByStatusCodePages()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        var raw = await response.Content.ReadAsStringAsync();

        raw.Should().NotContain("\"code\"");
        raw.Should().NotContain("\"message\"");
    }
}
