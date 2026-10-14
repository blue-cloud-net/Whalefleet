using System.Net;
using FluentAssertions;
using Xunit;

namespace Whalefleet.IntegrationTests.Security;

/// <summary>
/// CORS 白名单行为的端到端测试。
/// </summary>
/// <remarks>
/// 0.0.2 的安全基线是「默认拒绝跨域」：<c>Cors:AllowedOrigins</c> 为空时不得发出<b>任何</b>
/// <c>Access-Control-*</c> 响应头；只有显式列入白名单的 origin 才被放行。
/// </remarks>
public class CorsTests
{
    private const string AllowedOriginKey = "Cors:AllowedOrigins:0";

    [Fact]
    public async Task WithoutWhitelist_ShouldNotEmitAnyCorsHeader()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", "http://evil.example");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        HasAnyCorsHeader(response).Should().BeFalse(
            "白名单为空时不得发出任何 Access-Control-* 响应头");
    }

    [Fact]
    public async Task WithoutWhitelist_ShouldNotAnswerPreflight()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var request = CreatePreflight("http://evil.example");
        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task WithWhitelist_ShouldAnswerPreflightForListedOrigin()
    {
        using var factory = CreateFactoryWithAllowedOrigin("http://allowed.example");
        using var client = factory.CreateClient();

        using var request = CreatePreflight("http://allowed.example");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ReadHeader(response, "Access-Control-Allow-Origin").Should().Be("http://allowed.example");
    }

    [Fact]
    public async Task WithWhitelist_ShouldRejectPreflightForUnlistedOrigin()
    {
        using var factory = CreateFactoryWithAllowedOrigin("http://allowed.example");
        using var client = factory.CreateClient();

        using var request = CreatePreflight("http://evil.example");
        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task WithWhitelist_ShouldEchoOriginOnActualRequest()
    {
        using var factory = CreateFactoryWithAllowedOrigin("http://allowed.example");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", "http://allowed.example");

        using var response = await client.SendAsync(request);

        ReadHeader(response, "Access-Control-Allow-Origin").Should().Be("http://allowed.example");
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeTrue();
    }

    [Fact]
    public async Task WithWhitelist_ShouldNotEchoOriginForOtherOrigin()
    {
        using var factory = CreateFactoryWithAllowedOrigin("http://allowed.example");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", "http://other.example");

        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    private static ApiFactory CreateFactoryWithAllowedOrigin(string origin) =>
        new ApiFactory().WithSetting(AllowedOriginKey, origin);

    private static HttpRequestMessage CreatePreflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/healthz");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }

    private static bool HasAnyCorsHeader(HttpResponseMessage response) =>
        response.Headers.Any(header =>
            header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));

    private static string? ReadHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? String.Join(",", values) : null;
}
