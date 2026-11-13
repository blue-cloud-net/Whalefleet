using FluentAssertions;

using Whalefleet.Core.Images;

using Xunit;

namespace Whalefleet.UnitTests.Images;

/// <summary>
/// <see cref="ImageUpdatePolicy"/> 的行为测试。
/// </summary>
/// <remarks>
/// <para><b>这些语义为什么值得逐条钉住</b>：策略的结论会直接决定「要不要重建生产
/// 容器」，而它的全部判断都建立在两个摘要的比对之上。任一规则走偏，
/// 症状是「一直说有更新」（永远不会自动应用）或「明明有新版本却说最新」
/// （自动更新形同虚设）——两者都不会报错。</para>
/// <para>覆盖的规则：
/// <list type="number">
///   <item><description>镜像引用补齐 <c>latest</c>，且端口号不被误当成标签；
/// </description></item>
///   <item><description>摘要引用（<c>repo@sha256:…</c>）被拒绝（它永不变化）；</description></item>
///   <item><description>首次检查只建立基线，不宣称「有更新」；</description></item>
///   <item><description>基线前进：远端变了 → 有更新；本地已跟上 → 推平基线；</description></item>
///   <item><description>失败不清基线（一次网络抖动不该吞掉一条真实更新）；</description></item>
///   <item><description>通知按摘要去重（同一版只提醒一次）。</description></item>
/// </list></para>
/// </remarks>
public class ImageUpdatePolicyTests
{
    private static readonly DateTime _now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string _digestA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string _digestB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("nginx", "nginx:latest")]
    [InlineData("nginx:1.27", "nginx:1.27")]
    [InlineData("harbor.local:5000/app", "harbor.local:5000/app:latest")]
    [InlineData("harbor.local:5000/app:2.1", "harbor.local:5000/app:2.1")]
    public void 创建_规范化镜像引用(string input, string expected)
    {
        var policy = ImageUpdatePolicy.Create(Guid.NewGuid(), Guid.NewGuid(), input);

        policy.ImageReference.Should().Be(expected);
    }

    [Fact]
    public void 创建_摘要引用被拒绝()
    {
        var act = () => ImageUpdatePolicy.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"nginx@{_digestA}");

        act.Should().Throw<ArgumentException>().WithMessage("*摘要引用*");
    }

    [Fact]
    public void 创建_含空白的引用被拒绝()
    {
        var act = () => ImageUpdatePolicy.Create(Guid.NewGuid(), Guid.NewGuid(), "nginx :latest");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 首次检查只建立基线_不宣称有更新()
    {
        var policy = ImageUpdatePolicy.Create(Guid.NewGuid(), Guid.NewGuid(), "nginx:1.27");

        policy.RecordProbe(_now, _digestA, localDigest: null);

        policy.HasBaseline.Should().BeTrue();
        policy.BaselineDigest.Should().Be(_digestA);
        policy.State.Should().Be(ImageUpdateState.UpToDate, "刚加上的策略不该立刻报「有更新」");
        policy.ShouldNotify(_digestA).Should().BeFalse();
    }

    [Fact]
    public void 远端变化_结论是有更新且需要通知()
    {
        var policy = NewPolicyWithBaseline();

        policy.RecordProbe(_now, _digestB, localDigest: null);

        policy.State.Should().Be(ImageUpdateState.UpdateAvailable);
        policy.BaselineDigest.Should().Be(_digestA, "基线只在「应用成功」或「本地已跟上」时前进");
        policy.ShouldNotify(_digestB).Should().BeTrue();
    }

    [Fact]
    public void 同一摘要只通知一次()
    {
        var policy = NewPolicyWithBaseline();
        policy.RecordProbe(_now, _digestB, localDigest: null);

        policy.MarkNotified(_digestB);

        policy.ShouldNotify(_digestB).Should().BeFalse("扫描是周期性的，同一版不该反复提醒");
        policy.ShouldNotify(_digestA).Should().BeFalse("旧摘要已经不在状态上");

        // 再检查一次同一摘要：状态会重算，但去重记录仍在。
        policy.RecordProbe(_now.AddMinutes(5), _digestB, localDigest: null);

        policy.ShouldNotify(_digestB).Should().BeFalse();
    }

    [Fact]
    public void 本地已跟上远端_基线前进并回到最新()
    {
        var policy = NewPolicyWithBaseline();
        policy.RecordProbe(_now, _digestB, localDigest: null);
        policy.State.Should().Be(ImageUpdateState.UpdateAvailable);

        // 有人手工 docker pull 过了：本地已经有了新摘要。
        policy.RecordProbe(_now.AddMinutes(1), _digestB, _digestB);

        policy.State.Should().Be(ImageUpdateState.UpToDate);
        policy.BaselineDigest.Should().Be(_digestB);
    }

    [Fact]
    public void 检查失败_保留基线且状态为失败()
    {
        var policy = NewPolicyWithBaseline();

        policy.RecordFailure(_now, "仓库不可达");

        policy.State.Should().Be(ImageUpdateState.Failed);
        policy.LastMessage.Should().Be("仓库不可达");
        policy.BaselineDigest.Should().Be(_digestA, "一次网络抖动不该清掉用户确认过的版本");
        policy.ShouldNotify(_digestB).Should().BeFalse("失败本身不产生「有更新」的结论");
    }

    [Fact]
    public void 应用成功_基线推到已应用的摘要()
    {
        var policy = NewPolicyWithBaseline();
        policy.RecordProbe(_now, _digestB, localDigest: null);

        policy.RecordApplied(_now.AddMinutes(2), _digestB, "重建了 2 个容器");

        policy.State.Should().Be(ImageUpdateState.Applied);
        policy.BaselineDigest.Should().Be(_digestB);
        policy.LastAppliedAt.Should().Be(_now.AddMinutes(2));
        policy.LastMessage.Should().Be("重建了 2 个容器");
    }

    [Fact]
    public void 停用后不再通知()
    {
        var policy = NewPolicyWithBaseline();
        policy.RecordProbe(_now, _digestB, localDigest: null);

        policy.SetEnabled(false);

        policy.ShouldNotify(_digestB).Should().BeFalse("停用意味着「别再提醒我」");
    }

    [Fact]
    public void 状态与模式的文本形态可往返()
    {
        foreach (var state in Enum.GetValues<ImageUpdateState>())
        {
            ImageUpdateStateText.Parse(ImageUpdateStateText.ToText(state)).Should().Be(state);
        }

        foreach (var mode in Enum.GetValues<ImageUpdateMode>())
        {
            ImageUpdateModeText.Parse(ImageUpdateModeText.ToText(mode)).Should().Be(mode);
        }
    }

    /// <summary>
    /// 造一条已建立基线的策略。
    /// </summary>
    /// <returns>策略实体。</returns>
    private static ImageUpdatePolicy NewPolicyWithBaseline()
    {
        var policy = ImageUpdatePolicy.Create(Guid.NewGuid(), Guid.NewGuid(), "nginx:1.27");
        policy.RecordProbe(_now, _digestA, localDigest: null);

        return policy;
    }
}
