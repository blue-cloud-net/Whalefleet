namespace Docker.Manager.Infrastructure.Docker;

/// <summary>
/// 端点规范化结果。
/// </summary>
/// <param name="EndpointUri">
/// 规范化后的端点 URI，交给 <c>DockerClientBuilder.WithEndpoint</c> 使用。
/// </param>
/// <param name="CanonicalEndpoint">
/// 规范化的连接串文本，作为客户端缓存的键主体（必要时再拼认证区分串）。
/// </param>
public readonly record struct NormalizedDockerEndpoint(Uri EndpointUri, string CanonicalEndpoint);
