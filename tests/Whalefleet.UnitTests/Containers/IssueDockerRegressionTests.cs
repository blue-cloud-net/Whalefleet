using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Application.Images;
using Whalefleet.Application.Networks;
using Whalefleet.Application.Repositories;
using Whalefleet.Application.Stacks;

using Xunit;

namespace Whalefleet.UnitTests.Containers;

/// <summary>
/// 经真实 Docker SDK 的确定性 HTTP 回归，不访问宿主 Docker 资源。
/// </summary>
public sealed class IssueDockerRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 拉取镜像_匹配仓库凭据_正确传递认证头(bool authenticated)
    {
        string? header = null;
        await using var daemon = new FakeDockerDaemon(request =>
        {
            if (request.Url!.AbsolutePath.EndsWith("/images/create", StringComparison.Ordinal))
            {
                header = request.Headers["X-Registry-Auth"];
                return (200, "{\"status\":\"done\"}\n");
            }

            return (200, "{\"Id\":\"sha256:test\",\"Size\":42,\"RepoTags\":[\"private.example/app:v1\"]}");
        });
        var credentials = new Credentials(authenticated);
        var images = new ImageService(new StubDockerHostAccessor(daemon.Endpoint),
            NullLogger<ImageService>.Instance, credentials);
        var result = await images.PullAsync("local", new PullImageRequest { Image = "private.example/app", Tag = "v1" });
        result.Reference.Should().Be("private.example/app:v1");
        credentials.Address.Should().Be("private.example");
        if (authenticated)
        {
            header.Should().NotBeNullOrEmpty();
            var encoded = header!.Replace('-', '+').Replace('_', '/');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            using var auth = JsonDocument.Parse(json);
            auth.RootElement.GetProperty("username").GetString().Should().Be("test-user");
            auth.RootElement.GetProperty("password").GetString().Should().Be("test-secret");
        }
        else
        {
            // SDK 可省略该头或发送空 JSON，但不能带上虚构凭据。
            if (!String.IsNullOrEmpty(header))
            {
                Encoding.UTF8.GetString(Convert.FromBase64String(header)).Should().NotContain("test-secret");
            }
        }
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(409, false)]
    [InlineData(500, false)]
    public async Task 删除编排_卷删除失败_仅不存在视为成功(int status, bool succeeds)
    {
        var removalStatus = status;
        await using var daemon = new FakeDockerDaemon(request =>
        {
            var path = request.Url!.AbsolutePath;
            if (request.HttpMethod == "DELETE")
            {
                return (removalStatus, "{\"message\":\"volume unavailable\"}");
            }

            return path.EndsWith("/volumes", StringComparison.Ordinal)
                ? (200, "{\"Volumes\":[{\"Name\":\"test-volume\",\"Labels\":{\"com.docker.compose.project\":\"demo\"}}]}")
                : (200, "[]");
        });
        var accessor = new StubDockerHostAccessor(daemon.Endpoint);
        var images = new ImageService(accessor, NullLogger<ImageService>.Instance, new Credentials(false));
        var executor = new StackOperationExecutor(accessor, images, new NoopProgress(),
            NullLogger<StackOperationExecutor>.Instance);
        var request = new StackOperationRequest("task", "local", "demo", StackOperations.Remove,
            null, false, false, true, null, null, null, null);
        if (succeeds)
        {
            var result = await executor.ExecuteAsync(request);
            result.VolumesRemoved.Should().Be(0);
        }
        else
        {
            var act = () => executor.ExecuteAsync(request);
            (await act.Should().ThrowAsync<BusinessException>()).Which.Code.Should()
                .Be(status == 409 ? ErrorCodes.DockerResourceConflict : ErrorCodes.DockerApiFailed);
            if (status == 409)
            {
                removalStatus = 204;
                var retried = await executor.ExecuteAsync(request);
                retried.VolumesRemoved.Should().Be(1);
            }
        }
    }

    [Theory]
    [InlineData("bridge")]
    [InlineData("host")]
    [InlineData("none")]
    [InlineData("null")]
    public async Task 删除网络_内置网络及旧版名称_禁止删除(string name)
    {
        var deletes = 0;
        await using var daemon = new FakeDockerDaemon(request =>
        {
            if (request.HttpMethod == "DELETE")
            {
                Interlocked.Increment(ref deletes);
            }

            return (200, JsonSerializer.Serialize(new
            {
                Id = "network-id",
                Name = name
            }));
        });
        var service = new NetworkService(new StubDockerHostAccessor(daemon.Endpoint), NullLogger<NetworkService>.Instance);
        var act = () => service.RemoveAsync("local", new RemoveNetworkRequest { NetworkId = "network-id" });
        (await act.Should().ThrowAsync<BusinessException>()).Which.Code.Should().Be(ErrorCodes.DockerResourceConflict);
        deletes.Should().Be(0);
    }

    private sealed class Credentials(bool enabled) : IRepositoryCredentialResolver
    {
        public string? Address
        {
            get; private set;
        }
        public Task<RepositoryCredentialValue?> ResolveCredentialAsync(string serverAddress,
            CancellationToken cancellationToken = default)
        {
            this.Address = serverAddress;
            return Task.FromResult<RepositoryCredentialValue?>(enabled
                ? new RepositoryCredentialValue("private.example", "test-user", "test-secret") : null);
        }
    }

    private sealed class NoopProgress : IOperationProgressPublisher
    {
        public ValueTask PublishAsync(OperationProgress progress, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
