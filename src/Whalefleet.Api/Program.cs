using Whalefleet.Api.DependencyInjection;
using Whalefleet.Api.Development;
using Whalefleet.Api.Configuration;
using Whalefleet.Api.Errors;
using Whalefleet.Api.Middleware;
using Whalefleet.Api.Realtime;
using Whalefleet.Api.Serialization;
using Whalefleet.Api.Terminal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;
using Whalefleet.Application.Abstractions.Terminal;
using Whalefleet.Application.Abstractions.Persistence;
using Whalefleet.Application.Common;
using Whalefleet.Application.DependencyInjection;
using Whalefleet.Infrastructure.DependencyInjection;
using Whalefleet.Infrastructure.Persistence;
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

// 实时通道（0.5.0）：SignalR、Hub、订阅表与 Realtime 配置节。
// 必须在 AddInfrastructure 之后：Hub 要用主机访问器与权限判定（都由基础设施层注册）。
builder.Services.AddApiRealtime(builder.Configuration);

// 交互式终端（0.8.0）：Terminal 配置节 + 每用户会话计数器。
// 与实时通道同理，必须在 AddInfrastructure 之后（用例服务依赖主机访问器与权限判定）。
builder.Services.AddApiTerminal(builder.Configuration);

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
//   1) 命令行参数：dotnet Whalefleet.Api.dll --urls http://0.0.0.0:9090
//   2) 无前缀环境变量：URLS=http://0.0.0.0:9090
var app = builder.Build();

// SPA 配置要在管道搭建前读出来（根目录不存在时要在挂中间件**之前**就知道，
// 否则会为一个空目录注册静态文件中间件，日志里看不出任何异常）。
var spaOptions = ConfigurationReader.ReadSpa(builder.Configuration);
var spaEnabled = spaOptions.Enabled;
var spaRoot = spaEnabled ? SpaRootResolver.Resolve(spaOptions, app.Environment.ContentRootPath) : null;
if (spaEnabled && spaRoot is null)
{
    // 生产镜像里产物缺失 = 交付了一个只有接口、没有界面的容器。
    // 这必须在启动时就炸，而不是让人打开浏览器看到 404。
    throw new InvalidOperationException(
        $"Spa:Enabled 为 true，但找不到前端产物目录 '{spaOptions.RootPath}'。"
        + "请确认镜像构建时已执行 pnpm build 并把 dist 复制到发布目录的 wwwroot 下。");
}

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

// 4.1) 服务端错误兜底审计（0.9.0）：只记 5xx。
//      必须在外层异常处理**之内**（否则看不到端点抛出的异常），
//      且在端点之前（否则拿不到最终状态码）。
app.UseMiddleware<AuditFallbackMiddleware>();

// 5) CORS：白名单为空时不会注册默认策略，等价于完全拒绝跨域。
app.UseCors();

// 5.1) WebSocket 升级（0.8.0 的交互式终端）。
//     SignalR 自己处理升级，**不**依赖这一层；这里只服务 /ws 的原生 WebSocket。
//     KeepAliveInterval 必须**小于**反代的读超时（示例配置是 300s）：
//     用户盯着终端想事情时没有任何数据流，反代会把连接当空闲连接掐掉，
//     症状是「终端莫名断开、重连又好了」。
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(
        app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TerminalOptions>>()
            .Value.KeepAliveSeconds),
});

// 6) 鉴权：必须在 UseCors 之后、MapControllers() 之前。
//    - UseAuthentication 填充 HttpContext.User（ICurrentUser 依赖它）
//    - UseAuthorization 执行 [Authorize] / [Authorize(Roles = …)]
app.UseAuthentication();
app.UseAuthorization();

// 7) 前端产物（0.0.15）：生产镜像内由本进程同源托管，部署因此只有一个容器、一个端口。
//    未启用（Spa:Enabled=false，即开发环境默认）时整段跳过，
//    前端仍由 Vite 在 5173 提供，API 只负责接口。
//
//    ⚠️ 位置在 UseCurrentHost **之前**（不是「MapControllers 之前」就够）：
//    前端的 /hosts/{hostId}/... 与接口的 /api/v1/hosts/{hostId}/... 同前缀，
//    而 UseCurrentHost 会把任何请求的 hostId 路由值当主机去查库。
//    放在它之后，每次刷新 SPA 深链都会先抛「主机不存在」——页面根本打不开。
if (spaRoot is not null)
{
    var spaFileProvider = new PhysicalFileProvider(spaRoot);

    // UseDefaultFiles 让 / 命中 index.html；显式指定文件提供器是为了让
    // UseStaticFiles 能把目录请求转成文件请求（否则 / 会 404）。
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = spaFileProvider,
        DefaultFileNames = [spaOptions.IndexFile],
    });

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = spaFileProvider,
        ServeUnknownFileTypes = false,
    });

    // 深链回退：前端用 createWebHistory，/hosts/local/containers 这类路径
    // 在服务端没有对应端点，必须回退到 index.html 交给前端路由。
    // 刻意不用 MapFallbackToFile——它会把打错的 /api 路径也变成 200 + HTML。
    app.UseSpaFallback(spaRoot, spaOptions.IndexFile);
}

// 8) 当前主机：从路由值 hostId 解析并写入 Scoped ICurrentHost；主机不存在则 1300/404。
//    放在鉴权之后：0.1.0 的资源可见性校验要读 HttpContext.User。
//    放在 SPA 回退之后：前端的同前缀深链不是主机请求，不能让它进主机解析。
app.UseCurrentHost();

app.MapControllers();
app.MapHealthChecks("/healthz");

// ============================== 实时通道（0.5.0） ==============================
// 三条 Hub：
//   /hubs/hosts/{hostId}/container-log    容器日志逐行推送（主机作用域）
//   /hubs/hosts/{hostId}/container-stats  容器资源采样推送（主机作用域）
//   /hubs/notification                    全局通知（容器异常退出、镜像操作进度）
//
// ⚠️ 路径里带 {hostId} 的两条：CurrentHostMiddleware 会按路由值解析主机并校验可见性，
//    因此**不需要**为 Hub 另写一套主机解析。但「能不能读这台主机的容器」是更细的权限，
//    仍在 Hub 方法里逐次校验（见 RealtimeHostResolver）。
//
// ⚠️ 顺序：必须与 MapControllers 同一段（端点映射区），在 UseCurrentHost 之后——
//    UseCurrentHost 是中间件，读的是路由值，与端点映射的先后无关，但放在同一段更清楚。
app.MapHub<ContainerLogHub>(RealtimeHubRoutes.ContainerLog);
app.MapHub<ContainerStatsHub>(RealtimeHubRoutes.ContainerStats);
app.MapHub<NotificationHub>(RealtimeHubRoutes.Notification);

// ============================== 交互式终端（0.8.0） ==============================
// /ws/hosts/{hostId}/containers/exec —— 容器 exec 的原生 WebSocket。
//
// ⚠️ 这条端点的请求在 CurrentHostMiddleware 眼里是**匿名**的：它用 terminal token
//    （独立 audience 的短时 JWT）鉴权，而不是 access token。因此那个中间件里
//    显式跳过了 /ws 前缀，改由端点自己解析主机并校验 container.write。
//    不跳过的话症状是「一开终端就 1501」，而终端本身完全正常。
app.MapContainerTerminal();

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