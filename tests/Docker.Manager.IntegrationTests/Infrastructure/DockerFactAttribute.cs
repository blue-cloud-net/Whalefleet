using Xunit;

namespace Docker.Manager.IntegrationTests.Infrastructure;

/// <summary>
/// 「需要真实 Docker 守护进程」的用例标注。
/// </summary>
/// <remarks>
/// <para>宿主 socket 不可达时把用例标记为 <b>Skip</b> 而不是让它们失败：CI 与开发机不一定都有可用的
/// Docker，而"这条用例没跑"与"这条用例挂了"是两件必须能区分的事——混在一起会让人开始忽略红灯。</para>
/// <para>用法与 <c>[Fact]</c> 完全一致：</para>
/// <code>
/// [DockerFact]
/// public async Task 列出容器() { ... }
/// </code>
/// <para><b>注意</b>：跳过是<b>按探测结果</b>发生的，因此同一个用例在不同机器上可能跑也可能跳；
/// 不要用它来标注"慢用例"，只用于"依赖真实守护进程"。</para>
/// </remarks>
public sealed class DockerFactAttribute : FactAttribute
{
    /// <summary>
    /// 初始化特性，并在守护进程不可用时给出跳过原因。
    /// </summary>
    public DockerFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            this.Skip = DockerAvailability.SkipReason;
        }
    }
}

/// <summary>
/// <see cref="DockerFactAttribute"/> 的理论版（多组数据）。
/// </summary>
/// <remarks>与 <see cref="DockerFactAttribute"/> 同理，只是承载 <see cref="InlineDataAttribute"/> 的数据。</remarks>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    /// <summary>
    /// 初始化特性，并在守护进程不可用时给出跳过原因。
    /// </summary>
    public DockerTheoryAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            this.Skip = DockerAvailability.SkipReason;
        }
    }
}
