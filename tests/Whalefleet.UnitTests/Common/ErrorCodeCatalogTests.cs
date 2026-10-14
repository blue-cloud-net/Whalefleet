using Whalefleet.Application.Common;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Common;

/// <summary>
/// <see cref="ErrorCodeCatalog"/> 的语义分类与文案测试。
/// </summary>
public class ErrorCodeCatalogTests
{
    /// <summary>
    /// 错误码 → 期望分类，与 <c>docs/api-spec.md</c> 的 HTTP 列推导一致。
    /// </summary>
    /// <remarks>
    /// 表中特意覆盖了几处「不能用数值分段推断」的样本：
    /// <c>1002</c>（同段内却是 500）、<c>1103</c>（同段内却是 429）、
    /// <c>1301</c>（同段内却是 502）。
    /// </remarks>
    public static TheoryData<int, ErrorCategory> ClassifyCases => new()
    {
        { ErrorCodes.Success, ErrorCategory.Success },

        { ErrorCodes.ValidationFailed, ErrorCategory.Validation },
        { ErrorCodes.NotFound, ErrorCategory.NotFound },
        { ErrorCodes.DockerOperationFailed, ErrorCategory.Internal },
        { ErrorCodes.OperationTimeout, ErrorCategory.Timeout },

        { ErrorCodes.Unauthenticated, ErrorCategory.Unauthorized },
        { ErrorCodes.TokenExpired, ErrorCategory.Unauthorized },
        { ErrorCodes.RefreshTokenRevoked, ErrorCategory.Unauthorized },
        { ErrorCodes.LoginRateLimited, ErrorCategory.RateLimited },
        { ErrorCodes.TerminalTokenInvalid, ErrorCategory.Unauthorized },

        { ErrorCodes.UsernameAlreadyExists, ErrorCategory.Validation },
        { ErrorCodes.UserNotFound, ErrorCategory.NotFound },
        { ErrorCodes.WeakPassword, ErrorCategory.Validation },

        { ErrorCodes.HostNotFound, ErrorCategory.NotFound },
        { ErrorCodes.HostUnreachable, ErrorCategory.UpstreamFailure },

        { ErrorCodes.DockerResourceNotFound, ErrorCategory.NotFound },
        { ErrorCodes.DockerApiFailed, ErrorCategory.Internal },
        { ErrorCodes.DockerTimeout, ErrorCategory.Timeout },

        { ErrorCodes.ResourceForbidden, ErrorCategory.Forbidden },
        { ErrorCodes.HostForbidden, ErrorCategory.Forbidden },
        { ErrorCodes.OperationForbidden, ErrorCategory.Forbidden },

        { ErrorCodes.InternalError, ErrorCategory.Internal },
    };

    [Theory]
    [MemberData(nameof(ClassifyCases))]
    public void Classify_ShouldReturnExpectedCategory(int code, ErrorCategory expected)
    {
        ErrorCodeCatalog.Classify(code).Should().Be(expected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1042)]
    [InlineData(1399)]
    [InlineData(1555)]
    [InlineData(8000)]
    [InlineData(-1)]
    public void Classify_ShouldFallbackToInternalForUnknownCodes(int code)
    {
        ErrorCodeCatalog.IsKnown(code).Should().BeFalse();

        ErrorCodeCatalog.Classify(code).Should().Be(
            ErrorCategory.Internal,
            "未登记错误码一律按服务器内部错误处理，绝不能按数值分段推断——" +
            "分段内既有 404 也有 429、502，推断会把服务端故障伪装成客户端请求错误");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1042)]
    [InlineData(9998)]
    public void GetDefaultMessage_ShouldReturnFallbackForUnknownCodes(int code)
    {
        ErrorCodeCatalog.GetDefaultMessage(code).Should().Be("请求处理失败");
    }

    [Fact]
    public void GetDefaultMessage_ShouldReturnNonEmptyMessageForEveryKnownCode()
    {
        var knownCodes = ErrorCodeCatalog.KnownCodes.ToArray();

        knownCodes.Should().NotBeEmpty();

        foreach (var code in knownCodes)
        {
            var message = ErrorCodeCatalog.GetDefaultMessage(code);

            message.Should().NotBeNullOrWhiteSpace($"错误码 {code} 的默认文案不能为空");
            message.Should().NotBe(
                "请求处理失败",
                $"错误码 {code} 已在目录中登记，不应回落到未登记兜底文案");
        }
    }

    [Fact]
    public void KnownCodes_ShouldBeUnique()
    {
        ErrorCodeCatalog.KnownCodes.Should().OnlyHaveUniqueItems();
    }
}
