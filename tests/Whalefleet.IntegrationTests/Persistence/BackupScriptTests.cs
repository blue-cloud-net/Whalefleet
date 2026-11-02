using System.Diagnostics;

using FluentAssertions;

using Microsoft.Data.Sqlite;

using Xunit;

namespace Whalefleet.IntegrationTests.Persistence;

/// <summary>
/// 真实 bash 与 SQLite 验证数据库、密钥和轮转策略共同构成可恢复备份。
/// </summary>
public sealed class BackupScriptTests
{
    [Theory]
    [InlineData("repository.key")]
    [InlineData("registry.key")]
    public async Task 备份_数据库与密钥_可恢复且一起轮转(string keyName)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"wf-backup-{Guid.NewGuid():N}");
        var backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(backups);
        try
        {
            var database = Path.Combine(root, "test.db");
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=false"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE probe(value TEXT); INSERT INTO probe VALUES ('恢复验证');";
                await command.ExecuteNonQueryAsync();
            }

            var key = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(root, keyName), key);
            File.WriteAllText(Path.Combine(root, "jwt.key"), "jwt-test-key");
            var old = Path.Combine(backups, "whalefleet-20000101-000000.db");
            foreach (var suffix in new[] { String.Empty, ".jwt.key", ".repository.key" })
            {
                File.WriteAllText(old + suffix, "old");
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "backup.sh")))
            {
                directory = directory.Parent;
            }

            directory.Should().NotBeNull();
            var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(directory!.FullName, "scripts", "backup.sh"));
            start.Environment["WHALEFLEET_DB"] = database;
            start.Environment["WHALEFLEET_BACKUP_DIR"] = backups;
            start.Environment["WHALEFLEET_BACKUP_KEEP"] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            process.ExitCode.Should().Be(0, (await output) + (await errors));

            var backup = Directory.GetFiles(backups, "*.db").Should().ContainSingle().Subject;
            File.Exists(old).Should().BeFalse();
            File.Exists(old + ".jwt.key").Should().BeFalse();
            File.Exists(old + ".repository.key").Should().BeFalse();
            File.ReadAllBytes(backup + ".repository.key").Should().Equal(key);
            File.ReadAllText(backup + ".jwt.key").Should().Be("jwt-test-key");
            File.GetUnixFileMode(backup + ".repository.key").Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);

            await using var restored = new SqliteConnection($"Data Source={backup};Pooling=false");
            await restored.OpenAsync();
            await using var check = restored.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            (await check.ExecuteScalarAsync()).Should().Be("ok");
            check.CommandText = "SELECT value FROM probe";
            (await check.ExecuteScalarAsync()).Should().Be("恢复验证");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
