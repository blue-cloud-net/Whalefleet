using Whalefleet.Application.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Auditing;

/// <summary>
/// 审计查询条件的构造（0.9.0）。
/// </summary>
/// <remarks>
/// 这段逻辑决定「界面不填时间时到底查了哪一段」，而它错了**不会报错**——
/// 只会让人以为「那段时间没有记录」。
/// </remarks>
public class AuditLogFilterBuilderTests
{
    private static readonly DateTime _now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 不传时间时默认最近七天()
    {
        var filter = AuditLogFilterBuilder.Build(
            AuditCategory.Activity,
            new ListAuditLogsRequest(),
            hostId: null,
            _now);

        filter.To.Should().Be(_now);
        filter.From.Should().Be(_now.AddDays(-AuditLogFilterBuilder.DefaultWindowDays));
    }

    [Fact]
    public void 只给结束时间时_起点按结束时间往前推七天()
    {
        var to = _now.AddDays(-1);

        var filter = AuditLogFilterBuilder.Build(
            AuditCategory.Activity,
            new ListAuditLogsRequest { To = to },
            hostId: null,
            _now);

        filter.To.Should().Be(to);
        filter.From.Should().Be(to.AddDays(-7));
    }

    [Fact]
    public void 时间区间颠倒时报1000而不是静默交换()
    {
        var act = () => AuditLogFilterBuilder.Build(
            AuditCategory.Activity,
            new ListAuditLogsRequest { From = _now, To = _now.AddDays(-1) },
            hostId: null,
            _now);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void 不带时区的时间按UTC解释()
    {
        var unspecified = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Unspecified);

        var filter = AuditLogFilterBuilder.Build(
            AuditCategory.Activity,
            new ListAuditLogsRequest { From = unspecified, To = _now },
            hostId: null,
            _now);

        filter.From.Should().Be(DateTime.SpecifyKind(unspecified, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("success", AuditOutcome.Success)]
    [InlineData("DENIED", AuditOutcome.Denied)]
    [InlineData(" failure ", AuditOutcome.Failure)]
    public void 结果取值大小写与空白都能认(string value, AuditOutcome expected)
    {
        AuditLogFilterBuilder.ParseOutcome(value).Should().Be(expected);
    }

    [Fact]
    public void 结果取值省略或空串表示不筛选()
    {
        AuditLogFilterBuilder.ParseOutcome(null).Should().BeNull();
        AuditLogFilterBuilder.ParseOutcome("  ").Should().BeNull();
    }

    [Fact]
    public void 不认识的结果取值报1000_而不是忽略()
    {
        var act = () => AuditLogFilterBuilder.ParseOutcome("ok");

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void 空白关键字归一为不筛选()
    {
        var filter = AuditLogFilterBuilder.Build(
            AuditCategory.Activity,
            new ListAuditLogsRequest { Search = "   ", Actor = " ", TargetType = "" },
            hostId: null,
            _now);

        filter.Search.Should().BeNull();
        filter.ActorName.Should().BeNull();
        filter.TargetType.Should().BeNull();
    }

    [Fact]
    public void 主机作用域会被带进条件()
    {
        var hostId = Guid.NewGuid();

        var filter = AuditLogFilterBuilder.Build(
            AuditCategory.Activity,
            new ListAuditLogsRequest(),
            hostId,
            _now);

        filter.HostId.Should().Be(hostId);
    }
}
