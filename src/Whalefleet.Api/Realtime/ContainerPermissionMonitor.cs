using Whalefleet.Application.Containers;
using Whalefleet.Application.Common;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Api.Realtime;

/// <summary>长连接独立复核授权，避免使用请求生命周期或请求缓存。</summary>
internal static class ContainerPermissionMonitor
{
    internal static async Task RunAsync(IServiceScopeFactory scopes, Guid userId, string host, string containerId, string permission, CancellationTokenSource lifetime)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                await using var scope = scopes.CreateAsyncScope();
                var authorization = scope.ServiceProvider.GetRequiredService<ContainerAuthorizationService>();
                await authorization.EnsureForUserAsync(userId, host, containerId, permission, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }
}
