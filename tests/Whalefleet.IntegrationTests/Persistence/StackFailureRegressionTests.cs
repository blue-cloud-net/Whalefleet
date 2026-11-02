using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Application.Stacks;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Stacks;

using Xunit;

using StackEntity = Whalefleet.Core.Stacks.Stack;

namespace Whalefleet.IntegrationTests.Persistence;

/// <summary>
/// 任务受理失败的补偿以及后台未知异常的审计与继续执行。
/// </summary>
public sealed class StackFailureRegressionTests
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
        var stack = StackEntity.Create(Guid.NewGuid(), local.Id, "demo", "services: { app: { image: nginx } }");
        var repository = new StackRepository(stack) { FailUpdate = writeFails };
        var registry = new StackTaskRegistry();
        var queue = new StackOperationQueue();
        if (!writeFails)
        {
            queue.Complete();
        }

        var limiter = new StackWebhookRateLimiter(TimeProvider.System);
        var service = new StackWebhookService(repository, hosts, registry, queue, TimeProvider.System,
            NullLogger<StackWebhookService>.Instance, limiter);
        var act = () => service.TriggerAsync("test-token", new StackWebhookReloadRequest());
        await act.Should().ThrowAsync<Exception>();
        registry.GetSnapshot().Should().BeEmpty();

        repository.FailUpdate = false;
        var retry = new StackWebhookService(repository, hosts, registry, new StackOperationQueue(),
            TimeProvider.System, NullLogger<StackWebhookService>.Instance, limiter);
        var accepted = await retry.TriggerAsync("test-token", new StackWebhookReloadRequest());
        registry.Get(accepted.TaskId).Should().NotBeNull();
    }

    [Fact]
    public async Task 后台任务_未知异常_审计和通知脱敏并继续下一任务()
    {
        var audit = new AuditCollector();
        var notifier = new FailureCollector();
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLogger>(audit);
        services.AddSingleton<IStackTaskNotifier>(notifier);
        services.AddScoped<StackOperationExecutor>(_ => throw new IOException("secret-path-password"));
        using var provider = services.BuildServiceProvider();
        var queue = new StackOperationQueue();
        var registry = new StackTaskRegistry();
        foreach (var id in new[] { "first", "second" })
        {
            registry.TryRegister(id, "local", id, StackOperations.Deploy, DateTime.UtcNow);
            queue.TryEnqueue(new StackOperationRequest(id, "local", id, StackOperations.Deploy,
                null, false, false, false, null, null, null, null)).Should().BeTrue();
        }

        queue.Complete();
        using var worker = new StackOperationWorker(queue, registry,
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<StackOperationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var id in new[] { "first", "second" })
        {
            var snapshot = registry.Get(id)!;
            snapshot.Status.Should().Be(StackTaskStatusText.Failed);
            snapshot.ErrorMessage.Should().NotContain("secret-path-password");
        }

        audit.Drafts.Should().HaveCount(2);
        audit.Drafts.Should().OnlyContain(draft => draft.Outcome == AuditOutcome.Failure
            && draft.ErrorCode == ErrorCodes.StackDeployFailed);
        notifier.Messages.Should().HaveCount(2);
        notifier.Messages.Should().OnlyContain(message => !message.Message.Contains("secret-path-password", StringComparison.Ordinal));
    }

    private sealed class StackRepository(StackEntity stack) : IStackRepository
    {
        public bool FailUpdate
        {
            get; set;
        }
        public Task<StackEntity?> FindByWebhookTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
            => Task.FromResult<StackEntity?>(stack);
        public Task<bool> UpdateAsync(StackEntity entity, CancellationToken cancellationToken = default)
            => this.FailUpdate ? Task.FromException<bool>(new IOException("write failed")) : Task.FromResult(true);
        public Task<StackEntity?> FindByHostAndNameAsync(Guid hostId, string name, CancellationToken cancellationToken = default)
            => Task.FromResult<StackEntity?>(stack);
        public Task<IReadOnlyList<StackEntity>> ListByHostAsync(Guid hostId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StackEntity>>([stack]);
        public Task<StackEntity?> QueryByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<StackEntity?>(stack);
        public Task<IReadOnlyList<StackEntity>> QueryAllListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StackEntity>>([stack]);
        public Task AddAsync(StackEntity entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    private sealed class FailureCollector : IStackTaskNotifier
    {
        public List<StackTaskFailureNotification> Messages { get; } = [];
        public Task NotifyFailedAsync(StackTaskFailureNotification notification, CancellationToken cancellationToken = default)
        {
            this.Messages.Add(notification);
            return Task.CompletedTask;
        }
    }
}
