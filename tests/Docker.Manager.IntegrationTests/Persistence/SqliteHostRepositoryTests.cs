using Docker.Manager.Core.Hosts;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Docker.Manager.IntegrationTests.Persistence;

/// <summary>
/// <c>SqliteHostRepository</c> 的持久化行为集成测试。
/// </summary>
/// <remarks>
/// 重点验证<b>凭据的两列 ↔ 一个值对象</b>的往返：实体对外只有一个
/// <c>Credentials</c> 计算属性，库里却是 <c>auth_kind</c> + <c>auth_config_json</c> 两列，
/// 这层「分裂」只有真库往返才能证明没写串。
/// </remarks>
public class SqliteHostRepositoryTests
{
    private const string _tlsPayload = """{"caPath":"/certs/ca.pem","certPath":"/certs/cert.pem"}""";

    private static readonly Guid _operatorId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task AddAsync_填充创建审计并可用主键读回()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        var dockerHost = CreateSocketHost("prod-host-1");
        await hosts.AddAsync(dockerHost);
        var loaded = await hosts.QueryByIdAsync(dockerHost.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(dockerHost.Id);
        loaded.Name.Should().Be("prod-host-1");
        loaded.Type.Should().Be(DockerHostType.Socket);
        loaded.Endpoint.Value.Should().Be("/var/run/docker.sock");
        loaded.Enabled.Should().BeTrue();
        loaded.Credentials.Should().Be(HostCredentials.None);

        loaded.CreationTime.Should().Be(PersistenceTestHost.Now.UtcDateTime);
        loaded.CreatorId.Should().Be(_operatorId);
        loaded.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task 落库形态_枚举为小写文本_无认证时载荷为NULL()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        var dockerHost = CreateSocketHost("local-alias");
        await hosts.AddAsync(dockerHost);

        (await host.QueryScalarAsync("SELECT type FROM hosts")).Should().Be("socket");
        (await host.QueryScalarAsync("SELECT auth_kind FROM hosts")).Should().Be("none");
        (await host.QueryScalarAsync("SELECT auth_config_json FROM hosts")).Should().BeNull();
        (await host.QueryScalarAsync("SELECT endpoint FROM hosts")).Should().Be("/var/run/docker.sock");
        (await host.QueryScalarAsync("SELECT enabled FROM hosts")).Should().Be("1");
        (await host.QueryScalarAsync("SELECT id FROM hosts")).Should().Be(dockerHost.Id.ToString("D"));
    }

    [Fact]
    public async Task 带TLS凭据_两列往返不丢载荷()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        var credentials = HostCredentials.FromJson(DockerHostAuthKind.Tls, _tlsPayload);
        var dockerHost = DockerHost.Register(
            Guid.NewGuid(),
            "remote-tls",
            DockerHostType.Tcp,
            new HostEndpoint("tcp://10.0.0.5:2376"),
            credentials);

        await hosts.AddAsync(dockerHost);

        (await host.QueryScalarAsync("SELECT type FROM hosts")).Should().Be("tcp");
        (await host.QueryScalarAsync("SELECT auth_kind FROM hosts")).Should().Be("tls");
        (await host.QueryScalarAsync("SELECT auth_config_json FROM hosts")).Should().Be(_tlsPayload);

