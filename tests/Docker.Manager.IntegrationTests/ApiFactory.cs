using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Docker.Manager.IntegrationTests;

/// <summary>
/// 集成测试宿主工厂：在进程内以 <c>TestServer</c> 启动真实的 <c>Docker.Manager.Api</c> 宿主。
/// </summary>
/// <remarks>
/// <para>与直接 <c>new</c> 中间件的单元测试不同，本工厂走完整管道（含 DI、配置、中间件顺序），
/// 因此可以验证「异常 → 统一 ApiResult 错误响应」这类只有端到端才能证明的契约。</para>
/// <para><b>环境</b>：固定为 <c>Development</c>，因为 0.0.2 的 <c>/api/v1/dev/throw</c>
/// 故障注入端点只在开发环境注册。</para>
/// <para><b>日志隔离</b>：把 Serilog 文件 sink 的落盘目录重定向到每个实例独立的临时目录，
/// 避免测试污染仓库，也避免并行测试互相写同一个文件。</para>
/// <para><b>用法</b>：通过 <see cref="WithSetting"/> 在首次创建 HttpClient 之前叠加配置。</para>
/// </remarks>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = new(StringComparer.Ordinal);
    private readonly string _logDirectory;
    private readonly string _databasePath;

    /// <summary>
    /// 初始化 <see cref="ApiFactory"/>，并为本次测试分配独立的日志目录与数据库文件。
    /// </summary>
    public ApiFactory()
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "docker-manager-tests",
            Guid.NewGuid().ToString("N"));

        _logDirectory = Path.Combine(workingDirectory, "logs");
        _databasePath = Path.Combine(workingDirectory, "docker-manager.db");
    }

    /// <summary>
    /// 本次测试专属的日志目录（可能不存在——仅当确有日志写入时才会创建）。
    /// </summary>
    public string LogDirectory => _logDirectory;

    /// <summary>
    /// 叠加一项配置。必须在首次创建 <c>HttpClient</c>（即宿主构建）之前调用。
    /// </summary>
    /// <param name="key">配置键，使用 <c>:</c> 分隔层级。</param>
    /// <param name="value">配置值。</param>
    /// <returns>当前工厂实例，便于链式调用。</returns>
    public ApiFactory WithSetting(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _settings[key] = value;
        return this;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // 故障注入端点仅在 Development 环境注册。
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                // 显式给出 Name，使本覆盖不依赖 appsettings.json 中 WriteTo 数组的下标布局；
                // 同时把落盘位置改到临时目录。
                ["Serilog:WriteTo:1:Name"] = "File",
                ["Serilog:WriteTo:1:Args:path"] = Path.Combine(_logDirectory, "app-.log"),

                // 测试只关心响应契约，压低日志级别减少噪声。
                ["Serilog:MinimumLevel:Default"] = "Warning",

                // 宿主启动时会执行数据库初始化（迁移 + 默认主机种子），
                // 必须把库重定向到实例专属临时目录：否则测试会在 bin/ 下反复建库，
                // 并行执行的多个宿主还会相互争抢同一个库文件。
                ["Persistence:DatabasePath"] = _databasePath,
            };

            foreach (var (key, value) in _settings)
            {
                settings[key] = value;
            }

            configuration.AddInMemoryCollection(settings);
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // 连接池会占住数据库文件，清池后才能删除临时目录。
        SqliteConnection.ClearAllPools();

        var workingDirectory = Path.GetDirectoryName(_logDirectory);
        if (workingDirectory is null || !Directory.Exists(workingDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应让测试失败（Windows 上文件句柄释放可能滞后）。
        }
    }
}
