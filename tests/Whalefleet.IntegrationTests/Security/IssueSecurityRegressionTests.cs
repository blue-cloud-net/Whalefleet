using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Whalefleet.Api.Controllers;
using Whalefleet.Api.Middleware;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using Whalefleet.Infrastructure.Persistence;
using Whalefleet.Infrastructure.Security;

using Xunit;

namespace Whalefleet.IntegrationTests.Security;

/// <summary>
/// issues 中密钥完整性和公开错误脱敏的行为回归。
/// </summary>
public sealed class IssueSecurityRegressionTests
{
    [Theory]
    [InlineData("repository.key")]
    [InlineData("registry.key")]
    public void 读取密钥_文件损坏_不覆盖或生成新密钥(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"wf-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[] { 1, 2, 3 };
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, bytes);
            var store = new RepositorySecretKeyStore(Options.Create(new SqliteOptions
            {
                DatabasePath = Path.Combine(root, "test.db"),
            }), NullLogger<RepositorySecretKeyStore>.Instance);

            var act = () => store.GetKey();
            act.Should().Throw<InvalidDataException>();
            File.ReadAllBytes(path).Should().Equal(bytes);
            Directory.GetFiles(root).Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 读取密钥_路径不可读取_不当作不存在()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wf-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "repository.key"));
        try
        {
            var store = new RepositorySecretKeyStore(Options.Create(new SqliteOptions
            {
                DatabasePath = Path.Combine(root, "test.db"),
            }), NullLogger<RepositorySecretKeyStore>.Instance);
            var act = () => store.GetKey();
            act.Should().Throw<UnauthorizedAccessException>();
            Directory.Exists(Path.Combine(root, "repository.key")).Should().BeTrue();
            Directory.GetFiles(root).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 创建密钥_新文件_仅属主可读写且可复用()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wf-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new SqliteOptions { DatabasePath = Path.Combine(root, "test.db") });
            var repository = new RepositorySecretKeyStore(options, NullLogger<RepositorySecretKeyStore>.Instance);
            repository.GetKey().Should().Equal(repository.GetKey());
            var secrets = new SecretFileStore(options, NullLogger<SecretFileStore>.Instance);
            secrets.GetJwtSecret().Should().Be(secrets.GetJwtSecret());
            secrets.WriteInitialAdminCredential("admin", "test-password");
            Directory.GetFiles(root, "*.tmp").Should().BeEmpty();
            if (!OperatingSystem.IsWindows())
            {
                foreach (var path in Directory.GetFiles(root))
                {
                    File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 异常响应_服务端取消_返回超时且不泄漏消息()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException("secret-path"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        context.Response.StatusCode.Should().Be(408);
        var json = Encoding.UTF8.GetString(body.ToArray());
        json.Should().NotContain("secret-path");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("code").GetInt32().Should().Be(ErrorCodes.OperationTimeout);
    }

    [Fact]
    public async Task 异常响应_客户端断开_不写响应()
    {
        var context = new DefaultHttpContext { RequestAborted = new CancellationToken(canceled: true) };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException(), NullLogger<ExceptionHandlingMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        body.Length.Should().Be(0);
    }

    [Fact]
    public async Task 健康检查_内部异常_返回503且不泄漏路径()
    {
        var controller = new HealthController(new FailedHealthCheck(), TimeProvider.System);
        var result = (ObjectResult)await controller.GetAsync(CancellationToken.None);
        result.StatusCode.Should().Be(503);
        var json = JsonSerializer.Serialize(result.Value);
        json.Should().NotContain("secret-path");
        json.Should().Contain("degraded");
    }

    [Fact]
    public async Task 兜底审计_内部异常_保存安全文案并重抛原异常()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/probe";
        var audit = new CapturingAudit();
        var failure = new IOException("password=secret-path");
        var middleware = new AuditFallbackMiddleware(_ => throw failure,
            NullLogger<AuditFallbackMiddleware>.Instance);
        var act = () => middleware.InvokeAsync(context, audit);
        (await act.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        audit.Draft.Should().NotBeNull();
        audit.Draft!.Detail.Should().NotContain("secret-path");
        audit.Draft.ErrorCode.Should().Be(ErrorCodes.InternalError);
    }

    private sealed class FailedHealthCheck : HealthCheckService
    {
        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default) => Task.FromResult(new HealthReport(
                new Dictionary<string, HealthReportEntry>
                {
                    ["sqlite"] = new(HealthStatus.Unhealthy, null, TimeSpan.Zero,
                        new IOException("secret-path"), null),
                }, TimeSpan.Zero));
    }

    private sealed class CapturingAudit : IAuditLogger
    {
        public AuditLogDraft? Draft
        {
            get; private set;
        }
        public Task RecordAsync(AuditLogDraft draft, CancellationToken cancellationToken = default)
        {
            this.Draft = draft;
            return Task.CompletedTask;
        }
    }
}
