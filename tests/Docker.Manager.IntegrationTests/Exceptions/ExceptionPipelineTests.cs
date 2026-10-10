using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Docker.Manager.Application.Common;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.IntegrationTests.Exceptions;

/// <summary>
/// 全局异常处理中间件的端到端契约测试。
/// </summary>
/// <remarks>
/// <para>这是 0.0.2 的核心验收项：<b>任何未处理异常都必须产生标准的 <c>ApiResult</c> JSON</b>，
/// 且响应体不能泄漏任何内部信息。</para>
/// <para>通过开发专用故障注入端点 <c>POST /api/v1/dev/throw?kind=…</c> 触发各异常分支
/// （见 <c>Docker.Manager.Api.Development.DevelopmentFaults</c>）。</para>
/// </remarks>
public class ExceptionPipelineTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ExceptionPipelineTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// 故障类型 → 期望（业务错误码, HTTP 状态码, 响应消息）。
    /// </summary>
    /// <remarks>
    /// 注意 <c>business</c> 分支抛的是 <c>BusinessException.NotFound</c>，
    /// 因此期望 404 而非 500 —— 业务异常自带错误码，状态码由错误码推导。
    /// </remarks>
    public static TheoryData<string?, int, HttpStatusCode, string> FaultCases => new()
    {
        // 未捕获异常 → 兜底 9999 / 500，消息被替换为固定文案
        { null, ErrorCodes.InternalError, HttpStatusCode.InternalServerError, "服务器内部错误" },
        { "internal", ErrorCodes.InternalError, HttpStatusCode.InternalServerError, "服务器内部错误" },

        // 业务异常：错误码与消息都来自业务方
        { "business", ErrorCodes.NotFound, HttpStatusCode.NotFound, "开发用故障注入：资源不存在" },

        // 参数异常 → 400（Core 层的不变量违规也映射到这一支）
        { "validation", ErrorCodes.ValidationFailed, HttpStatusCode.BadRequest, "参数校验失败" },

        // KeyNotFoundException → 404
        { "notfound", ErrorCodes.NotFound, HttpStatusCode.NotFound, "资源不存在" },

        // 超时 → 408
        { "timeout", ErrorCodes.OperationTimeout, HttpStatusCode.RequestTimeout, "操作超时" },
    };

    [Theory]
    [MemberData(nameof(FaultCases))]
    public async Task UnhandledException_ShouldMapToDocumentedApiResult(
        string? kind,
        int expectedCode,
        HttpStatusCode expectedStatus,
        string expectedMessage)
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(BuildThrowUri(kind), content: null);

        response.StatusCode.Should().Be(expectedStatus);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse>();

        body.Should().NotBeNull();
        body!.Success.Should().BeFalse();
        body.Code.Should().Be(expectedCode);
        body.Message.Should().Be(expectedMessage);
        body.Data.Should().BeNull("失败响应的 data 必须为 null");
    }

    [Theory]
    [MemberData(nameof(FaultCases))]
    public async Task UnhandledException_ShouldNotLeakInternals(
        string? kind,
        int expectedCode,
        HttpStatusCode expectedStatus,
        string expectedMessage)
    {
        _ = expectedCode;
        _ = expectedStatus;
        _ = expectedMessage;

        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(BuildThrowUri(kind), content: null);

        var raw = await response.Content.ReadAsStringAsync();

        // 绝不泄漏异常类型名、堆栈帧或源码位置。
        raw.Should().NotContain("Exception");
        raw.Should().NotContain("System.");
        raw.Should().NotContain("   at ");
        raw.Should().NotContain(".cs:line");
        raw.Should().NotContain("StackTrace");
        raw.Should().NotContain("Program.cs");
    }

    [Fact]
    public async Task ErrorResponse_ShouldUseJsonContentTypeWithCharset()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(BuildThrowUri(null), content: null);

        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
    }

    [Fact]
    public async Task ErrorResponse_ShouldHaveExactlyTheDocumentedShape()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(BuildThrowUri("internal"), content: null);

        var raw = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(raw);

        document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should().Equal("success", "code", "message", "data");

        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ErrorResponse_ShouldEmitChineseAsRawUtf8()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(BuildThrowUri("internal"), content: null);

        var raw = await response.Content.ReadAsStringAsync();

        // 0.0.2 决策：使用 JavaScriptEncoder.Create(UnicodeRanges.All)，
        // 中文原样输出（提高可读性、减小报文），而非默认的 \uXXXX 转义。
        // 同时仍然保留 JavaScript 编码器对 < > & 等 HTML 敏感字符的转义，
        // 因此这里只断言「不出现任何 Unicode 转义序列」。
        raw.Should().Contain("服务器内部错误");
        raw.Should().NotContain("\\u");
    }

    [Fact]
    public async Task UnknownFaultKind_ShouldReturnValidationErrorWithHint()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(BuildThrowUri("bogus"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<ApiResponse>();

        body.Should().NotBeNull();
        body!.Code.Should().Be(ErrorCodes.ValidationFailed);

        // 未知 kind 用 4xx 业务异常返回，使提示信息对开发者可见（9999 的消息会被固定化）。
        body.Message.Should().Contain("bogus");
    }

    private static string BuildThrowUri(string? kind) =>
        kind is null ? "/api/v1/dev/throw" : $"/api/v1/dev/throw?kind={kind}";
}
