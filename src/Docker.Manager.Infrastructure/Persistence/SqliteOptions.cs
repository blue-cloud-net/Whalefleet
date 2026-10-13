namespace Docker.Manager.Infrastructure.Persistence;

/// <summary>
/// SQLite 持久化配置（配置节 <c>Persistence</c>）。
/// </summary>
/// <remarks>
/// <para><b>默认路径</b>：生产为 <c>/app/data/docker-manager.db</c>（由 <c>appsettings.json</c> 提供），
/// 开发为 <c>data/docker-manager.db</c>（由 <c>appsettings.Development.json</c> 覆盖）。
/// 与日志一样，容器内 <c>/app/data</c> 是唯一的可写卷。</para>
/// <para><b>环境变量</b>：<c>PERSISTENCE__DATABASEPATH</c> /
/// <c>PERSISTENCE__COMMANDTIMEOUTSECONDS</c>（层级用 <c>__</c>）。</para>
/// <para><b>不支持内存库</b>：<c>:memory:</c> 每次连接都是独立数据库，会使"启动建表"与
/// "后续请求"看到不同的库，因此不在支持范围内。</para>
/// </remarks>
public sealed class SqliteOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Persistence";

    /// <summary>
    /// 数据库文件路径（相对路径按进程当前工作目录解析）。目录不存在时自动创建。
    /// </summary>
    public string DatabasePath { get; set; } = String.Empty;

    /// <summary>
    /// 命令超时（秒），同时用作 SQLite 的忙等待（busy timeout）时长。
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
}
