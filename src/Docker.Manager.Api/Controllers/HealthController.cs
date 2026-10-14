using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Docker.Manager.Api.Controllers;

/// <summary>
/// 健康检查 Controller。
/// </summary>
/// <remarks>
/// <para>roadmap 0.0.1 仅要求 <c>GET /healthz</c> 返回 <c>200 OK</c>；
/// 0.0.15 增强为 SQLite 连通性检查（<b>不</b>查 Docker daemon——理由见下）。</para>
/// <para>本 Controller 风格为 MVC，与 roadmap 后续规划的 RPC 风格
/// <c>POST /api/v1/...</c> 端点共存：健康检查走 RESTful，资源操作走 RPC。</para>
/// <para><b>刻意不查 Docker daemon</b>：主机停机维护时管理界面仍需可用，
/// 把 Docker 算进健康检查会导致容器被 orchestrator 反复重启，而重启并不能让 Docker 恢复。
/// 该检查项移到管理界面上的独立状态展示（0.0.9 起已有）。</para>
/// </remarks>
[ApiController]
[Route("healthz")]
public sealed class HealthController : ControllerBase
{
    /// <summary>
    /// SQLite 检查项名称（与 <c>SqliteHealthCheck.CheckName</c> 一致）。
    /// </summary>
    private const string SqliteCheckName = "sqlite";

    private readonly HealthCheckService _healthChecks;

    /// <summary>
    /// 初始化 Controller。
    /// </summary>
    /// <param name="healthChecks">健康检查服务。</param>
    /// <exception cref="ArgumentNullException"><paramref name="healthChecks"/> 为 <see langword="null"/>。</exception>
    public HealthController(HealthCheckService healthChecks)
    {
        ArgumentNullException.ThrowIfNull(healthChecks);

        _healthChecks = healthChecks;
    }

    /// <summary>
    /// 健康检查端点。全部检查通过返回 <c>200 OK</c>，任一失败返回 <c>503 Service Unavailable</c>。
    /// </summary>
    /// <returns>
    /// 裸 JSON 对象（<b>不</b>包 <c>ApiResult</c>——外部探针不该依赖业务信封）：
    /// <c>status</c> 为 <c>ok</c> / <c>degraded</c>，<c>timestamp</c> 为 UTC 时间，
    /// <c>checks.sqlite</c> 为该检查的结论与耗时。
    /// </returns>
    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        var report = await _healthChecks.CheckHealthAsync(cancellationToken: cancellationToken);

        // 检查项缺失本身就是部署配置错误（Api 侧漏了 AddPersistence 或包引用），
        // 因此回退成 unhealthy 而不是「当作通过」——否则探针会一直绿着而问题无人察觉。
        var found = report.Entries.TryGetValue(SqliteCheckName, out var entry);
        var sqliteStatus = found ? entry.Status : HealthStatus.Unhealthy;
        var sqliteDuration = found ? entry.Duration : TimeSpan.Zero;
        var sqliteDescription = found ? entry.Description : "未注册该检查项";
        Exception? sqliteError = found ? entry.Exception : null;

        var healthy = report.Status == HealthStatus.Healthy;

        var payload = new
        {
            status = healthy ? "ok" : "degraded",
            timestamp = DateTimeOffset.UtcNow,
            checks = new
            {
                sqlite = new
                {
                    status = sqliteStatus.ToString().ToLowerInvariant(),
                    durationMs = Math.Round(sqliteDuration.TotalMilliseconds, 2),
                    description = sqliteDescription,
                    error = sqliteError?.Message,
                },
            },
        };

        return healthy
            ? this.Ok(payload)
            : this.StatusCode(StatusCodes.Status503ServiceUnavailable, payload);
    }
}