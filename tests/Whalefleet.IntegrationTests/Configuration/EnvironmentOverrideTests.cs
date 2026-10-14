using Whalefleet.Api.Configuration;
using Whalefleet.Infrastructure.Docker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Whalefleet.IntegrationTests.Configuration;

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
    private const string IdleTimeoutVariable = "DOCKER__POOL__IDLETIMEOUT";
    private const string CorsOriginVariable = "CORS__ALLOWEDORIGINS__0";

    [Fact]
    public void WithoutOverride_ShouldUseDocumentedDefaultIdleTimeout()
    {
        RunWithEnvironmentVariable(IdleTimeoutVariable, null, () =>
        {
            using var factory = new ApiFactory();
            using var client = factory.CreateClient();

            var options = factory.Services.GetRequiredService<IOptions<DockerClientFactoryOptions>>();

            options.Value.Pool.IdleTimeout.Should().Be(TimeSpan.FromMinutes(30));
        });
    }

    [Fact]
    public void EnvironmentVariable_ShouldOverridePoolIdleTimeout()
    {
        RunWithEnvironmentVariable(IdleTimeoutVariable, "00:07:00", () =>
        {
            using var factory = new ApiFactory();
            using var client = factory.CreateClient();

            var options = factory.Services.GetRequiredService<IOptions<DockerClientFactoryOptions>>();

            options.Value.Pool.IdleTimeout.Should().Be(TimeSpan.FromMinutes(7));
        });
    }

    [Fact]
    public void InMemorySetting_ShouldOverridePoolIdleTimeout()
    {
        // 不使用环境变量的等价路径（WebApplicationFactory.UseSetting 语义），
        // 用于证明「后加载的配置源覆盖先加载的」这一顺序规律。
        using var factory = new ApiFactory()
            .WithSetting("Docker:Pool:IdleTimeout", "00:09:00");

        using var client = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<DockerClientFactoryOptions>>();

        options.Value.Pool.IdleTimeout.Should().Be(TimeSpan.FromMinutes(9));
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
