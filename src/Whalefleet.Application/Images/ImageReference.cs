namespace Whalefleet.Application.Images;

/// <summary>
/// 镜像引用（<c>仓库:标签</c>）的解析规则。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个类</b>：这两条规则（标签在哪、仓库前缀在哪）
/// 既被推送用例用，也被前端要显示的引用形态影响，因此它们是<b>可复用的纯逻辑</b>，
/// 而不是某个方法的私有细节。放在这里既能被用例调用，也能被单测直接覆盖。</para>
/// <para><b>判据必须与 Docker 一致</b>：Docker 自己的规则是
/// 「冒号是否在<b>最后一个斜杠之后</b>」才算标签（端口号在斜杠左边），
/// 「第一段含 <c>.</c> 或 <c>:</c>、或整段等于 <c>localhost</c>」才算 仓库前缀。
/// 照搬 Docker 的判据比「自己想一套」可靠得多。</para>
/// </remarks>
public static class ImageReference
{
    /// <summary>
    /// 默认仓库（Docker Hub）。
    /// </summary>
    public const string DockerHubServerAddress = "registry-1.docker.io";

    /// <summary>
    /// 把「仓库名:标签」拆成两段；没有标签时补 <c>latest</c>。
    /// </summary>
    /// <param name="reference">镜像引用。</param>
    /// <returns>（仓库名，标签）。</returns>
    /// <remarks>
    /// 判据与 Docker 一致：冒号是否在<b>最后一个斜杠之后</b>才是标签。
    /// 因此 <c>harbor.local:5000/app</c> 的 <c>:5000</c> 是<b>端口</b>而非标签，
    /// 拆出来的是 <c>("harbor.local:5000/app", "latest")</c>。
    /// </remarks>
    public static (string Repository, string Tag) Split(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var name = reference.Trim().TrimEnd(':');

        // 用 char 重载：LastIndexOf(char) 没有接受 StringComparison 的版本
        // （只有 IndexOf 有），而传字符串重载会被 CA1865 提示改回 char。
        var colon = name.LastIndexOf(':');

        if (colon <= name.LastIndexOf('/'))
        {
            return (name, "latest");
        }

        return (name[..colon], name[(colon + 1)..]);
    }

    /// <summary>
    /// 从仓库名里切出 仓库地址段。
    /// </summary>
    /// <param name="repository">仓库名（如 <c>nginx</c>、<c>harbor.local:5000/app</c>）。</param>
    /// <returns>地址段；<c>nginx</c> 这类无前缀的仓库返回 Docker Hub。</returns>
    /// <remarks>
    /// <b>判据与 Docker 一致</b>：只有「第一段里含 <c>.</c> 或 <c>:</c>，或整段等于 <c>localhost</c>」
    /// 才算 仓库前缀。否则 <c>org/app</c> 这样的官方镜像命名空间会被误当成 仓库主机——
    /// 推镜像时会因此去连一个不存在的主机。
    /// </remarks>
    public static string ExtractServerAddress(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        var slash = repository.IndexOf('/', StringComparison.Ordinal);

        if (slash <= 0)
        {
            return DockerHubServerAddress;
        }

        var prefix = repository[..slash];

        var looksLikeRegistry =
            prefix.Contains('.', StringComparison.Ordinal)
            || prefix.Contains(':', StringComparison.Ordinal)
            || string.Equals(prefix, "localhost", StringComparison.OrdinalIgnoreCase);

        return looksLikeRegistry ? prefix.ToLowerInvariant() : DockerHubServerAddress;
    }
}
