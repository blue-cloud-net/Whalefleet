using Microsoft.AspNetCore.Mvc;

namespace Docker.Manager.Api.Controllers;

/// <summary>
/// 健康检查 Controller。
/// </summary>
/// <remarks>
/// <para>roadmap 0.0.1 仅要求 <c>GET /healthz</c> 返回 <c>200 OK</c>；
/// 0.0.6 增强为 SQLite 连通性检查；0.0.9 起可能扩展为 Docker daemon 连通性。</para>
/// <para>本 Controller 风格为 MVC，与 roadmap 后续规划的 RPC 风格
/// <c>POST /api/v1/...</c> 端点共存：健康检查走 RESTful，资源操作走 RPC。</para>
/// </remarks>
[ApiController]
[Route("healthz")]
public sealed class HealthController : ControllerBase
{
    /// <summary>
    /// 健康检查端点。返回 <c>200 OK</c> 与当前时间戳。
    /// </summary>
    /// <returns>包含 <c>status</c> 与 <c>timestamp</c> 的匿名对象。</returns>
    [HttpGet]
    public IActionResult Get()
    {
        return this.Ok(new
        {
            status = "ok",
            timestamp = DateTimeOffset.UtcNow,
        });
    }
}