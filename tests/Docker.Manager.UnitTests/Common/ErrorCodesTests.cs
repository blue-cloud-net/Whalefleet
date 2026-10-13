using System.Reflection;
using Docker.Manager.Application.Common;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Common;

/// <summary>
/// <see cref="ErrorCodes"/> 常量表与 <c>docs/api-spec.md</c> 的一致性测试。
/// </summary>
/// <remarks>
/// 这些测试的作用是「防手滑」：错误码是公开 API 契约的一部分（0.1.0 起冻结），
/// 一旦数值被误改、重复或漏登记到 <see cref="ErrorCodeCatalog"/>，必须立刻失败。
/// </remarks>
public class ErrorCodesTests
{
    /// <summary>
    /// 常量名 → 期望数值，与 <c>docs/api-spec.md</c>「当前已分配的错误码」表格逐行对应。
    /// </summary>
    private static readonly Dictionary<string, int> _expectedValues = new(StringComparer.Ordinal)
    {
        // 通用
        [nameof(ErrorCodes.Success)] = 0,
        [nameof(ErrorCodes.ValidationFailed)] = 1000,
        [nameof(ErrorCodes.NotFound)] = 1001,
        [nameof(ErrorCodes.DockerOperationFailed)] = 1002,
        [nameof(ErrorCodes.OperationTimeout)] = 1003,

        // 鉴权与会话
        [nameof(ErrorCodes.Unauthenticated)] = 1100,
        [nameof(ErrorCodes.TokenExpired)] = 1101,
        [nameof(ErrorCodes.RefreshTokenRevoked)] = 1102,
        [nameof(ErrorCodes.LoginRateLimited)] = 1103,
        [nameof(ErrorCodes.TerminalTokenInvalid)] = 1104,

        // 用户管理
        [nameof(ErrorCodes.UsernameAlreadyExists)] = 1200,
        [nameof(ErrorCodes.UserNotFound)] = 1201,
        [nameof(ErrorCodes.WeakPassword)] = 1202,

        // 主机管理
        [nameof(ErrorCodes.HostNotFound)] = 1300,
        [nameof(ErrorCodes.HostUnreachable)] = 1301,
        [nameof(ErrorCodes.HostDisabled)] = 1302,
        [nameof(ErrorCodes.HostNameAlreadyExists)] = 1303,
        [nameof(ErrorCodes.GroupNameAlreadyExists)] = 1304,
        [nameof(ErrorCodes.GroupNotFound)] = 1305,

        // 资源操作
        [nameof(ErrorCodes.DockerResourceNotFound)] = 1401,
        [nameof(ErrorCodes.DockerApiFailed)] = 1402,
        [nameof(ErrorCodes.DockerTimeout)] = 1403,
        [nameof(ErrorCodes.DockerResourceConflict)] = 1404,

        // 资源权限
        [nameof(ErrorCodes.ResourceForbidden)] = 1500,
        [nameof(ErrorCodes.HostForbidden)] = 1501,
        [nameof(ErrorCodes.OperationForbidden)] = 1502,

        // 服务器内部错误
        [nameof(ErrorCodes.InternalError)] = 9999,
    };

    private static Dictionary<string, int> ReadConstants()
    {
        return typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(int))
            .ToDictionary(
                field => field.Name,
                field => (int)field.GetRawConstantValue()!,
                StringComparer.Ordinal);
    }

    [Fact]
    public void Constants_ShouldMatchDocumentedValues()
    {
        var actual = ReadConstants();

        actual.Should().Equal(_expectedValues);
    }

    [Fact]
    public void Constants_ShouldNotContainDuplicateValues()
    {
        var duplicates = ReadConstants()
            .GroupBy(pair => pair.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"code {group.Key} 被 {String.Join(", ", group.Select(pair => pair.Key))} 重复占用")
            .ToArray();

        duplicates.Should().BeEmpty();
    }

    [Fact]
    public void Constants_ShouldAllBeRegisteredInCatalog()
    {
        var unregistered = ReadConstants()
            .Where(pair => !ErrorCodeCatalog.IsKnown(pair.Value))
            .Select(pair => $"{pair.Key} ({pair.Value})")
            .ToArray();

        unregistered.Should().BeEmpty(
            "新增错误码时必须同步在 ErrorCodeCatalog 中登记语义分类与默认文案");
    }
}
