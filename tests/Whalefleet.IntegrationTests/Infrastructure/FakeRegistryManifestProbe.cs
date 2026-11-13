using Whalefleet.Application.Abstractions;

namespace Whalefleet.IntegrationTests.Infrastructure;

/// <summary>
/// 仓库清单探测器的可控替身（测试用）。
/// </summary>
/// <remarks>
/// <para><b>为什么不打真仓库</b>：真实实现会向 Docker Hub 或用户自建仓库发 HTTPS
/// 请求。集成测试依赖公网就变成了「今天网络好不好」的检测器，而断网、限流、
/// 镜像被删都会让它们变红——它们要验的与这些毫无关系。</para>
/// <para><b>为什么按「仓库」而不是按「标签」预设</b>：用例要验的是
/// 「摘要变了 → 报有更新」，摘要本身就是返回值，标签只影响请求参数。</para>
/// </remarks>
internal sealed class FakeRegistryManifestProbe : IRegistryManifestProbe
{
    private readonly Dictionary<string, RegistryProbeResult> _results = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 记录每次探测的「仓库:标签」，供断言调用次数与参数。
    /// </summary>
    public List<string> Calls { get; } = [];

    /// <summary>
    /// 让某个仓库返回固定摘要。
    /// </summary>
    /// <param name="repository">仓库名。</param>
    /// <param name="digest">摘要。</param>
    /// <returns>当前实例，便于链式调用。</returns>
    public FakeRegistryManifestProbe WithDigest(string repository, string digest)
    {
        _results[repository] = new RegistryProbeResult(RegistryProbeStatus.Digest, digest);

        return this;
    }

    /// <summary>
    /// 让某个仓库返回失败结论。
    /// </summary>
    /// <param name="repository">仓库名。</param>
    /// <param name="status">结论分类。</param>
    /// <param name="message">说明。</param>
    /// <returns>当前实例，便于链式调用。</returns>
    public FakeRegistryManifestProbe WithFailure(
        string repository,
        RegistryProbeStatus status,
        string message)
    {
        _results[repository] = new RegistryProbeResult(status, Message: message);

        return this;
    }

    /// <inheritdoc />
    public Task<RegistryProbeResult> ProbeAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add($"{repository}:{tag}");
        }

        return Task.FromResult(
            _results.TryGetValue(repository, out var result)
                ? result
                : new RegistryProbeResult(
                    RegistryProbeStatus.NotFound,
                    Message: "仓库里没有这个镜像或标签"));
    }
}
