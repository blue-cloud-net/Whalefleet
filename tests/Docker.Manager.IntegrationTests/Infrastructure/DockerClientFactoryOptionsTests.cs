using Docker.Manager.Application.Abstractions;
using Docker.Manager.Infrastructure.DependencyInjection;
using Docker.Manager.Infrastructure.Docker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Docker.Manager.IntegrationTests.Infrastructure;

/// <summary>
/// <c>Docker</c> 配置节的绑定与<b>启动时校验</b>测试。
/// </summary>
/// <remarks>
/// 配置错误必须在启动时暴露（<c>ValidateOnStart</c>），而不是拖到第一次连接——
/// 后者会让"服务起来了但所有 Docker 操作都失败"这种故障排查变得很费劲。
/// 这里直接解析 <see cref="IOptions{T}"/>：访问 <c>Value</c> 即触发全部校验器，
/// 与宿主启动时 <c>ValidateOnStart</c> 走的是同一条路径。
/// </remarks>
public class DockerClientFactoryOptionsTests
{
    [Fact]
    public void Hosts_ShouldBindNameTypeEndpointAndCachePolicy()
    {
        var options = Resolve(BuildConfiguration(
            ("Docker:Hosts:0:Name", "local"),
            ("Docker:Hosts:0:Type", "Socket"),
            ("Docker:Hosts:0:Endpoint", "/var/run/docker.sock"),
            ("Docker:Hosts:1:Name", "prod"),
            ("Docker:Hosts:1:Type", "Tcp"),
            ("Docker:Hosts:1:Endpoint", "tcp://docker.example:2375"),
            ("Docker:ClientCache:IdleTimeout", "00:15:00"),
            ("Docker:ClientCache:ReapInterval", "00:02:00")));

        options.Hosts.Should().HaveCount(2);
        options.Hosts[0].Name.Should().Be("local");
        options.Hosts[0].Type.Should().Be(DockerHostType.Socket);
        options.Hosts[0].Endpoint.Should().Be("/var/run/docker.sock");
        options.Hosts[1].Name.Should().Be("prod");
        options.Hosts[1].Type.Should().Be(DockerHostType.Tcp);
        options.Hosts[1].Endpoint.Should().Be("tcp://docker.example:2375");
        options.ClientCache.IdleTimeout.Should().Be(TimeSpan.FromMinutes(15));
        options.ClientCache.ReapInterval.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void CachePolicy_ShouldFallBackToDocumentedDefaults()
    {
        var options = Resolve(BuildConfiguration(
            ("Docker:Hosts:0:Name", "local"),
            ("Docker:Hosts:0:Endpoint", "/var/run/docker.sock")));

        options.ClientCache.IdleTimeout.Should().Be(TimeSpan.FromMinutes(30));
        options.ClientCache.ReapInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void DuplicateEndpointsAcrossDistinctNames_ShouldBeAllowed()
    {
        // 两个主机名指向同一守护进程是合法（且被鼓励）的用法：它们共享同一个客户端。
        var options = Resolve(BuildConfiguration(
            ("Docker:Hosts:0:Name", "local"),
            ("Docker:Hosts:0:Endpoint", "/var/run/docker.sock"),
            ("Docker:Hosts:1:Name", "local-alias"),
            ("Docker:Hosts:1:Endpoint", "unix:///var/run/docker.sock")));

        options.Hosts.Should().HaveCount(2);
    }

    [Fact]
    public void MissingHostConfiguration_ShouldFailStartupValidation()
    {
        var act = () => Resolve(BuildConfiguration());

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Docker:Hosts");
    }

    [Fact]
    public void DuplicateHostNames_ShouldFailStartupValidation()
    {
        // 不区分大小写：LOCAL 与 local 视为同一个主机。
        var act = () => Resolve(BuildConfiguration(
            ("Docker:Hosts:0:Name", "local"),
            ("Docker:Hosts:0:Endpoint", "/var/run/docker.sock"),
            ("Docker:Hosts:1:Name", "LOCAL"),
            ("Docker:Hosts:1:Endpoint", "tcp://127.0.0.1:2375")));

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("不能重复");
    }

    [Fact]
    public void EmptyHostName_ShouldFailStartupValidation()
    {
        var act = () => Resolve(BuildConfiguration(
            ("Docker:Hosts:0:Name", " "),
            ("Docker:Hosts:0:Endpoint", "/var/run/docker.sock")));

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("Name");
    }

    [Theory]
    [InlineData("relative/docker.sock")]
    [InlineData("http://127.0.0.1:2375")]
    [InlineData("tcp://host:99999")]
    public void InvalidEndpoint_ShouldFailStartupValidation(string endpoint)
    {
        var act = () => Resolve(BuildConfiguration(
            ("Docker:Hosts:0:Name", "local"),
            ("Docker:Hosts:0:Endpoint", endpoint)));

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("端点非法");
    }

    [Fact]
    public void NegativeCachePolicy_ShouldFailStartupValidation()
    {
        var act = () => Resolve(BuildConfiguration(
            ("Docker:Hosts:0:Name", "local"),
            ("Docker:Hosts:0:Endpoint", "/var/run/docker.sock"),
            ("Docker:ClientCache:IdleTimeout", "-00:01:00")));

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("不能为负值");
    }

    private static DockerClientFactoryOptions Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<DockerClientFactoryOptions>>().Value;
    }

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal))
            .Build();
}
