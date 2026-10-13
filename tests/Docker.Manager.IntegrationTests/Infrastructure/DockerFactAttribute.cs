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

/// <summary>
/// 「需要真实 Docker 守护进程<b>并且</b>宿主上至少有一张现成镜像」的用例标注。
/// </summary>
/// <remarks>
/// <para>用于需要<b>创建</b>容器的用例：创建必须指定镜像，而镜像有两种来法——本地已有，或者去拉。
/// 后者会让用例依赖网络与镜像仓库，与「验证本项目的创建参数映射」这个目标无关，因此这里只接受本地现成镜像。</para>
/// <para>跳过条件比 <see cref="DockerFactAttribute"/> 多一条（没有镜像也跳），原因写在
/// <see cref="DockerAvailability.ImageSkipReason"/>："没镜像"与"用例挂了"必须能区分。</para>
/// </remarks>
public sealed class DockerImageFactAttribute : FactAttribute
{
    /// <summary>
    /// 初始化特性，并在守护进程不可用或本地无镜像时给出跳过原因。
    /// </summary>
    public DockerImageFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            this.Skip = DockerAvailability.SkipReason;
        }
        else if (DockerAvailability.ImageReference is null)
        {
            this.Skip = DockerAvailability.ImageSkipReason;
        }
    }
}

/// <summary>
/// 「需要真实 Docker 守护进程<b>并且</b>能连上镜像仓库」的用例标注。
/// </summary>
/// <remarks>
/// 用于<b>拉取</b>镜像的成功路径：拉取必然要出网，而本组还有一条验证「拉取失败路径」的用例——
/// 那条不能联网（它要证明的就是失败）。两者若共用一个特性，离线时会一起跳过；
/// 分开之后离线时仍能验证失败路径，而成功路径的缺失是已知且已记录的。
/// </remarks>
public sealed class DockerRegistryFactAttribute : FactAttribute
{
    /// <summary>
    /// 初始化特性，并在守护进程不可用或镜像仓库不可达时给出跳过原因。
    /// </summary>
    public DockerRegistryFactAttribute()
    {
        if (!DockerAvailability.IsAvailable)
        {
            this.Skip = DockerAvailability.SkipReason;
        }
        else if (!DockerAvailability.IsRegistryReachable)
        {
            this.Skip = DockerAvailability.RegistrySkipReason;
        }
    }
}
