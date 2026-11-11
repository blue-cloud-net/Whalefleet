using System.Runtime.CompilerServices;

using Docker.DotNet;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Whalefleet.Api.Realtime;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Alerting;
using Whalefleet.Core.Alerting;
using Whalefleet.Core.Hosts;

using Xunit;

namespace Whalefleet.IntegrationTests.Realtime;

/// <summary>
/// 单条事件解析失败不能结束全局容器通知派发。
/// </summary>
public sealed class EventDispatchRecoveryTests
{
    [Fact]
    public async Task 派发_第一条主机解析失败_仍处理下一条()
    {
        var accessor = new IntermittentAccessor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDockerHostAccessor>(accessor);
        services.AddSingleton<IPermissionChecker, NoPermissions>();
        services.AddSingleton<INotificationWriter, NoopWriter>();
        using var provider = services.BuildServiceProvider();
        using var dispatcher = new ContainerNotificationDispatcher(
            new Events(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContainerNotificationDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);
        await dispatcher.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        accessor.Attempts.Should().Be(2);
    }

    private sealed class Events : IContainerEventBus
    {
        public ValueTask PublishAsync(ContainerEventNotification notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ContainerEventNotification> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new("local", "first", "first", 1, false, DateTimeOffset.UtcNow);
            yield return new("local", "second", "second", 1, false, DateTimeOffset.UtcNow);
        }
    }

    private sealed class IntermittentAccessor : IDockerHostAccessor
    {
        public int Attempts
        {
            get; private set;
        }
        public Task<DockerHost> ResolveAsync(string hostId, CancellationToken cancellationToken = default)
        {
            this.Attempts++;
            return this.Attempts == 1
                ? Task.FromException<DockerHost>(new IOException("transient"))
                : Task.FromResult(DockerHost.Register(Guid.NewGuid(), "local", DockerHostType.Socket,
                    new HostEndpoint("/var/run/docker.sock")));
        }

        public Task<IDockerClient> GetClientAsync(string hostId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> TestConnectionAsync(DockerHostConnection connection, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void Invalidate(DockerHostConnection connection) => throw new NotSupportedException();
    }

    private sealed class NoopWriter : INotificationWriter
    {
        public Task<Notification> CreateAsync(NotificationDraft draft, CancellationToken cancellationToken = default) =>
            Task.FromResult(Notification.Create(
                Guid.NewGuid(),
                draft.SourceKind,
                draft.Severity,
                draft.MessageKey,
                "{}",
                draft.HostId,
                draft.HostName,
                draft.DedupeKey,
                draft.OccurredAt));
    }

    private sealed class NoPermissions : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(Guid userId, Guid? hostId, string permission, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
        public Task EnsurePermissionAsync(Guid userId, Guid? hostId, string permission, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(Guid userId, Guid? hostId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<DockerHost>> FilterVisibleHostsAsync(Guid userId, IReadOnlyList<DockerHost> hosts, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> IsAdministratorAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
