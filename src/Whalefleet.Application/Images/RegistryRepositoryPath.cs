namespace Whalefleet.Application.Images;

/// <summary>
/// 把镜像引用换算成 Registry V2 API 需要的「主机 + 仓库路径」。
/// </summary>
/// <remarks>
/// <para><b>为什么需要这一层</b>：用户在界面上填的是 Docker 的习惯写法
/// （<c>nginx:1.27</c>、<c>bitnami/nginx:1.27</c>、<c>harbor.local:5000/app:1.0</c>），
/// 而 Registry V2 的路径是另一套口径——官方镜像必须带 <c>library/</c> 命名空间，
/// 否则 <c>HEAD /v2/nginx/manifests/…</c> 会 404，而镜像明明存在。</para>
/// <para><b>别名与真名</b>：Docker Hub 有三个通行写法（<c>docker.io</c>、
/// <c>index.docker.io</c>、<c>registry-1.docker.io</c>），
/// 而 API 只在后者上可用。别名不归一化会导致「同一个镜像填不同前缀，一个能查一个
/// 不能」。</para>
/// </remarks>
public sealed record RegistryRepositoryPath(string ServerAddress, string Repository)
{
    /// <summary>
    /// Docker Hub 的别名（都要归一成 <see cref="ImageReference.DockerHubServerAddress"/>）。
    /// </summary>
    private static readonly string[] _dockerHubAliases =
    [
        "docker.io",
        "index.docker.io",
        "registry.hub.docker.com",
    ];

    /// <summary>
    /// 把镜像引用换算成 Registry V2 的请求坐标。
    /// </summary>
    /// <param name="repository">仓库名（可含主机前缀）。</param>
    /// <returns>主机地址与仓库路径。</returns>
    /// <exception cref="ArgumentException">仓库名为空。</exception>
    public static RegistryRepositoryPath From(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        var trimmed = repository.Trim().Trim('/');
        var server = ImageReference.ExtractServerAddress(trimmed);

        var path = String.Equals(server, ImageReference.DockerHubServerAddress, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : StripServerPrefix(trimmed, server);

        if (String.Equals(server, ImageReference.DockerHubServerAddress, StringComparison.OrdinalIgnoreCase)
            || _dockerHubAliases.Contains(server, StringComparer.OrdinalIgnoreCase))
        {
            path = StripDockerHubAlias(path);
            server = ImageReference.DockerHubServerAddress;

            // 官方镜像必须带 library/ 命名空间，否则 /v2/<name> 一律 404。
            if (!path.Contains('/', StringComparison.Ordinal))
            {
                path = $"library/{path}";
            }
        }

        return new RegistryRepositoryPath(server.ToLowerInvariant(), path);
    }

    /// <summary>
    /// 去掉仓库名里的主机前缀。
    /// </summary>
    /// <param name="repository">仓库名。</param>
    /// <param name="server">主机地址。</param>
    /// <returns>去掉前缀后的路径。</returns>
    private static string StripServerPrefix(string repository, string server) =>
        repository.StartsWith(server + "/", StringComparison.OrdinalIgnoreCase)
            ? repository[(server.Length + 1)..]
            : repository;

    /// <summary>
    /// 去掉 Docker Hub 的别名前缀（<c>docker.io/library/nginx</c> → <c>library/nginx</c>）。
    /// </summary>
    /// <param name="repository">仓库名。</param>
    /// <returns>去掉别名后的路径。</returns>
    private static string StripDockerHubAlias(string repository)
    {
        foreach (var alias in _dockerHubAliases)
        {
            if (repository.StartsWith(alias + "/", StringComparison.OrdinalIgnoreCase))
            {
                return repository[(alias.Length + 1)..];
            }
        }

        return repository;
    }
}
