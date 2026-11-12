using System.Collections.Concurrent;

namespace Whalefleet.Application.Containers;

/// <summary>同一主机的授权变更与平台重建串行，避免旧 ID 授权遗漏。</summary>
public sealed class ContainerAssignmentCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>获取主机级互斥租约；调用者在所有退出路径释放。</summary>
    public async Task<IDisposable> AcquireAsync(string hostName, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(hostName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }
    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Dispose();
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            gate.Release();
        }
    }
}
