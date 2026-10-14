using Docker.Manager.Application.Abstractions.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Docker.Manager.Infrastructure.Persistence;

/// <summary>
/// SQLite 连通性健康检查（0.0.15）。
/// </summary>
/// <remarks>
/// <para><b>为什么只查 SQLite、不查 Docker</b>：本检查服务于容器的 <c>HEALTHCHECK</c>，
/// 问的是「这个进程还能不能干活」，而 SQLite 是本服务<b>唯一</b>无法降级恢复的依赖
/// ——磁盘满、文件权限错、卷没挂上，都会让每个业务端点直接 500。</para>
/// <para>反过来，Docker daemon 不可达<b>不应</b>让容器被判为 unhealthy：主机可能被
/// 停机维护，而管理界面此时仍需打开以便查看配置、甚至新增主机。把它算进健康检查等于
/// 「Docker 一停，容器就被 orchestrator 反复重启」，而重启并不能让 Docker 恢复。</para>
/// <para><b>查询为什么是 <c>SELECT 1</c></b>：它只验证「能开连接、能执行语句」，
/// 不触碰任何表，因此不会与迁移争锁，也能在表尚未建好时给出可诊断的结论。</para>
/// </remarks>
internal sealed class SqliteHealthCheck : IHealthCheck
{
    /// <summary>
    /// 检查项名称，会出现在 <c>/healthz</c> 的 <c>checks</c> 字段里。
    /// </summary>
    internal const string CheckName = "sqlite";

    private readonly IDbConnectionFactory _connections;
    private readonly ILogger<SqliteHealthCheck> _logger;

    /// <summary>
    /// 初始化健康检查。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public SqliteHealthCheck(IDbConnectionFactory connections, ILogger<SqliteHealthCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await using var connection = _connections.Create();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            // 只记 Warning 不记 Error：健康检查失败不是应用异常，探针每几秒就会打一次，
            // 记 Error 会把告警刷满，真正的失败原因反而被淹没。
            _logger.LogWarning(
                exception,
                "SQLite 健康检查失败，路径 {DatabasePath}",
                context.Registration?.Name ?? CheckName);

            return HealthCheckResult.Unhealthy(
                "无法访问 SQLite：{Message}",
                exception);
        }
    }
}