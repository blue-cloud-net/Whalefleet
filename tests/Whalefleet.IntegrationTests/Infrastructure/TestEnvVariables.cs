using Microsoft.Extensions.Logging.Abstractions;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Variables;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Variables;

namespace Whalefleet.IntegrationTests.Infrastructure;

/// <summary>
/// 构造测试用的 <see cref="EnvVariableService"/>（变量表恒为空）。
/// </summary>
/// <remarks>
/// <para><b>为什么集成测试不直接用容器解析</b>：有的回归测试手工 <c>new</c> 用例
/// （要注入会失败的仓储），拿不到 DI 容器。这里提供一份最小的组装，
/// 让那些测试不必为「变量服务」再补一套依赖。</para>
/// <para><b>为什么变量表恒为空</b>：这些测试关心的是别的语义
/// （写库失败后的撤销登记、未知异常的脱敏）。变量为空时插值不做任何替换，
/// 与它们的被测行为无关。</para>
/// </remarks>
internal static class TestEnvVariables
{
    /// <summary>
    /// 构造一个变量表为空的 <see cref="EnvVariableService"/>。
    /// </summary>
    /// <returns>环境变量用例。</returns>
    internal static EnvVariableService Create() =>
        new(
            new EmptyEnvVariableRepository(),
            new PassThroughSecretProtector(),
            new NoopAuditWriter(),
            NullLogger<EnvVariableService>.Instance);
}

/// <summary>
/// 空的变量仓储（测试用）。
/// </summary>
internal sealed class EmptyEnvVariableRepository : IEnvVariableRepository
{
    /// <inheritdoc />
    public Task<EnvVariable?> QueryByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<EnvVariable?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<EnvVariable>> QueryAllListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EnvVariable>>([]);

    /// <inheritdoc />
    public Task AddAsync(EnvVariable entity, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<bool> UpdateAsync(EnvVariable entity, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public Task<bool> RemoveByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public Task<EnvVariable?> FindByScopeAsync(
        Guid? hostId,
        string? composeName,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<EnvVariable?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<EnvVariable>> ListGlobalAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EnvVariable>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<EnvVariable>> ListByProjectAsync(
        Guid hostId,
        string composeName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EnvVariable>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<EnvVariable>> ListVisibleAsync(
        Guid hostId,
        string composeName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EnvVariable>>([]);
}

/// <summary>
/// 直通式的敏感文本保护器（测试用）。
/// </summary>
/// <remarks>
/// <b>不是加密</b>：只加前缀，用来断言「存进去的不是明文」。
/// </remarks>
internal sealed class PassThroughSecretProtector : ISecretProtector
{
    private const string _prefix = "enc:";

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        return _prefix + plaintext;
    }

    /// <inheritdoc />
    public string Unprotect(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);

        return protectedText.StartsWith(_prefix, StringComparison.Ordinal)
            ? protectedText[_prefix.Length..]
            : protectedText;
    }
}

/// <summary>
/// 不做任何事的审计写入器（测试用）。
/// </summary>
internal sealed class NoopAuditWriter : IAuditLogger
{
    /// <inheritdoc />
    public Task RecordAsync(AuditLogDraft draft, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
