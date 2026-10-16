using System.Net;
using System.Reflection;
using Whalefleet.Api.Errors;
using Whalefleet.Application.Common;
using FluentAssertions;
using Xunit;

namespace Whalefleet.IntegrationTests.Errors;

/// <summary>
/// 错误码 ↔ HTTP 状态码映射的契约测试。
/// </summary>
/// <remarks>
/// 本测试的价值在于把「应用层的错误码分类」与「接口层的 HTTP 映射」串起来，
/// 逐码对照 <c>docs/api-spec.md</c> 的「当前已分配的错误码」表格，
/// 防止任何一侧被独立改动而导致线上契约漂移。
/// </remarks>
public class ErrorHttpStatusTests
{
    /// <summary>
    /// 错误码 → 期望 HTTP 状态码，逐行对应 <c>docs/api-spec.md</c> 的错误码表格。
    /// </summary>
    private static readonly Dictionary<int, int> DocumentedCodeToHttpStatus = new()
    {
        [ErrorCodes.Success] = 200,

        [ErrorCodes.ValidationFailed] = 400,
        [ErrorCodes.NotFound] = 404,
        [ErrorCodes.DockerOperationFailed] = 500,
        [ErrorCodes.OperationTimeout] = 408,

        [ErrorCodes.Unauthenticated] = 401,
        [ErrorCodes.TokenExpired] = 401,
        [ErrorCodes.RefreshTokenRevoked] = 401,
        [ErrorCodes.LoginRateLimited] = 429,
        [ErrorCodes.TerminalTokenInvalid] = 401,

        [ErrorCodes.UsernameAlreadyExists] = 400,
        [ErrorCodes.UserNotFound] = 404,
        [ErrorCodes.WeakPassword] = 400,

        [ErrorCodes.HostNotFound] = 404,
        [ErrorCodes.HostUnreachable] = 502,
        [ErrorCodes.HostDisabled] = 400,
        [ErrorCodes.HostNameAlreadyExists] = 400,
        [ErrorCodes.GroupNameAlreadyExists] = 400,
        [ErrorCodes.GroupNotFound] = 404,

        [ErrorCodes.DockerResourceNotFound] = 404,
        [ErrorCodes.DockerApiFailed] = 500,
        [ErrorCodes.DockerTimeout] = 408,
        [ErrorCodes.DockerResourceConflict] = 409,

        [ErrorCodes.ResourceForbidden] = 403,
        [ErrorCodes.HostForbidden] = 403,
        [ErrorCodes.OperationForbidden] = 403,
        [ErrorCodes.PermissionNotFound] = 404,

        [ErrorCodes.RoleNotFound] = 404,
        [ErrorCodes.RoleNameAlreadyExists] = 400,
        [ErrorCodes.RoleInUse] = 400,
        [ErrorCodes.RoleProtected] = 400,
        [ErrorCodes.PermissionUnknown] = 400,
        [ErrorCodes.RoleAssignmentNotFound] = 404,

        [ErrorCodes.InternalError] = 500,
    };

    public static TheoryData<ErrorCategory, int> CategoryCases => new()
    {
        { ErrorCategory.Success, 200 },
        { ErrorCategory.Validation, 400 },
        { ErrorCategory.NotFound, 404 },
        { ErrorCategory.Unauthorized, 401 },
        { ErrorCategory.RateLimited, 429 },
        { ErrorCategory.Timeout, 408 },
        { ErrorCategory.Forbidden, 403 },
        { ErrorCategory.UpstreamFailure, 502 },
        { ErrorCategory.Internal, 500 },
    };

    [Theory]
    [MemberData(nameof(CategoryCases))]
    public void ToHttpStatus_ShouldMapEveryCategory(ErrorCategory category, int expected)
    {
        ErrorHttpStatus.ToHttpStatus(category).Should().Be(expected);
    }

    [Fact]
    public void DocumentedTable_ShouldCoverEveryErrorCodeConstant()
    {
        var allCodes = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(int))
            .Select(field => (int)field.GetRawConstantValue()!)
            .ToArray();

        DocumentedCodeToHttpStatus.Keys.Should().BeEquivalentTo(
            allCodes,
            "新增错误码时必须同时在本测试的期望表中登记其 HTTP 状态码");
    }

    [Fact]
    public void FromErrorCode_ShouldMatchDocumentedHttpStatusForEveryCode()
    {
        foreach (var (code, expectedHttpStatus) in DocumentedCodeToHttpStatus)
        {
            ErrorHttpStatus.FromErrorCode(code).Should().Be(
                expectedHttpStatus,
                $"错误码 {code} 的 HTTP 状态码必须与 docs/api-spec.md 一致");
        }
    }

    [Theory]
    [InlineData(401, ErrorCodes.Unauthenticated)]
    [InlineData(403, ErrorCodes.ResourceForbidden)]
    [InlineData(404, ErrorCodes.NotFound)]
    [InlineData(408, ErrorCodes.OperationTimeout)]
    [InlineData(400, ErrorCodes.ValidationFailed)]
    [InlineData(405, ErrorCodes.ValidationFailed)]
    [InlineData(415, ErrorCodes.ValidationFailed)]
    [InlineData(429, ErrorCodes.ValidationFailed)]
    [InlineData(500, ErrorCodes.InternalError)]
    [InlineData(503, ErrorCodes.InternalError)]
    public void ToErrorCode_ShouldMapFrameworkStatusBackToBusinessCode(int httpStatus, int expectedCode)
    {
        ErrorHttpStatus.ToErrorCode(httpStatus).Should().Be(expectedCode);
    }

    [Fact]
    public void ToHttpStatus_ShouldNeverReturnSuccessForFailureCodes()
    {
        // 除 ErrorCategory.Success 外，任何分类都不能映射到 2xx，否则客户端会把失败当成功。
        foreach (var category in Enum.GetValues<ErrorCategory>().Where(value => value != ErrorCategory.Success))
        {
            ErrorHttpStatus.ToHttpStatus(category).Should().BeGreaterThanOrEqualTo(
                (int)HttpStatusCode.BadRequest,
                $"分类 {category} 不应映射到成功状态码");
        }
    }
}
