using Xunit;

namespace Whalefleet.IntegrationTests;

/// <summary>
/// 需要修改<b>进程级环境变量</b>的测试所属的集合。
/// </summary>
/// <remarks>
/// <para><see cref="Environment.SetEnvironmentVariable(String, String?)"/> 是进程级副作用，
/// 若不与其它测试串行，会污染并发运行的宿主配置（<c>ApiFactory</c> 会在构建宿主时读取环境变量）。</para>
/// <para><c>DisableParallelization = true</c> 使本集合不与任何其它集合并行执行，
/// 从而保证环境变量的设置/还原窗口内没有其它测试在构建宿主。</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariablesCollectionDefinition
{
    /// <summary>集合名称。</summary>
    public const string Name = "EnvironmentVariables";
}
