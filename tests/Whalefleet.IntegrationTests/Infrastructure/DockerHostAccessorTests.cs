using Docker.DotNet;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Infrastructure.Docker;
using Whalefleet.IntegrationTests.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Whalefleet.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="DockerHostAccessor"/> 的主机解析与取客户端行为（真库 + 真仓储）。
/// </summary>
/// <remarks>
/// <para><b>为什么用真库</b>：本类的全部价值就是「主机来源从配置换成 <c>hosts</c> 表」。
/// 只有走真实的 SQLite + <c>SqliteHostRepository</c> 才能证明：默认主机能按名解析到、
/// 大小写不敏感、软删除后不可见、停用后拒绝取用——这些用假的仓储都测不出来。</para>
/// </remarks>
public class DockerHostAccessorTests
{
    private const string LocalSocket = "/var/run/docker.sock";
    private const string UnreachableTcp = "tcp://127.0.0.1:1";

    [Fact]
    public async Task GetClientAsync_ShouldResolveSeededDefaultHost()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var client = await accessor.GetClientAsync("local");

        client.Should().NotBeNull();
    }

    [Fact]
    public async Task GetClientAsync_ShouldMatchHostIdIgnoringCase()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var client = await accessor.GetClientAsync("LOCAL");

        client.Should().NotBeNull();
    }

    [Fact]
    public async Task GetClientAsync_ShouldReusePooledClientAcrossCalls()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var first = await accessor.GetClientAsync("local");
        var second = await accessor.GetClientAsync("local");

        second.Should().BeSameAs(first, "同一主机的客户端必须复用（切换主机不重建连接）");
    }

    [Fact]
    public async Task GetClientAsync_ShouldThrowHostNotFoundForUnknownHost()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var act = async () => await accessor.GetClientAsync("missing");

        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task GetClientAsync_ShouldThrowHostNotFoundForSoftDeletedHost()
    {
        // 软删除后按名查询返回 null → 对外表现为「不存在」，名称亦可再次使用。
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        var hosts = host.Services.GetRequiredService<IHostRepository>();
        var local = (await hosts.FindByNameAsync("local"))!;
        (await hosts.RemoveByIdAsync(local.Id)).Should().BeTrue();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var act = async () => await accessor.GetClientAsync("local");

        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostNotFound);
    }

    [Fact]
    public async Task GetClientAsync_ShouldThrowHostDisabledWhenHostIsDisabled()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        await DisableLocalHostAsync(host);

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var act = async () => await accessor.GetClientAsync("local");

        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostDisabled);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnHostEntityEvenWhenDisabled()
    {
        // 解析只负责「找到主机」；停用与否由取客户端时判定
        // （0.3.0 的权限校验需要拿到主机的 Guid 主键，那时它也必须拿到停用的主机）。
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        await DisableLocalHostAsync(host);

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var resolved = await accessor.ResolveAsync("local");

        resolved.Name.Should().Be("local");
        resolved.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnHostEntityAcrossCaseVariants()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var direct = await accessor.ResolveAsync("local");
        var upper = await accessor.ResolveAsync("LOCAL");

        upper.Id.Should().Be(direct.Id);
    }

    [Theory]
    [InlineData(DockerHostType.Tcp)]
    [InlineData(DockerHostType.Socket)]
    public async Task TestConnectionAsync_ShouldReturnFalseWhenDaemonUnreachable(DockerHostType type)
    {
        // 指向必然不存在的端点：期望优雅返回 false（而不是抛出或挂起）。
        var endpoint = type == DockerHostType.Tcp ? UnreachableTcp : "/nonexistent/docker.sock";

        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var reachable = await accessor.TestConnectionAsync(Connection("probe", type, endpoint));

        reachable.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ShouldReturnFalseForInvalidEndpoint()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var reachable = await accessor.TestConnectionAsync(
            Connection("draft", DockerHostType.Socket, "relative/docker.sock"));

        reachable.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnectionAsync_ShouldNotTouchThePool()
    {
        await using var host = new PersistenceTestHost();
        await host.InitializeAsync();

        using var pool = CreatePool();
        var accessor = CreateAccessor(host, pool);

        var pooled = await accessor.GetClientAsync("local");

        await accessor.TestConnectionAsync(Connection("local", DockerHostType.Socket, LocalSocket));

        (await accessor.GetClientAsync("local"))
            .Should().BeSameAs(pooled, "连通性测试不得重建池中的客户端");
    }

    private static async Task DisableLocalHostAsync(PersistenceTestHost host)
    {
        var hosts = host.Services.GetRequiredService<IHostRepository>();
        var local = (await hosts.FindByNameAsync("local"))!;
        local.Disable();
        (await hosts.UpdateAsync(local)).Should().BeTrue();
    }

    private static DockerHostAccessor CreateAccessor(PersistenceTestHost host, DockerClientPool pool) =>
        new(
            host.Services.GetRequiredService<IHostRepository>(),
            pool,
            new DockerClientFactory(NullLogger<DockerClientFactory>.Instance),
            NullLogger<DockerHostAccessor>.Instance);

    private static DockerClientPool CreatePool() =>
        new(
            new DockerClientFactory(NullLogger<DockerClientFactory>.Instance),
            Options.Create(new DockerClientFactoryOptions()),
            TimeProvider.System,
            NullLogger<DockerClientPool>.Instance);

    private static DockerHostConnection Connection(string hostId, DockerHostType type, string endpoint) => new()
    {
        HostId = hostId,
        Type = type,
        Endpoint = endpoint,
    };
}
