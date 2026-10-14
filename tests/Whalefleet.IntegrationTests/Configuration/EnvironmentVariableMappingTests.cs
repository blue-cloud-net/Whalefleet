using Whalefleet.Api.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Whalefleet.IntegrationTests.Configuration;

/// <summary>
/// 环境变量 → 配置键的映射约定测试（纯配置系统，不启动宿主）。
/// </summary>
/// <remarks>
/// <para>本项目约定：环境变量用 <c>__</c>（双下划线）表达配置层级 <c>:</c>，
/// 数组用数字下标表达，例如 <c>CORS__ALLOWEDORIGINS__0</c>。</para>
/// <para>这里同时锁定一个易被误解的事实：<b>配置键的查找是大小写不敏感的</b>
/// （<c>ConfigurationKeyComparer</c> 与各 provider 内部的字典均使用 OrdinalIgnoreCase），
/// 因此在 Linux 上设置大写的 <c>DOCKER__POOL__IDLETIMEOUT</c>
/// 可以正常绑定到 <c>Docker:Pool:IdleTimeout</c> 配置节。</para>
/// </remarks>
[Collection(EnvironmentVariablesCollectionDefinition.Name)]
public class EnvironmentVariableMappingTests
{
    private const string IdleTimeoutVariable = "DOCKER__POOL__IDLETIMEOUT";
    private const string CorsOriginVariable = "CORS__ALLOWEDORIGINS__0";

    [Fact]
    public void DoubleUnderscore_ShouldMapToConfigurationSectionSeparator()
    {
        RunWithEnvironmentVariable(IdleTimeoutVariable, "00:07:00", () =>
        {
            var configuration = BuildFromEnvironment();

            configuration["Docker:Pool:IdleTimeout"].Should().Be("00:07:00");
        });
    }

    [Fact]
    public void DoubleUnderscore_ShouldBeCaseInsensitiveWhenReadingBack()
    {
        RunWithEnvironmentVariable(IdleTimeoutVariable, "00:07:00", () =>
        {
            var configuration = BuildFromEnvironment();

            // 环境变量定义的是 DOCKER:POOL:IDLETIMEOUT，此处用小写读取，
            // 用于证明配置键比较不区分大小写。
            configuration["docker:pool:idletimeout"].Should().Be("00:07:00");
        });
    }

    [Fact]
    public void NumberedSuffix_ShouldMapToArrayIndex()
    {
        RunWithEnvironmentVariable(CorsOriginVariable, "http://env.example", () =>
        {
            var configuration = BuildFromEnvironment();

            configuration["Cors:AllowedOrigins:0"].Should().Be("http://env.example");
        });
    }

    [Fact]
    public void BoundOptions_ShouldReceiveMappedValues()
    {
        RunWithEnvironmentVariable(CorsOriginVariable, "http://env.example", () =>
        {
            var configuration = BuildFromEnvironment();

            var options = configuration
                .GetSection(CorsOptions.SectionName)
                .Get<CorsOptions>();

            options.Should().NotBeNull();
            options!.AllowedOrigins.Should().Equal("http://env.example");
        });
    }

    private static IConfigurationRoot BuildFromEnvironment() =>
        new ConfigurationBuilder().AddEnvironmentVariables().Build();

    private static void RunWithEnvironmentVariable(string name, string value, Action assertion)
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
