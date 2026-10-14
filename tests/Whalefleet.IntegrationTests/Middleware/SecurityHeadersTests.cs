using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Whalefleet.IntegrationTests.Middleware;

/// <summary>
/// 安全响应头中间件的端到端测试。
/// </summary>
/// <remarks>
/// 断言的是「<b>每个</b>响应都带这三个头」——包括错误响应。
/// 只在成功路径上加头很容易漏掉异常路径，而后者恰恰是攻击者更关心的。
/// </remarks>
public class SecurityHeadersTests : IClassFixture<ApiFactory>
{
    private static readonly (string Name, string Expected)[] _expectedHeaders =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("X-Frame-Options", "DENY"),
        ("Referrer-Policy", "no-referrer"),
    ];

    private readonly ApiFactory _factory;

    /// <summary>
    /// 初始化测试。
    /// </summary>
    /// <param name="factory">测试宿主工厂。</param>
    public SecurityHeadersTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task 成功响应_带全部安全头()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        AssertHeaders(response);
    }

    [Fact]
    public async Task 未知路由的404_也带全部安全头()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertHeaders(response);
    }

    [Fact]
    public async Task 未认证的401_也带全部安全头()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/auth/me", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertHeaders(response);
    }

    [Fact]
    public async Task 参数校验失败的400_也带全部安全头()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { username = "", password = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertHeaders(response);
    }

    private static void AssertHeaders(HttpResponseMessage response)
    {
        foreach (var (name, expected) in _expectedHeaders)
        {
            response.Headers.TryGetValues(name, out var values).Should().BeTrue($"{name} 必须存在");
            values!.Should().ContainSingle().Which.Should().Be(expected);
        }
    }
}
