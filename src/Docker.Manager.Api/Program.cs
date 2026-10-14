using Docker.Manager.Api.DependencyInjection;
using Docker.Manager.Api.Development;
using Docker.Manager.Api.Configuration;
using Docker.Manager.Api.Errors;
using Docker.Manager.Api.Middleware;
using Docker.Manager.Api.Serialization;
using Docker.Manager.Application.Abstractions.Persistence;
using Docker.Manager.Application.Common;
using Docker.Manager.Application.DependencyInjection;
using Docker.Manager.Infrastructure.DependencyInjection;
using Docker.Manager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Serilog：从配置文件读取（appsettings.json + appsettings.{Environment}.json），
// 集成 ASP.NET Core 日志（含请求日志中间件）。
builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

// 注册 Application + Infrastructure 层服务
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

// JWT 鉴权中间件 + 当前用户解析。
// 必须在 AddInfrastructure 之后：它会用 Api 层实现替换 ICurrentUser 的占位注册。
builder.Services.AddApiAuthentication(builder.Configuration);

// 当前主机上下文（Scoped）：由 CurrentHostMiddleware 从路由值 hostId 填充。
builder.Services.AddApiCurrentHost();

// CORS 白名单（默认空 = 完全拒绝跨域；开发环境走 Vite 代理，无需放行）
builder.Services.AddApiCors(builder.Configuration);

// ASP.NET Core MVC（Controller 风格）
// 统一 JSON 编码策略（中文原样输出而非 \uXXXX），与错误响应序列化器共用同一套默认值。
builder.Services
    .AddControllers()
    .AddJsonOptions(options => ApiJsonDefaults.Apply(options.JsonSerializerOptions));

// 统一「模型校验失败」与「框架自动生成的客户端错误」的响应形状，使其符合 ApiResult 契约。
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    // 禁止 ApiController 把 NotFound()/BadRequest() 等自动转换为 ProblemDetails，
    // 交由 UseStatusCodePages 统一包装为 ApiResult。
    options.SuppressMapClientErrors = true;

    // ModelState 校验失败默认返回 ValidationProblemDetails，此处改写为 ApiResult（code 1000）。
    options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
        ApiResults.Fail<object>(
            ErrorCodes.ValidationFailed,
            ValidationErrorFormatter.Format(context.ModelState)));
});

// 健康检查占位（0.0.15 增强为 SQLite 连通性检查）
builder.Services.AddHealthChecks();

// 监听地址由 appsettings.json 的 "Urls" 提供（生产默认 http://+:8080）。
// 刻意不在代码中硬编码 UseUrls：这会无条件覆盖一切外部配置，且不利于集成测试用 TestServer 接管。
//
// 【覆盖优先级（已实测）】appsettings.json 的 "Urls" 优先于 ASPNETCORE_URLS 环境变量与
// launchSettings.json 的 applicationUrl —— 因为 WebApplicationBuilder 把应用配置层
// 叠加在含 ASPNETCORE_ 前缀环境变量的宿主配置之上。
// 需要改端口时请使用以下任一方式（优先级自高至低）：
//   1) 命令行参数：dotnet Docker.Manager.Api.dll --urls http://0.0.0.0:9090
//   2) 无前缀环境变量：URLS=http://0.0.0.0:9090
var app = builder.Build();

// ============================== 管道顺序（不可随意调整） ==============================
// 0) 反向代理头修正：必须最先执行。
//    它改写 Connection.RemoteIpAddress，而限流计数与请求日志都读这个值——
//    任何在它之前读取的中间件都会把「反代的 IP」当成「客户端的 IP」。
//    未配置 ReverseProxy 白名单时本步不注册（保持直连语义），详见 ForwardedHeadersExtensions。
app.UseConfiguredForwardedHeaders(builder.Configuration);

// 1) 全局异常处理：必须是管道中第一个中间件，否则注册在它之前的中间件抛出的异常不会被捕获。
app.UseMiddleware<ExceptionHandlingMiddleware>();

// 2) 安全响应头：中间件内部用 Response.OnStarting 挂载，因此即便是由外层异常处理写出的
//    错误响应也带得上（那个路径会调用 Response.Clear()，直接赋的响应头会被清掉）。
app.UseMiddleware<SecurityHeadersMiddleware>();

// 3) 请求日志：包裹内层管道。
//    异常已由上一层完整记录（含堆栈与 TraceId），这里把「发生异常」的请求降级为 Debug，
//    避免同一次 5xx 产生两条 Error 日志、导致告警重复计数。
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = static (_, _, exception) => exception is null
        ? LogEventLevel.Information
        : LogEventLevel.Debug;
});

// 4) 空响应体的 4xx/5xx（未知路由、405 等）统一包装为 ApiResult。
app.UseStatusCodePages(StatusCodePageHandler.HandleAsync);

// 5) CORS：白名单为空时不会注册默认策略，等价于完全拒绝跨域。
app.UseCors();

// 6) 鉴权：必须在 UseCors 之后、MapControllers() 之前。
//    - UseAuthentication 填充 HttpContext.User（ICurrentUser 依赖它）
//    - UseAuthorization 执行 [Authorize] / [Authorize(Roles = …)]
app.UseAuthentication();
app.UseAuthorization();

// 7) 当前主机：从路由值 hostId 解析并写入 Scoped ICurrentHost；主机不存在则 1300/404。
//    放在鉴权之后：0.3.0 的资源可见性校验要读 HttpContext.User。
//    放在 MapControllers 之前：控制器与后续的主机作用域端点要能读到它。
app.UseCurrentHost();

app.MapControllers();
app.MapHealthChecks("/healthz");

// 8) 开发专用故障注入端点：仅 Development 环境注册，生产环境不存在该路由。
//    用于验证「异常 → 统一 ApiResult 错误响应」，并供集成测试复用。
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/v1/dev/throw", (string? kind) =>
    {
        throw DevelopmentFaults.Create(kind);
    });
}

// 数据库初始化：架构迁移 → 启用 WAL → 种入默认主机。
// 必须发生在开始监听之前——建表失败宁可启动失败，也不能让第一个请求撞上「no such table」。
await app.Services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

// 初始管理员引导：users 表为空且配置了 ADMIN__USER / ADMIN__PASSWORD 时创建 admin。
// 必须在数据库初始化之后——它要读由迁移创建的 users / roles 表。
await app.Services.GetRequiredService<IAdminBootstrapper>().InitializeAsync();

app.Run();

/// <summary>
/// 标记 Program 为 partial 类，便于集成测试通过 WebApplicationFactory 启动宿主。
/// </summary>
public partial class Program;