        var loaded = (await hosts.QueryByIdAsync(dockerHost.Id))!;
        loaded.Credentials.Kind.Should().Be(DockerHostAuthKind.Tls);
        loaded.Credentials.ConfigJson.Should().Be(_tlsPayload);
        loaded.Credentials.Should().Be(credentials);
    }

    [Fact]
    public async Task FindByNameAsync_不区分大小写()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();
        await hosts.AddAsync(CreateSocketHost("ProdHost"));

        (await hosts.FindByNameAsync("prodhost")).Should().NotBeNull();
        (await hosts.FindByNameAsync("PRODHOST"))!.Name.Should().Be("ProdHost");
        (await hosts.FindByNameAsync("nobody")).Should().BeNull();
    }

    [Fact]
    public async Task ExistsByNameAsync_忽略已软删除的主机()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();
        var dockerHost = CreateSocketHost("prod-host-1");
        await hosts.AddAsync(dockerHost);

        (await hosts.ExistsByNameAsync("PROD-HOST-1")).Should().BeTrue();

        await hosts.RemoveByIdAsync(dockerHost.Id);

        (await hosts.ExistsByNameAsync("prod-host-1")).Should().BeFalse();
    }

    [Fact]
    public async Task QueryAllListAsync_按创建时间升序_支持计数()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        await hosts.AddAsync(CreateSocketHost("first"));
        host.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await hosts.AddAsync(CreateSocketHost("second"));

        var list = await hosts.QueryAllListAsync();

        list.Select(item => item.Name).Should().ContainInOrder("first", "second");
        (await hosts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task UpdateAsync_持久化连接方式端点与凭据的改动()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        var dockerHost = CreateSocketHost("prod-host-1");
        await hosts.AddAsync(dockerHost);

        host.TimeProvider.Advance(TimeSpan.FromHours(1));
        dockerHost.UpdateConnection(
            DockerHostType.Tcp,
            new HostEndpoint("tcp://10.0.0.9:2375"),
            HostCredentials.FromJson(DockerHostAuthKind.Basic, """{"user":"admin"}"""));

        (await hosts.UpdateAsync(dockerHost)).Should().BeTrue();

        var loaded = (await hosts.QueryByIdAsync(dockerHost.Id))!;
        loaded.Type.Should().Be(DockerHostType.Tcp);
        loaded.Endpoint.Value.Should().Be("tcp://10.0.0.9:2375");
        loaded.Credentials.Kind.Should().Be(DockerHostAuthKind.Basic);
        loaded.Credentials.ConfigJson.Should().Be("""{"user":"admin"}""");

        loaded.LastModificationTime.Should().Be(PersistenceTestHost.Now.AddHours(1).UtcDateTime);
        loaded.LastModifierId.Should().Be(_operatorId);

        (await host.QueryScalarAsync("SELECT type FROM hosts")).Should().Be("tcp");
        (await host.QueryScalarAsync("SELECT auth_kind FROM hosts")).Should().Be("basic");
    }

    [Fact]
    public async Task UpdateAsync_持久化连接变更与停用()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        var dockerHost = CreateSocketHost("prod-host-1");
        await hosts.AddAsync(dockerHost);

        // 名称不可改，可改的是「连接方式 + 端点 + 凭据」这一组。
        dockerHost.UpdateConnection(DockerHostType.Tcp, new HostEndpoint("tcp://10.0.0.5:2375"));
        dockerHost.Disable();

        (await hosts.UpdateAsync(dockerHost)).Should().BeTrue();

        var loaded = (await hosts.QueryByIdAsync(dockerHost.Id))!;
        loaded.Name.Should().Be("prod-host-1");
        loaded.Type.Should().Be(DockerHostType.Tcp);
        loaded.Endpoint.Value.Should().Be("tcp://10.0.0.5:2375");
        loaded.Enabled.Should().BeFalse();

        (await host.QueryScalarAsync("SELECT enabled FROM hosts")).Should().Be("0");
    }

    [Fact]
    public async Task UpdateAsync_目标不存在_返回false()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        (await hosts.UpdateAsync(CreateSocketHost("ghost"))).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_软删除_业务查询不可见且名称可复用()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        var dockerHost = CreateSocketHost("prod-host-1");
        await hosts.AddAsync(dockerHost);

        host.TimeProvider.Advance(TimeSpan.FromMinutes(30));
        (await hosts.RemoveByIdAsync(dockerHost.Id)).Should().BeTrue();

        (await hosts.QueryByIdAsync(dockerHost.Id)).Should().BeNull();
        (await hosts.CountAsync()).Should().Be(0);
        (await host.QueryScalarAsync("SELECT COUNT(1) FROM hosts")).Should().Be("1");
        (await host.QueryScalarAsync("SELECT is_deleted FROM hosts")).Should().Be("1");
        (await host.QueryScalarAsync("SELECT deleter_id FROM hosts")).Should().Be(_operatorId.ToString("D"));

        (await hosts.RemoveByIdAsync(dockerHost.Id)).Should().BeFalse();

        // 部分唯一索引（WHERE is_deleted = 0）让名称可以再次使用
        await hosts.AddAsync(CreateSocketHost("PROD-HOST-1"));
        (await hosts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task 名称唯一约束_不区分大小写()
    {
        await using var host = await CreateHostAsync();
        var hosts = host.Services.GetRequiredService<IHostRepository>();

        await hosts.AddAsync(CreateSocketHost("prod-host-1"));

        var act = () => hosts.AddAsync(CreateSocketHost("PROD-HOST-1"));

        await act.Should().ThrowAsync<SqliteException>();
    }

    private static async Task<PersistenceTestHost> CreateHostAsync()
    {
        var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        // 初始化会种入默认主机；本组用例自行控制 hosts 表内容，因此清空后重新开始。
        await host.ExecuteAsync("DELETE FROM hosts");

        return host;
    }

    private static DockerHost CreateSocketHost(string name) =>
        DockerHost.Register(Guid.NewGuid(), name, DockerHostType.Socket, new HostEndpoint("/var/run/docker.sock"));
}
