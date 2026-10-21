using Whalefleet.Core.Hosts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Persistence;

/// <summary>
/// 数据库初始化（迁移 / WAL / 默认主机种子）的集成测试。
/// </summary>
/// <remarks>
/// 这些用例同时充当「迁移脚本仍能被正确加载与应用」的守门人：
/// 嵌入式资源改名、csproj 的 <c>EmbeddedResource</c> 被删、版本号不连续，
/// 都会让这里的断言失败。
/// </remarks>
public class DatabaseInitializerTests
{
    /// <summary>
    /// 最新架构版本（<c>PRAGMA user_version</c>）——新增迁移时必须同步上调，
    /// 以此强制「新脚本确实被跑过」。
    /// </summary>
    /// <remarks>
    /// 0.1.0 新增 <c>0006_rbac.sql</c>；0.4.0 新增 <c>0007_repositories.sql</c>；
    /// 0.6.0 新增 <c>0008_host_tags.sql</c>。
    /// </remarks>
    private const int _expectedSchemaVersion = 8;

    [Fact]
    public async Task 初始化_建出业务表并推进架构版本()
    {
        await using var host = new PersistenceTestHost();

        await host.InitializeAsync();

        (await host.GetSchemaVersionAsync()).Should().Be(_expectedSchemaVersion);

        // 0.1.0 新增 role_permissions / user_role_assignments 两张表，所以清单变了：
        // 授权相关的两张新表取代了原先的角色字段。
        // 0.6.0 再新增 host_tags / host_tag_assignments（标签与关联）。
        var tableCount = await host.QueryScalarAsync(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' "
            + "AND name IN ('users', 'roles', 'hosts', 'refresh_tokens', 'user_role_assignments', "
            + "'host_tags', 'host_tag_assignments')");

        tableCount.Should().Be("7");
    }

    [Fact]
    public async Task 初始化_启用WAL()
    {
        await using var host = new PersistenceTestHost();

        await host.InitializeAsync();

        (await host.QueryScalarAsync("PRAGMA journal_mode")).Should().Be("wal");
    }

    [Fact]
    public async Task 初始化_hosts为空_种入默认主机()
    {
        await using var host = new PersistenceTestHost();

        await host.InitializeAsync();

        var hosts = host.Services.GetRequiredService<IHostRepository>();
        var list = await hosts.QueryAllListAsync();

        list.Should().ContainSingle();

        var local = list[0];
        local.Id.Should().Be(new Guid("00000000-0000-0000-0000-000000000001"));
        local.Name.Should().Be("local");
        local.Type.Should().Be(DockerHostType.Socket);
        local.Endpoint.Value.Should().Be("/var/run/docker.sock");
        local.Enabled.Should().BeTrue();
        local.Credentials.Should().Be(HostCredentials.None);
        local.IsDeleted.Should().BeFalse();

        // 启动引导没有登录用户，审计人取默认值哨兵
        local.CreatorId.Should().Be(Guid.Empty);
        local.CreationTime.Should().Be(PersistenceTestHost.Now.UtcDateTime);
    }

    [Fact]
    public async Task 初始化_重复调用_幂等()
    {
        await using var host = new PersistenceTestHost();

        await host.InitializeAsync();
        await host.InitializeAsync();

        (await host.GetSchemaVersionAsync()).Should().Be(_expectedSchemaVersion);

        var hosts = host.Services.GetRequiredService<IHostRepository>();
        (await hosts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task 初始化_hosts非空_不再种入默认主机()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        var hosts = host.Services.GetRequiredService<IHostRepository>();
        await hosts.RemoveByIdAsync(new Guid("00000000-0000-0000-0000-000000000001"));

        var remote = DockerHost.Register(
            Guid.NewGuid(),
            "remote",
            DockerHostType.Tcp,
            new HostEndpoint("tcp://10.0.0.5:2375"));

        await hosts.AddAsync(remote);

        await host.InitializeAsync();

        var list = await hosts.QueryAllListAsync();
        list.Should().ContainSingle().Which.Name.Should().Be("remote");
    }

    [Fact]
    public async Task 初始化_默认主机被软删除后重启_不重复种入也不撞主键()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        var hosts = host.Services.GetRequiredService<IHostRepository>();

        // 软删除后业务计数为 0，但物理行仍在；若按业务计数判断就会再次种入同一个固定主键
        await hosts.RemoveByIdAsync(new Guid("00000000-0000-0000-0000-000000000001"));
        (await hosts.CountAsync()).Should().Be(0);

        await host.InitializeAsync();

        (await hosts.QueryAllListAsync()).Should().BeEmpty();
        (await host.QueryScalarAsync("SELECT COUNT(1) FROM hosts")).Should().Be("1");
    }
}
