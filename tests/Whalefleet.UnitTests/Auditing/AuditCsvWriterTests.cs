using Whalefleet.Application.Auditing;
using Whalefleet.Core.Auditing;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Auditing;

/// <summary>
/// 审计 CSV 序列化（0.9.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么要单测</b>：导出的错误都是**静默**的——多一个逗号让整行错位、
/// 少一层引号让字段被拆开、缺 BOM 让 Excel 把中文显示成乱码。
/// 三种都不会报错，只会在使用者那边表现为「这个功能不能用」。</para>
/// </remarks>
public class AuditCsvWriterTests
{
    private static AuditLog CreateLog(
        AuditCategory category = AuditCategory.Activity,
        string actorName = "admin",
        string? targetName = "nginx",
        string? detail = null,
        AuditOutcome outcome = AuditOutcome.Success,
        int? errorCode = null)
    {
        return AuditLog.Record(
            Guid.NewGuid(),
            new AuditLogDraft
            {
                Category = category,
                Action = AuditActions.ContainerStart,
                Outcome = outcome,
                OccurredAt = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc),
                ActorId = Guid.NewGuid(),
                ActorName = actorName,
                TargetType = AuditTargetTypes.Container,
                TargetId = "abc",
                TargetName = targetName,
                HostName = "local",
                ErrorCode = errorCode,
                ClientIp = "192.168.1.10",
                Detail = detail,
            });
    }

    [Fact]
    public void 表头与页面列一致_活动日志十列()
    {
        AuditCsvWriter.HeaderFor(AuditCategory.Activity).Should().HaveCount(10);
        AuditCsvWriter.HeaderFor(AuditCategory.Authentication).Should().HaveCount(7);
    }

    [Fact]
    public void 含逗号的单元格必须加引号()
    {
        AuditCsvWriter.Escape("a,b").Should().Be("\"a,b\"");
    }

    [Fact]
    public void 引号要成对翻倍()
    {
        AuditCsvWriter.Escape("say \"hi\"").Should().Be("\"say \"\"hi\"\"\"");
    }

    [Fact]
    public void 换行必须加引号_否则整行会断成两条()
    {
        AuditCsvWriter.Escape("line1\nline2").Should().Be("\"line1\nline2\"");
    }

    [Fact]
    public void 普通文本不加引号()
    {
        AuditCsvWriter.Escape("container.start").Should().Be("container.start");
    }

    [Fact]
    public void 活动日志一行十列_错误码为空时留空()
    {
        var row = AuditCsvWriter.BuildRow(AuditCategory.Activity, CreateLog());

        row.Split(',').Should().HaveCount(10);
        row.Should().StartWith("2026-10-30 09:00:00,admin,container.start,container,nginx,local,success,");
    }

    [Fact]
    public void 认证日志一行七列()
    {
        var log = CreateLog(
            category: AuditCategory.Authentication,
            actorName: "nobody",
            targetName: null,
            outcome: AuditOutcome.Failure,
            errorCode: 1100);

        var row = AuditCsvWriter.BuildRow(AuditCategory.Authentication, log);

        row.Split(',').Should().HaveCount(7);
        row.Should().Contain("nobody,container.start") // 夹具里的动作就是 container.start
            .And.Contain("failure")
            .And.Contain("1100");
    }

    [Fact]
    public async Task 写出的内容以BOM开头_且行数等于表头加记录数()
    {
        var logs = new[] { CreateLog(), CreateLog(targetName: "other") };

        using var writer = new StringWriter();
        await AuditCsvWriter.WriteAsync(writer, AuditCategory.Activity, logs);

        var text = writer.ToString();

        text.Should().StartWith(AuditCsvWriter.Bom, "Excel 靠 BOM 判定 UTF-8，缺了中文列头会乱码");

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1 + logs.Length);
    }
}
