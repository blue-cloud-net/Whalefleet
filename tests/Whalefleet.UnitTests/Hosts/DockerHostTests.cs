using Whalefleet.Core.Hosts;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Hosts;

/// <summary>
/// <see cref="DockerHost"/> 实体的不变量与行为测试。
/// </summary>
/// <remarks>
/// 覆盖「连接方式 ↔ 端点形态」的粗粒度一致性——明显矛盾的一对必须在实体层就被拒绝，
/// 而端点的权威规范化仍在 <c>DockerEndpointNormalizer</c>（由集成测试覆盖）。
/// </remarks>
public class DockerHostTests
{
    private static readonly DateTime _createdAt = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Register_默认启用且无认证()
    {
        var id = Guid.NewGuid();

        var host = DockerHost.Register(id, "local", DockerHostType.Socket, new HostEndpoint("/var/run/docker.sock"));

        host.Id.Should().Be(id);
        host.Name.Should().Be("local");
        host.Type.Should().Be(DockerHostType.Socket);
        host.Endpoint.Value.Should().Be("/var/run/docker.sock");
        host.Enabled.Should().BeTrue();
        host.Credentials.Should().Be(HostCredentials.None);
        host.IsDeleted.Should().BeFalse();

        // 审计由仓储填充，实体自身不取时间与用户
        host.CreationTime.Should().Be(default(DateTime));
        host.CreatorId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Register_TCP端点_允许()
    {
        var host = DockerHost.Register(
            Guid.NewGuid(),
            "remote",
            DockerHostType.Tcp,
            new HostEndpoint("tcp://10.0.0.5:2375"));

        host.Type.Should().Be(DockerHostType.Tcp);
        host.Endpoint.Value.Should().Be("tcp://10.0.0.5:2375");
    }

    [Fact]
    public void Register_去掉名称首尾空白()
    {
        var host = DockerHost.Register(
            Guid.NewGuid(),
            "  prod-host-1  ",
            DockerHostType.Socket,
            new HostEndpoint("/var/run/docker.sock"));

        host.Name.Should().Be("prod-host-1");
    }

    [Fact]
    public void Register_带凭据_凭据被保留()
    {
        var credentials = HostCredentials.FromJson(DockerHostAuthKind.Basic, """{"user":"admin","password":"x"}""");

        var host = DockerHost.Register(
            Guid.NewGuid(),
            "remote",
            DockerHostType.Tcp,
            new HostEndpoint("tcp://10.0.0.5:2375"),
            credentials);

        host.Credentials.Should().Be(credentials);
        host.Credentials.Kind.Should().Be(DockerHostAuthKind.Basic);
    }

    [Fact]
    public void Register_端点为null_抛出参数异常()
    {
        var act = () => DockerHost.Register(Guid.NewGuid(), "local", DockerHostType.Socket, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_名称为空_抛出参数异常(string name)
    {
        var act = () => DockerHost.Register(
            Guid.NewGuid(),
            name,
            DockerHostType.Socket,
            new HostEndpoint("/var/run/docker.sock"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_名称含控制字符_抛出参数异常()
    {
        var act = () => DockerHost.Register(
            Guid.NewGuid(),
            "local\u0007host",
            DockerHostType.Socket,
            new HostEndpoint("/var/run/docker.sock"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_名称超长_抛出参数异常()
    {
        var name = new string('a', DockerHost.MaxNameLength + 1);

        var act = () => DockerHost.Register(
            Guid.NewGuid(),
            name,
            DockerHostType.Socket,
            new HostEndpoint("/var/run/docker.sock"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_连接方式未知_抛出参数异常()
    {
        var act = () => DockerHost.Register(
            Guid.NewGuid(),
            "local",
            (DockerHostType)77,
            new HostEndpoint("/var/run/docker.sock"));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(DockerHostType.Socket, "tcp://10.0.0.5:2375")]
    [InlineData(DockerHostType.Socket, "http://10.0.0.5:2375")]
    [InlineData(DockerHostType.Tcp, "/var/run/docker.sock")]
    [InlineData(DockerHostType.Tcp, "unix:///var/run/docker.sock")]
    [InlineData(DockerHostType.Tcp, "npipe://./pipe/docker_engine")]
    public void Register_连接方式与端点形态矛盾_抛出参数异常(DockerHostType type, string endpoint)
    {
        var act = () => DockerHost.Register(Guid.NewGuid(), "local", type, new HostEndpoint(endpoint));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UpdateConnection_同时更换方式端点与凭据()
    {
        var host = DockerHost.Register(Guid.NewGuid(), "local", DockerHostType.Socket, new HostEndpoint("/var/run/docker.sock"));
        var credentials = HostCredentials.FromJson(DockerHostAuthKind.Tls, """{"caPath":"/certs/ca.pem"}""");

        host.UpdateConnection(DockerHostType.Tcp, new HostEndpoint("tcp://10.0.0.5:2376"), credentials);

        host.Type.Should().Be(DockerHostType.Tcp);
        host.Endpoint.Value.Should().Be("tcp://10.0.0.5:2376");
        host.Credentials.Should().Be(credentials);
    }

    [Fact]
    public void UpdateConnection_省略凭据_视为无认证()
    {
        var host = DockerHost.Register(
            Guid.NewGuid(),
            "remote",
            DockerHostType.Tcp,
            new HostEndpoint("tcp://10.0.0.5:2375"),
            HostCredentials.FromJson(DockerHostAuthKind.Basic, """{"user":"admin"}"""));

        host.UpdateConnection(DockerHostType.Tcp, new HostEndpoint("tcp://10.0.0.6:2375"));

        host.Credentials.Should().Be(HostCredentials.None);
    }

    [Fact]
    public void UpdateConnection_形态不匹配_抛出参数异常且保持原值()
    {
        var host = DockerHost.Register(Guid.NewGuid(), "local", DockerHostType.Socket, new HostEndpoint("/var/run/docker.sock"));

        var act = () => host.UpdateConnection(DockerHostType.Socket, new HostEndpoint("tcp://10.0.0.5:2375"));

        act.Should().Throw<ArgumentException>();
        host.Type.Should().Be(DockerHostType.Socket);
        host.Endpoint.Value.Should().Be("/var/run/docker.sock");
    }

    [Fact]
    public void Enable与Disable_幂等()
    {
        var host = DockerHost.Register(Guid.NewGuid(), "local", DockerHostType.Socket, new HostEndpoint("/var/run/docker.sock"));

        host.Disable();
        host.Disable();
        host.Enabled.Should().BeFalse();

        host.Enable();
        host.Enable();
        host.Enabled.Should().BeTrue();
    }

    [Fact]
    public void 继承的软删除_可用且保留创建审计()
    {
        var creator = Guid.NewGuid();
        var host = DockerHost.Register(Guid.NewGuid(), "local", DockerHostType.Socket, new HostEndpoint("/var/run/docker.sock"));
        host.MarkCreated(_createdAt, creator);

        host.Delete(_createdAt.AddMinutes(5), creator);

        host.IsDeleted.Should().BeTrue();
        host.DeletionTime.Should().Be(_createdAt.AddMinutes(5));
        host.CreationTime.Should().Be(_createdAt);
    }
}
