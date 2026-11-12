namespace Whalefleet.Application.Abstractions;

/// <summary>实例级容器授权策略存储。</summary>
public interface IAuthorizationSettingsStore
{
    /// <summary>读取模式：inheritHost 或 requireAssignment。</summary>
    Task<string> GetModeAsync(CancellationToken cancellationToken = default);
    /// <summary>保存已校验的模式。</summary>
    Task SetModeAsync(string mode, CancellationToken cancellationToken = default);
}
