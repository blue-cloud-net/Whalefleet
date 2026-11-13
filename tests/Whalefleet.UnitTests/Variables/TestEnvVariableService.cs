using Microsoft.Extensions.Logging.Abstractions;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Variables;
using Whalefleet.Core.Variables;

using Whalefleet.UnitTests.Containers;

namespace Whalefleet.UnitTests.Variables;

/// <summary>
/// 构造测试用的 <see cref="EnvVariableService"/>。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：0.15.0 起编排执行器与用例都依赖环境变量服务，
/// 而单测关心的是别的语义（卷删除幂等、端口排序、搜索、分页）——
/// 变量表在那些用例里恒为空。</para>
/// <para><b>为什么不用 mocking 库</b>：本项目的单测不引 mocking 库，
/// 三个十几行的空实现比引一个包更省事。</para>
/// </remarks>
internal static class TestEnvVariableService
{
    /// <summary>
    /// 构造一个变量表为空的 <see cref="EnvVariableService"/>。
    /// </summary>
    /// <param name="repository">变量仓储；省略时用空实现。</param>
    /// <returns>环境变量用例。</returns>
    internal static EnvVariableService Create(IEnvVariableRepository? repository = null) =>
        new(
            repository ?? new EmptyEnvVariableRepository(),
            new PassThroughSecretProtector(),
            new NoopAuditLogger(),
            NullLogger<EnvVariableService>.Instance);
}

/// <summary>
/// 空的变量仓储（单测用）。
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
/// 直通式的敏感文本保护器（单测用）。
/// </summary>
/// <remarks>
/// <b>它不是加密</b>：只是加个前缀以便断言「存进去的不是明文」。
/// 单测不需要真正的密文强度，需要的是「值不落库成明文」这个性质。
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
