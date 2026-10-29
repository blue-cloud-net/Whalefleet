using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Core.Auditing;

namespace Whalefleet.UnitTests.Containers;

/// <summary>
/// 不做任何事的审计写入器（单测用）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：0.9.0 起用例服务的构造依赖 <see cref="IAuditLogger"/>。
/// 单测关心的是查询语义（端口排序、搜索、分页），审计去写哪里与它们无关。</para>
/// <para><b>为什么不 mock 框架</b>：本项目的单测不引 mocking 库，
/// 一个三行的空实现比引一个包更省事，也更容易看出「这里没验证审计」。</para>
/// <para><b>它额外记录调用</b>，以便需要时断言「这条路径确实埋了点」——
/// 但默认不参与任何断言。</para>
/// </remarks>
internal sealed class NoopAuditLogger : IAuditLogger
{
    private readonly List<AuditLogDraft> _recorded = [];

    /// <summary>已记录的草稿（按调用顺序）。</summary>
    public IReadOnlyList<AuditLogDraft> Recorded => _recorded;

    /// <inheritdoc />
    public Task RecordAsync(AuditLogDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        _recorded.Add(draft);

        return Task.CompletedTask;
    }
}
