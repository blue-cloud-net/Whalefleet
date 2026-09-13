using Docker.Manager.Application.DependencyInjection;
using Docker.Manager.Infrastructure.DependencyInjection;
using Serilog;

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

// ASP.NET Core MVC（Controller 风格）
builder.Services.AddControllers();

// 健康检查占位（0.0.6 增强为 SQLite 连通性检查）
builder.Services.AddHealthChecks();

// Kestrel 监听端口：默认 :8080（与 roadmap 一致；容器内通常通过环境变量覆盖）
builder.WebHost.UseUrls("http://+:8080");

var app = builder.Build();

app.UseSerilogRequestLogging();

app.MapControllers();
app.MapHealthChecks("/healthz");

app.Run();

/// <summary>
/// 标记 Program 为 partial 类，便于未来扩展（如集成测试 WebApplicationFactory）。
/// </summary>
public partial class Program;