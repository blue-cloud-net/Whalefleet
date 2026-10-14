using System.Text.Json;
using Whalefleet.Application.Common;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Common;

/// <summary>
/// <see cref="ApiResult"/> / <see cref="ApiResult{T}"/> / <see cref="ApiResults"/> 的行为测试。
/// </summary>
/// <remarks>
/// 其中序列化测试锁定 <c>docs/api-spec.md</c> 的线上契约：字段为 camelCase，
/// 且失败响应必须显式包含 <c>"data": null</c>。
/// </remarks>
public class ApiResultTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OkWithoutData_ShouldProduceSuccessResult()
    {
        var result = ApiResults.Ok();

        result.Success.Should().BeTrue();
        result.Code.Should().Be(ErrorCodes.Success);
        result.Message.Should().Be("ok");
    }

    [Fact]
    public void OkWithData_ShouldCarryData()
    {
        var result = ApiResults.Ok(new { username = "admin" });

        result.Success.Should().BeTrue();
        result.Code.Should().Be(ErrorCodes.Success);
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public void OkWithData_ShouldDefaultDataToNullWhenOmitted()
    {
        var result = ApiResults.Ok<string>();

        result.Success.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    [Fact]
    public void FailWithoutData_ShouldProduceFailureResult()
    {
        var result = ApiResults.Fail(ErrorCodes.NotFound, "资源不存在");

        result.Success.Should().BeFalse();
        result.Code.Should().Be(ErrorCodes.NotFound);
        result.Message.Should().Be("资源不存在");
    }

    [Fact]
    public void FailWithDataPlaceholder_ShouldLeaveDataNull()
    {
        var result = ApiResults.Fail<object>(ErrorCodes.InternalError, "服务器内部错误");

        result.Success.Should().BeFalse();
        result.Code.Should().Be(ErrorCodes.InternalError);
        result.Data.Should().BeNull();
    }

    [Fact]
    public void GenericFailure_ShouldSerializeToDocumentedShape()
    {
        var result = ApiResults.Fail<object>(ErrorCodes.InternalError, "服务器内部错误");

        var json = JsonSerializer.Serialize(result, WebJson);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("code").GetInt32().Should().Be(ErrorCodes.InternalError);
        root.GetProperty("message").GetString().Should().Be("服务器内部错误");
        root.TryGetProperty("data", out var data).Should().BeTrue("失败响应必须显式输出 data 字段");
        data.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void GenericSuccess_ShouldSerializeDataAsCamelCase()
    {
        var result = ApiResults.Ok(new { username = "admin" });

        var json = JsonSerializer.Serialize(result, WebJson);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("code").GetInt32().Should().Be(ErrorCodes.Success);
        root.GetProperty("message").GetString().Should().Be("ok");
        root.GetProperty("data").GetProperty("username").GetString().Should().Be("admin");
    }

    [Fact]
    public void NonGenericResult_ShouldOmitDataField()
    {
        var result = ApiResults.Fail(ErrorCodes.ValidationFailed, "参数校验失败");

        var json = JsonSerializer.Serialize(result, WebJson);

        using var document = JsonDocument.Parse(json);

        document.RootElement.TryGetProperty("data", out _).Should().BeFalse(
            "非泛型 ApiResult 不携带 data 字段，需要 data:null 的接口应使用 ApiResults.Fail<T>");
    }
}
