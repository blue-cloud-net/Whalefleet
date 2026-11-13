using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Application.Composes;
using Whalefleet.Application.Webhooks;
using Whalefleet.IntegrationTests.Infrastructure;
using Whalefleet.Application.Operations;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Composes;

using Xunit;

using ComposeProjectEntity = Whalefleet.Core.Composes.ComposeProject;

namespace Whalefleet.IntegrationTests.Persistence;

/// <summary>
/// 任务受理失败的补偿以及后台未知异常的审计与继续执行。
/// </summary>
public sealed class ComposeFailureRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Webhook_写库或入队失败_撤销登记并允许重试(bool writeFails)
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();
        var local = (await hosts.FindByNameAsync("local"))!;
        var compose = ComposeProjectEntity.Create(Guid.NewGuid(), local.Id, "demo", "services: { app: { image: nginx } }");
        var repository = new ComposeProjectRepository(compose) { FailUpdate = writeFails };
        var registry = new ResourceTaskRegistry<ComposeOperationResult>();
        var queue = new ResourceOperationQueue();
        if (!writeFails)
        {
            queue.Complete();
        }

        var limiter = new WebhookRateLimiter(TimeProvider.System);
        var variables = TestEnvVariables.Create();
        var service = new ComposeWebhookService(repository, hosts, variables, registry, queue, TimeProvider.System,
            NullLogger<ComposeWebhookService>.Instance, limiter);
        var act = () => service.TriggerAsync("test-token", new ComposeWebhookReloadRequest());
        await act.Should().ThrowAsync<Exception>();
        registry.GetSnapshot().Should().BeEmpty();

        repository.FailUpdate = false;
        var retry = new ComposeWebhookService(repository, hosts, variables, registry, new ResourceOperationQueue(),
            TimeProvider.System, NullLogger<ComposeWebhookService>.Instance, limiter);
        var accepted = await retry.TriggerAsync("test-token", new ComposeWebhookReloadRequest());
        registry.Get(accepted.TaskId).Should().NotBeNull();
    }

    [Fact]
    public async Task 后台任务_未知异常_审计和通知脱敏并继续下一任务()
    {
        var audit = new AuditCollector();
        var notifier = new FailureCollector();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLogger>(audit);
        services.AddSingleton<IComposeTaskNotifier>(notifier);
        services.AddScoped<ComposeOperationExecutor>(_ => throw new IOException("secret-path-password"));
        using var provider = services.BuildServiceProvider();
        var queue = new ResourceOperationQueue();
        var registry = new ResourceTaskRegistry<ComposeOperationResult>();
        foreach (var id in new[] { "first", "second" })
        {
            registry.TryRegister(id, "local", ResourceKinds.Compose, id, ComposeOperations.Deploy, DateTime.UtcNow);
            queue.TryEnqueue(new ResourceOperationRequest(id, "local", ResourceKinds.Compose, id, ComposeOperations.Deploy,
                null, false, false, false, null, null, null, null)).Should().BeTrue();
        }

        queue.Complete();
        using var worker = new ResourceOperationWorker(queue, registry,
            new ResourceTaskRegistry<ContainerRecreateResult>(),
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<ResourceOperationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var id in new[] { "first", "second" })
        {
            var snapshot = registry.Get(id)!;
            snapshot.Status.Should().Be(ResourceTaskStatusText.Failed);
            snapshot.ErrorMessage.Should().NotContain("secret-path-password");
        }

        audit.Drafts.Should().HaveCount(2);
        audit.Drafts.Should().OnlyContain(draft => draft.Outcome == AuditOutcome.Failure
            && draft.ErrorCode == ErrorCodes.ComposeDeployFailed);
        notifier.Messages.Should().HaveCount(2);
        notifier.Messages.Should().OnlyContain(message => !message.Message.Contains("secret-path-password", StringComparison.Ordinal));
    }

    private sealed class ComposeProjectRepository(ComposeProjectEntity compose) : IComposeProjectRepository
    {
        public bool FailUpdate
        {
            get; set;
        }
        public Task<ComposeProjectEntity?> FindByWebhookTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
            => Task.FromResult<ComposeProjectEntity?>(compose);
        public Task<bool> UpdateAsync(ComposeProjectEntity entity, CancellationToken cancellationToken = default)
            => this.FailUpdate ? Task.FromException<bool>(new IOException("write failed")) : Task.FromResult(true);
        public Task<ComposeProjectEntity?> FindByHostAndNameAsync(Guid hostId, string name, CancellationToken cancellationToken = default)
            => Task.FromResult<ComposeProjectEntity?>(compose);
        public Task<IReadOnlyList<ComposeProjectEntity>> ListByHostAsync(Guid hostId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ComposeProjectEntity>>([compose]);
        public Task<ComposeProjectEntity?> QueryByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<ComposeProjectEntity?>(compose);
        public Task<IReadOnlyList<ComposeProjectEntity>> QueryAllListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ComposeProjectEntity>>([compose]);
        public Task AddAsync(ComposeProjectEntity entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveByIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AuditCollector : IAuditLogger
    {
        public List<AuditLogDraft> Drafts { get; } = [];
        public Task RecordAsync(AuditLogDraft draft, CancellationToken cancellationToken = default)
        {
            this.Drafts.Add(draft);
            return Task.CompletedTask;
        }
    }

    private sealed class FailureCollector : IComposeTaskNotifier
    {
        public List<ComposeTaskFailureNotification> Messages { get; } = [];
        public Task NotifyFailedAsync(ComposeTaskFailureNotification notification, CancellationToken cancellationToken = default)
        {
            this.Messages.Add(notification);
            return Task.CompletedTask;
        }
    }
}
