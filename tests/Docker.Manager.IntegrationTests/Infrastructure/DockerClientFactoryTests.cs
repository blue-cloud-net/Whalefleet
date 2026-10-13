using Docker.DotNet;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Hosts;
using Docker.Manager.Infrastructure.Docker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Docker.Manager.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="DockerClientFactory"/> 的集成测试：把连接描述构建成可用的客户端。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：若只做编译验证，底层 Docker SDK 的用法错误要等到 0.0.9
/// 第一次真正调用时才暴露。本测试实际执行客户端构建，把「能编译」升级为「能构造出可用客户端」。</para>
/// <para>测试不依赖真实 Docker 守护进程：构建客户端本身不建立连接（只有 Ping 才需要守护进程）。</para>
/// <para><b>职责边界</b>：本类只测「新建」。客户端的复用、空闲回收、失效与释放见
/// <see cref="DockerClientPoolTests"/>。</para>
/// </remarks>
public class DockerClientFactoryTests
{
    [Theory]
    [InlineData(DockerHostType.Tcp, "tcp://127.0.0.1:2375")]
    [InlineData(DockerHostType.Socket, "/var/run/docker.sock")]
    [InlineData(DockerHostType.Socket, "unix:///var/run/docker.sock")]
    public void CreateClient_ShouldBuildClientForSupportedEndpoints(DockerHostType type, string endpoint)
    {
        var factory = CreateFactory();

        using var client = factory.CreateClient(Connection("local", type, endpoint));

        client.Should().NotBeNull();
    }

    [Fact]
    public void CreateClient_ShouldReturnDistinctInstances()
    {
        // 工厂是无状态的纯构建器：每次调用都新建，不复用任何东西。
        var factory = CreateFactory();
        var connection = Connection("local", DockerHostType.Socket, "/var/run/docker.sock");

        using var first = factory.CreateClient(connection);
        using var second = factory.CreateClient(connection);

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void CreateClient_ShouldThrowHostUnreachableForUnsupportedHostType()
    {
        var factory = CreateFactory();

        var act = () => factory.CreateClient(Connection("local", (DockerHostType)999, "tcp://127.0.0.1:2375"));

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    [Theory]
    [InlineData("relative/docker.sock")]
    [InlineData("http://127.0.0.1:2375")]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateClient_ShouldThrowHostUnreachableForInvalidEndpoint(string endpoint)
    {
        var factory = CreateFactory();

        var act = () => factory.CreateClient(Connection("local", DockerHostType.Socket, endpoint));

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    [Fact]
    public void CreateClient_ShouldThrowForNullConnection()
    {
        var factory = CreateFactory();

        var act = () => factory.CreateClient(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static DockerClientFactory CreateFactory() => new(NullLogger<DockerClientFactory>.Instance);

    private static DockerHostConnection Connection(string hostId, DockerHostType type, string endpoint) => new()
    {
        HostId = hostId,
        Type = type,
        Endpoint = endpoint,
    };
}
