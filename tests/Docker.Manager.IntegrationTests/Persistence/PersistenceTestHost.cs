using System.Globalization;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Infrastructure.DependencyInjection;
using Docker.Manager.Infrastructure.Persistence;
using Docker.Manager.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Docker.Manager.IntegrationTests.Persistence;

/// <summary>
/// 持久化测试宿主：为每个测试准备<b>独立的临时数据库文件</b>与可控的时间源 / 当前用户。
/// </summary>
/// <remarks>
/// <para><b>为什么不用 <c>:memory:</c></b>：内存库是"每连接一个库"，而本层会为每次操作
/// 新开连接（连接池内），迁移建的表在下一个连接里就看不见了。</para>
/// <para><b>为什么要假时间与假用户</b>：审计填充的正确性（写入了什么时间、谁写的）
/// 只有在时间与用户可控时才能断言。</para>
/// <para><b>为什么要 <c>ClearAllPools</c></b>：驱动默认启用连接池，池中连接会占住数据库文件，
/// 删临时目录前必须先清池，否则清理会失败并留下垃圾。</para>
/// </remarks>
internal sealed class PersistenceTestHost : IAsyncDisposable
{
    /// <summary>
    /// 测试基准时刻（UTC）。
    /// </summary>
    internal static readonly DateTimeOffset Now = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;
    private readonly string _directory;

    /// <summary>
    /// 初始化测试宿主。
    /// </summary>
    /// <param name="currentUserId">当前登录用户主键；为 <see langword="null"/> 表示未认证（系统操作）。</param>
    public PersistenceTestHost(Guid? currentUserId = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), "docker-manager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        this.DatabasePath = Path.Combine(_directory, "docker-manager.db");

        this.TimeProvider = new TestTimeProvider(Now);
        this.CurrentUser = new StubCurrentUser(currentUserId);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Persistence:DatabasePath"] = this.DatabasePath,
                ["Persistence:CommandTimeoutSeconds"] = "30",
            })
            .Build();

        var services = new ServiceCollection();

        // 不引入 Microsoft.Extensions.Logging 的 AddLogging（测试项目只需要空实现），
        // 用 Abstractions 里的 NullLogger 顶掉日志依赖。
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // 先注册：后续 AddPersistence 内的 TryAdd 不会覆盖已存在的注册。
        services.AddSingleton<TimeProvider>(this.TimeProvider);
        services.AddSingleton<ICurrentUser>(this.CurrentUser);

        services.AddPersistence(configuration);

        _provider = services.BuildServiceProvider();
    }

    /// <summary>
    /// 临时数据库文件路径。
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// 可控时间源。
    /// </summary>
    public TestTimeProvider TimeProvider { get; }

    /// <summary>
    /// 当前用户桩。
    /// </summary>
    public StubCurrentUser CurrentUser { get; }

    /// <summary>
    /// 服务定位器。
    /// </summary>
    public IServiceProvider Services => _provider;

    /// <summary>
    /// 执行数据库初始化（迁移 + WAL + 默认主机种子）。
    /// </summary>
    /// <returns>初始化任务。</returns>
    public Task InitializeAsync() =>
        _provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();

    /// <summary>
    /// 用一条独立的连接读取标量值，用于断言"库里的真实形态"（而不只是实体内存态）。
    /// </summary>
    /// <param name="sql">查询语句。</param>
    /// <returns>标量值的字符串形式；<c>NULL</c> 返回 <see langword="null"/>。</returns>
    public async Task<string?> QueryScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={this.DatabasePath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 读取当前架构版本（<c>PRAGMA user_version</c>）。
    /// </summary>
    /// <returns>架构版本号。</returns>
    public async Task<int> GetSchemaVersionAsync()
    {
        var value = await this.QueryScalarAsync("PRAGMA user_version");
        return value is null ? 0 : Int32.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 执行一条非查询语句（如 <c>DELETE FROM hosts</c>），用于把某张表清成已知起点。
    /// </summary>
    /// <param name="sql">语句。</param>
    /// <returns>受影响行数。</returns>
    public async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={this.DatabasePath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return await command.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 测试用当前用户。
/// </summary>
internal sealed class StubCurrentUser : ICurrentUser
{
    /// <summary>
    /// 初始化当前用户桩。
    /// </summary>
    /// <param name="userId">用户主键；<see langword="null"/> 表示未认证。</param>
    /// <param name="username">用户名。</param>
    /// <param name="role">角色名。</param>
    public StubCurrentUser(Guid? userId, string? username = null, string? role = null)
    {
        this.UserId = userId;
        this.Username = username;
        this.Role = role;
    }

    /// <inheritdoc />
    public bool IsAuthenticated => this.UserId.HasValue;

    /// <inheritdoc />
    public Guid? UserId { get; }

    /// <inheritdoc />
    public string? Username { get; }

    /// <inheritdoc />
    public string? Role { get; }
}
