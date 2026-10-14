using System.Data.Common;
using System.Globalization;
using Whalefleet.Application.Abstractions.Persistence;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Infrastructure.Persistence;

/// <summary>
/// 基于 <c>PRAGMA user_version</c> 的 SQLite 架构迁移器。
/// </summary>
/// <remarks>
/// <para><b>为什么现在就版本化</b>：0.1.0 已承诺「SQLite schema 版本化管理（启动时检查 + 自动迁移）」。
/// 先用 <c>user_version</c> 打好地基，比日后把「一堆 <c>CREATE TABLE IF NOT EXISTS</c>」
/// 反向拆成迁移要便宜得多。</para>
/// <para><b>版本来源</b>：嵌入资源文件名前缀（<c>0001_users.sql</c> → 版本 1）。
/// 版本号必须从 1 开始<b>连续</b>，否则启动即失败——避免"漏写一个脚本"变成静默的架构漂移。</para>
/// <para><b>原子性</b>：每个脚本与其版本号写入同一个事务，因此中断只会留下"该脚本整体未应用"，
/// 不会出现"表建了一半、版本号却已推进"的中间态。</para>
/// <para><b>并发</b>：迁移在进程启动时执行一次（<see cref="IDatabaseInitializer"/>）。
/// 单实例部署下不存在并发迁移；多进程同时启动由 SQLite 的文件锁兜底。</para>
/// <para><b><c>PRAGMA</c> 不可参数化</b>：版本号用字符串插值拼入，但它来自文件名解析出的
/// <see cref="System.Int32"/>，不是外部输入。</para>
/// </remarks>
internal sealed class SqliteSchemaMigrator
{
    private const string _resourceMarker = ".Persistence.Schema.Migrations.";
    private const string _resourceSuffix = ".sql";

    private readonly IDbConnectionFactory _connections;
    private readonly ILogger<SqliteSchemaMigrator> _logger;

    /// <summary>
    /// 初始化迁移器。
    /// </summary>
    /// <param name="connections">连接工厂。</param>
    /// <param name="logger">日志。</param>
    public SqliteSchemaMigrator(IDbConnectionFactory connections, ILogger<SqliteSchemaMigrator> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _logger = logger;
    }

    /// <summary>
    /// 应用所有尚未执行的迁移。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用完成后的架构版本（<c>user_version</c>）。</returns>
    internal async Task<int> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var scripts = LoadScripts();

        await using var connection = _connections.Create();
        await connection.OpenAsync(cancellationToken);

        var currentVersion = await GetUserVersionAsync(connection, cancellationToken);

        foreach (var script in scripts)
        {
            if (script.Version <= currentVersion)
            {
                continue;
            }

            await ApplyAsync(connection, script, cancellationToken);
            currentVersion = script.Version;

            _logger.LogInformation(
                "已应用数据库迁移 {ResourceName}（user_version → {Version}）",
                script.ResourceName,
                script.Version);
        }

        return currentVersion;
    }

    private static MigrationScript[] LoadScripts()
    {
        var scripts = typeof(SqliteSchemaMigrator).Assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(_resourceMarker, StringComparison.Ordinal)
                && name.EndsWith(_resourceSuffix, StringComparison.Ordinal))
            .Select(ToScript)
            .OrderBy(script => script.Version)
            .ToArray();

        if (scripts.Length == 0)
        {
            throw new InvalidOperationException(
                "未找到任何嵌入式迁移脚本；请确认 Infrastructure.csproj 已把 Schema/Migrations/*.sql 标记为 EmbeddedResource");
        }

        for (var index = 0; index < scripts.Length; index++)
        {
            if (scripts[index].Version != index + 1)
            {
                throw new InvalidOperationException(
                    $"迁移脚本版本必须从 1 开始连续编号，但第 {index + 1} 个脚本的版本为 {scripts[index].Version}"
                    + $"（{scripts[index].ResourceName}）");
            }
        }

        return scripts;
    }

    private static MigrationScript ToScript(string resourceName)
    {
        var markerIndex = resourceName.IndexOf(_resourceMarker, StringComparison.Ordinal);
        var fileName = resourceName[(markerIndex + _resourceMarker.Length)..^_resourceSuffix.Length];

        var separatorIndex = fileName.IndexOf('_', StringComparison.Ordinal);
        var versionText = separatorIndex < 0 ? fileName : fileName[..separatorIndex];

        if (!Int32.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new InvalidOperationException($"迁移脚本文件名必须以版本号开头：{resourceName}");
        }

        return new MigrationScript(version, resourceName, ReadResource(resourceName));
    }

    private static string ReadResource(string resourceName)
    {
        using var stream = typeof(SqliteSchemaMigrator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到嵌入资源：{resourceName}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<int> GetUserVersionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task ApplyAsync(
        DbConnection connection,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = script.Sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA user_version = {script.Version}";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 一个待应用的迁移脚本。
    /// </summary>
    /// <param name="Version">目标架构版本。</param>
    /// <param name="ResourceName">嵌入资源名（用于日志与报错）。</param>
    /// <param name="Sql">脚本内容。</param>
    private sealed record MigrationScript(int Version, string ResourceName, string Sql);
}
