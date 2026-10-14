using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests;

/// <summary>
/// 烟雾测试（占位用例），验证测试链路通畅 + 测试项目可引用 Core / Application。
/// </summary>
/// <remarks>
/// <para>当前 0.0.1 阶段仅一个最小断言。后续迭代（0.0.3 / 0.0.4 / 0.0.8 起）按 roadmap
/// 落地覆盖密码哈希、登录、JWT、容器操作等用例。</para>
/// </remarks>
public class SmokeTests
{
    [Fact]
    public void TestRunner_ShouldExecute()
    {
        // 极简断言：仅验证 xUnit + FluentAssertions 链路通畅
        const bool expected = true;
        expected.Should().BeTrue();
    }
}