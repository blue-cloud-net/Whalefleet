using System.Data.Common;
using Dapper;
using Whalefleet.Application.Abstractions.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Whalefleet.Infrastructure.Persistence;

/// <summary>
/// SQLite 连接工厂：按配置生成连接串，按需创建<b>未打开</b>的 <see cref="SqliteConnection"/>。
/// </summary>
/// <remarks>
/// <para><b>目录自动创建</b>：SQLite 只会创建数据库<b>文件</b>，不会创建其所在目录，
/// 目录缺失时会以「unable to open database file」失败（容器内极易踩到）。
/// 因此在构造函数里（而非每次取连接时）做一次目录准备。</para>
/// <para><b>连接池</b>：<c>Microsoft.Data.Sqlite</c> 自带连接池，
/// 因此「每次操作新开连接」只是借还池中连接，不会反复做文件打开。
/// 池化可由 <c>Persistence:Pooling</c> 关闭（集成测试依赖这一点来避免全局清池）。</para>
/// <para><b>类型映射初始化</b>：构造函数顺带调用 <see cref="DapperTypeMaps.Initialize"/>，
/// 保证任何取到连接的代码路径都已具备 snake_case 映射与自定义类型处理器
/// （否则会出现"列名对不上 → 静默丢值"这类难查问题）。</para>
/// <para><b>PRAGMA 设置</b>：外键约束与忙等待通过连接串关键字下发
/// （<c>Foreign Keys</c> 是每连接生效的 PRAGMA；<c>Default Timeout</c> 同时充当 busy timeout），
/// 因此无需逐次执行 PRAGMA。</para>
/// </remarks>
internal sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>
    /// 初始化连接工厂。
    /// </summary>
    /// <param name="options">持久化配置。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    public SqliteConnectionFactory(IOptions<SqliteOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;

        EnsureDatabaseDirectory(settings.DatabasePath);
        DapperTypeMaps.Initialize();

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = settings.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = settings.CommandTimeoutSeconds,
            Pooling = settings.Pooling,
        }.ToString();
    }

    /// <inheritdoc />
    public DbConnection Create() => new SqliteConnection(_connectionString);

    private static void EnsureDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!String.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
