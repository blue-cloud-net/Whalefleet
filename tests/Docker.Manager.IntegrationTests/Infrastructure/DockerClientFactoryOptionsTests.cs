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
/// <para>配置错误必须在启动时暴露（<c>ValidateOnStart</c>），而不是拖到第一次连接——
/// 后者会让"服务起来了但所有 Docker 操作都失败"这种故障排查变得很费劲。
/// 这里直接解析 <see cref="IOptions{T}"/>：访问 <c>Value</c> 即触发全部校验器，
/// 与宿主启动时 <c>ValidateOnStart</c> 走的是同一条路径。</para>
/// <para><b>主机不在配置里</b>（0.0.8 起）：主机是 <c>hosts</c> 表里的数据，由
/// <c>IDockerHostAccessor</c> 读取，因此本节的校验只覆盖池策略。</para>
/// </remarks>
public class DockerClientFactoryOptionsTests
{
    [Fact]
    public void Pool_ShouldBindCachePolicy()
    {
        var options = Resolve(BuildConfiguration(
            ("Docker:Pool:IdleTimeout", "00:15:00"),
            ("Docker:Pool:ReapInterval", "00:02:00")));

        options.Pool.IdleTimeout.Should().Be(TimeSpan.FromMinutes(15));
        options.Pool.ReapInterval.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Pool_ShouldFallBackToDocumentedDefaults()
    {
        var options = Resolve(BuildConfiguration());

        options.Pool.IdleTimeout.Should().Be(TimeSpan.FromMinutes(30));
        options.Pool.ReapInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void NegativeIdleTimeout_ShouldFailStartupValidation()
    {
        var act = () => Resolve(BuildConfiguration(("Docker:Pool:IdleTimeout", "-00:01:00")));

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("不能为负值");
    }

    [Fact]
    public void NegativeReapInterval_ShouldFailStartupValidation()
    {
        var act = () => Resolve(BuildConfiguration(("Docker:Pool:ReapInterval", "-00:01:00")));

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
