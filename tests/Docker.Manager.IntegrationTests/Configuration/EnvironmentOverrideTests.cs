using Docker.Manager.Api.Configuration;
using Docker.Manager.Infrastructure.Docker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Docker.Manager.IntegrationTests.Configuration;

/// <summary>
/// 环境变量覆盖配置的端到端测试：环境变量 → 配置键 → <c>IOptions</c> 绑定值。
/// </summary>
/// <remarks>
/// <para>与 <see cref="EnvironmentVariableMappingTests"/> 的分工：后者只验证配置系统的键映射，
/// 本类验证真实宿主的 <c>IOptions</c> 最终拿到了被覆盖的值——即
/// <c>docs/constraints.md</c>「配置系统」小节承诺的行为。</para>
/// <para>本类通过 <see cref="EnvironmentVariablesCollectionDefinition"/> 与其它测试串行执行，
/// 以避免进程级环境变量污染并发构建的宿主。</para>
/// </remarks>
[Collection(EnvironmentVariablesCollectionDefinition.Name)]
public class EnvironmentOverrideTests
{
    private const string EndpointVariable = "DOCKER__HOSTS__0__ENDPOINT";
    private const string CorsOriginVariable = "CORS__ALLOWEDORIGINS__0";

    [Fact]
    public void WithoutOverride_ShouldUseDocumentedDefaultSocketPath()
    {
        RunWithEnvironmentVariable(EndpointVariable, null, () =>
        {
            using var factory = new ApiFactory();
            using var client = factory.CreateClient();

            var options = factory.Services.GetRequiredService<IOptions<DockerClientFactoryOptions>>();

            options.Value.Hosts[0].Endpoint.Should().Be("/var/run/docker.sock");
        });
    }

    [Fact]
    public void EnvironmentVariable_ShouldOverrideFirstHostEndpoint()
    {
        RunWithEnvironmentVariable(EndpointVariable, "/tmp/dm-env-override.sock", () =>
        {
            using var factory = new ApiFactory();
            using var client = factory.CreateClient();

            var options = factory.Services.GetRequiredService<IOptions<DockerClientFactoryOptions>>();

            options.Value.Hosts[0].Endpoint.Should().Be("/tmp/dm-env-override.sock");
        });
    }

    [Fact]
    public void InMemorySetting_ShouldOverrideFirstHostEndpoint()
    {
        // 不使用环境变量的等价路径（WebApplicationFactory.UseSetting 语义），
        // 用于证明「后加载的配置源覆盖先加载的」这一顺序规律。
        using var factory = new ApiFactory()
            .WithSetting("Docker:Hosts:0:Endpoint", "/tmp/dm-setting.sock");

        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<DockerClientFactoryOptions>>();

        options.Value.Hosts[0].Endpoint.Should().Be("/tmp/dm-setting.sock");
    }

    [Fact]
    public void EnvironmentVariable_ShouldConfigureCorsWhitelist()
    {
        RunWithEnvironmentVariable(CorsOriginVariable, "http://env.example", () =>
        {
            using var factory = new ApiFactory();
            using var client = factory.CreateClient();

            var options = factory.Services.GetRequiredService<IOptions<CorsOptions>>();

            options.Value.AllowedOrigins.Should().Equal("http://env.example");
        });
    }

    [Fact]
    public void InvalidCorsOrigin_ShouldFailFastAtStartup()
    {
        // 尾斜杠是 CORS 白名单最常见的配置错误（浏览器与 ASP.NET Core 均不接受），
        // 必须在启动时就暴露，而不是等到第一次跨域请求。
        using var factory = new ApiFactory()
            .WithSetting("Cors:AllowedOrigins:0", "http://allowed.example/");

        Action act = () => factory.CreateClient();

        act.Should().Throw<Exception>();
    }

    private static void RunWithEnvironmentVariable(string name, string? value, Action assertion)
    {
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);

        try
        {
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
