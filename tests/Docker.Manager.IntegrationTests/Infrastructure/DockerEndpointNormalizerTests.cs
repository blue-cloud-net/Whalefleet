using Docker.Manager.Application.Abstractions;
using Docker.Manager.Infrastructure.Docker;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.IntegrationTests.Infrastructure;

/// <summary>
/// <see cref="DockerEndpointNormalizer"/> 的规范化规则测试。
/// </summary>
/// <remarks>
/// <para>这套规则是「按连接缓存」的地基：只要两种写法被规范化成同一个连接串，
/// 它们就会共享同一个客户端与 HTTP 连接池。</para>
/// <para>其中对 <see cref="Uri.AbsolutePath"/> 的断言尤为关键——它守住了
/// <c>unix://var/run/docker.sock</c> 这类写法被 <see cref="Uri"/> 解析成
/// <c>Host="var"</c> + <c>Path="/run/docker.sock"</c> 的陷阱。</para>
/// </remarks>
public class DockerEndpointNormalizerTests
{
    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("unix://var/run/docker.sock")]
    [InlineData("/var/run/docker.sock/")]
    [InlineData("unix:////var/run/docker.sock")]
    [InlineData("  /var/run/docker.sock  ")]
    public void SocketEndpoints_ShouldNormalizeToSingleCanonicalForm(string input)
    {
        var succeeded = DockerEndpointNormalizer.TryNormalize(
            DockerHostType.Socket,
            input,
            out var normalized,
            out var error);

        succeeded.Should().BeTrue($"端点「{input}」应当合法：{error}");
        normalized.CanonicalEndpoint.Should().Be("unix:///var/run/docker.sock");
        normalized.EndpointUri.Scheme.Should().Be("unix");

        // 三个斜杠才让 socket 路径完整落在 AbsolutePath 上。
        normalized.EndpointUri.AbsolutePath.Should().Be("/var/run/docker.sock");
    }

    [Theory]
    [InlineData("npipe://./pipe/docker_engine")]
    [InlineData("npipe://./pipe/docker_engine/")]
    [InlineData("//./pipe/docker_engine")]
    [InlineData(@"\\.\pipe\docker_engine")]
    [InlineData("NPIPE://./pipe/docker_engine")]
    public void NamedPipeEndpoints_ShouldNormalizeToNpipeForm(string input)
    {
        var succeeded = DockerEndpointNormalizer.TryNormalize(
            DockerHostType.Socket,
            input,
            out var normalized,
            out var error);

        succeeded.Should().BeTrue($"端点「{input}」应当合法：{error}");
        normalized.CanonicalEndpoint.Should().Be("npipe://./pipe/docker_engine");
        normalized.EndpointUri.Scheme.Should().Be("npipe");
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:2375", "tcp://127.0.0.1:2375")]
    [InlineData("127.0.0.1:2375", "tcp://127.0.0.1:2375")]
    [InlineData("tcp://127.0.0.1", "tcp://127.0.0.1:2375")]
    [InlineData("127.0.0.1", "tcp://127.0.0.1:2375")]
    [InlineData("HTTP://Docker.Example:2375", "tcp://docker.example:2375")]
    [InlineData("tcp://socket-proxy:2375/", "tcp://socket-proxy:2375")]
    [InlineData("tcp://[::1]:2375", "tcp://[::1]:2375")]
    [InlineData("tcp://[::1]", "tcp://[::1]:2375")]
    public void TcpEndpoints_ShouldNormalizeHostCaseAndDefaultPort(string input, string expected)
    {
        var succeeded = DockerEndpointNormalizer.TryNormalize(
            DockerHostType.Tcp,
            input,
            out var normalized,
            out var error);

        succeeded.Should().BeTrue($"端点「{input}」应当合法：{error}");
        normalized.CanonicalEndpoint.Should().Be(expected);
        normalized.EndpointUri.Scheme.Should().Be("tcp");
        normalized.EndpointUri.Port.Should().Be(DockerEndpointNormalizer.DefaultTcpPort);
    }

    [Fact]
    public void TcpEndpoint_ShouldKeepProxyPathPrefix()
    {
        // 反向代理后面的守护进程（如 /docker 前缀）不能被规范化抹掉。
        var succeeded = DockerEndpointNormalizer.TryNormalize(
            DockerHostType.Tcp,
            "tcp://proxy.example:2375/docker",
            out var normalized,
            out var error);

        succeeded.Should().BeTrue(error);
        normalized.CanonicalEndpoint.Should().Be("tcp://proxy.example:2375/docker");
    }

    [Theory]
    [InlineData(DockerHostType.Socket, "/var/run/docker.sock")]
    [InlineData(DockerHostType.Socket, "unix://var/run/docker.sock")]
    [InlineData(DockerHostType.Socket, "//./pipe/docker_engine")]
    [InlineData(DockerHostType.Tcp, "127.0.0.1")]
    [InlineData(DockerHostType.Tcp, "http://DOCKER.example:2375")]
    [InlineData(DockerHostType.Tcp, "tcp://[::1]:2375")]
    public void Normalization_ShouldBeIdempotent(DockerHostType type, string input)
    {
        DockerEndpointNormalizer.TryNormalize(type, input, out var once, out var firstError)
            .Should().BeTrue(firstError);

        DockerEndpointNormalizer.TryNormalize(type, once.CanonicalEndpoint, out var twice, out var secondError)
            .Should().BeTrue($"规范化结果「{once.CanonicalEndpoint}」必须自身合法：{secondError}");

        twice.CanonicalEndpoint.Should().Be(once.CanonicalEndpoint);
    }

    [Theory]
    [InlineData(DockerHostType.Socket, null)]
    [InlineData(DockerHostType.Socket, "")]
    [InlineData(DockerHostType.Socket, "   ")]
    [InlineData(DockerHostType.Socket, "relative/docker.sock")]
    [InlineData(DockerHostType.Socket, "/")]
    [InlineData(DockerHostType.Socket, "http://127.0.0.1:2375")]
    [InlineData(DockerHostType.Socket, @"\\.\pipe\")]
    [InlineData(DockerHostType.Socket, "npipe://")]
    [InlineData(DockerHostType.Tcp, "tcp://")]
    [InlineData(DockerHostType.Tcp, "tcp://:2375")]
    [InlineData(DockerHostType.Tcp, "tcp://host:abc")]
    [InlineData(DockerHostType.Tcp, "tcp://host:0")]
    [InlineData(DockerHostType.Tcp, "tcp://host:99999")]
    [InlineData(DockerHostType.Tcp, "https://host:2376")]
    [InlineData(DockerHostType.Tcp, "tcp://user:pass@host:2375")]

    [InlineData((DockerHostType)999, "tcp://host:2375")]
    public void InvalidEndpoints_ShouldFailWithReason(DockerHostType type, string? input)
    {
        var succeeded = DockerEndpointNormalizer.TryNormalize(type, input, out var normalized, out var error);

        succeeded.Should().BeFalse($"端点「{input}」应当被拒绝");
        error.Should().NotBeNullOrWhiteSpace();
        normalized.Should().Be(default(NormalizedDockerEndpoint));
    }
}
