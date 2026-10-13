using Docker.Manager.Application.Abstractions;
using Docker.Manager.Core.Auth;
using Docker.Manager.Infrastructure.DependencyInjection;
using Docker.Manager.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Docker.Manager.IntegrationTests.Security;

/// <summary>
/// <c>Argon2PasswordHasher</c> 的端到端行为测试。
/// </summary>
/// <remarks>
/// <para><b>为什么在 IntegrationTests</b>：实现类位于 <c>Infrastructure</c>，而 <c>UnitTests</c>
/// 被禁止引用该层（见 <c>docs/architecture.md</c> 的依赖方向规则）。这里与
/// <c>Infrastructure/DockerClientFactoryTests</c> 同例——测试真实的 DI 注册与真实算法，而不需要 HTTP 宿主。</para>
/// <para><b>算力成本</b>：除少量"默认参数"用例外，其余用例显式把参数降到
/// <c>m=8192, t=1, p=1</c>，以免整套测试被 Argon2 拖慢（默认参数单次哈希约数十至数百毫秒）。</para>
/// </remarks>
public class Argon2PasswordHasherTests
{
    private const string _password = "StrongP@ss123";

    /// <summary>
    /// 便宜的测试参数：8 MiB 内存、1 次迭代、单线程。
    /// </summary>
    private static readonly (string Key, string? Value)[] _cheapSettings =
    [
        ("Security:Argon2:MemorySizeKiB", "8192"),
        ("Security:Argon2:Iterations", "1"),
        ("Security:Argon2:Parallelism", "1")
    ];

    [Fact]
    public void Hash_产出标准PHC编码串_且参数取自配置()
    {
        var hasher = ResolveHasher(_cheapSettings);

        var encoded = hasher.Hash(_password);

        encoded.Should().StartWith("$argon2id$v=19$m=8192,t=1,p=1$");

        var parsed = PasswordHash.Parse(encoded);
        parsed.MemorySizeKiB.Should().Be(8192);
        parsed.Iterations.Should().Be(1);
        parsed.Parallelism.Should().Be(1);
        parsed.Salt.Should().HaveCount(16);
        parsed.Digest.Should().HaveCount(32);
    }

    [Fact]
    public void Hash_默认参数为文档值()
    {
        var encoded = ResolveHasher().Hash(_password);

        // constraints.md 的关键环境变量表：m=65536（64 MiB）、t=3、p=4
        encoded.Should().StartWith("$argon2id$v=19$m=65536,t=3,p=4$");
    }

    [Fact]
    public void Hash_同一密码两次哈希不同_因为盐随机()
    {
        var hasher = ResolveHasher(_cheapSettings);

        var first = hasher.Hash(_password);
        var second = hasher.Hash(_password);

        first.Should().NotBe(second);
        PasswordHash.Parse(first).Salt.Should().NotEqual(PasswordHash.Parse(second).Salt);
    }

    [Fact]
    public void Hash_结果中不含明文密码()
    {
        var hasher = ResolveHasher(_cheapSettings);

        var encoded = hasher.Hash(_password);

        encoded.Should().NotContain(_password);
        encoded.Should().NotContain("StrongP");
    }

    [Fact]
    public void Verify_正确密码通过()
    {
        var hasher = ResolveHasher(_cheapSettings);
        var encoded = hasher.Hash(_password);

        hasher.Verify(_password, encoded).Should().BeTrue();
    }

    [Theory]
    [InlineData("StrongP@ss124")] // 尾字符不同
    [InlineData("strongp@ss123")] // 大小写不同
    [InlineData("StrongP@ss12")] // 少一字符
    [InlineData("")]
    public void Verify_错误或空密码失败(string candidate)
    {
        var hasher = ResolveHasher(_cheapSettings);
        var encoded = hasher.Hash(_password);

        hasher.Verify(candidate, encoded).Should().BeFalse();
    }

    [Fact]
    public void Verify_摘要被篡改_失败()
    {
        var hasher = ResolveHasher(_cheapSettings);
        var encoded = hasher.Hash(_password);

        // 改摘要段的**首个**字符：整串仍是合法 Base64，但摘要的首字节必然不同。
        // （刻意不碰末字符——Base64 末字符的低位可能属于被丢弃的填充位，改了也解出同样的字节。）
        var segments = encoded.Split('$');
        var digest = segments[5];
        segments[5] = (digest[0] == 'A' ? 'B' : 'A') + digest[1..];

        var tampered = String.Join('$', segments);

        tampered.Should().NotBe(encoded);
        hasher.Verify(_password, tampered).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$m=8192,t=1,p=1$********$AAAAAA")]
    [InlineData("$argon2id$v=16$m=8192,t=1,p=1$AAAAAAAAAAA$AAAAAA")]
    public void Verify_哈希格式非法_返回false而不抛异常(string encoded)
    {
        var hasher = ResolveHasher(_cheapSettings);

        // 关键契约：损坏的库行必须表现为"认证失败"（401），而不是服务器内部错误（500）。
        var act = () => hasher.Verify(_password, encoded);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Fact]
    public void Verify_使用哈希自身的参数_而非当前配置()
    {
        // 先用"昂贵"参数生成哈希……
        var expensive = ResolveHasher(("Security:Argon2:MemorySizeKiB", "16384"));

        var encoded = expensive.Hash(_password);

        // ……再用"便宜"配置的实例校验：只要参数随哈希串一同解析，就应当通过。
        var cheap = ResolveHasher(_cheapSettings);

        cheap.Verify(_password, encoded).Should().BeTrue();
    }

    [Fact]
    public void Hash_空密码_抛出参数异常()
    {
        var hasher = ResolveHasher(_cheapSettings);

        var act = () => hasher.Hash("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Security:Argon2:MemorySizeKiB", "8", "Security:Argon2:Parallelism", "4")] // m < 8p
    [InlineData("Security:Argon2:Iterations", "0", "Security:Argon2:Parallelism", "1")]
    [InlineData("Security:Argon2:SaltSizeBytes", "4", "Security:Argon2:Parallelism", "1")]
    [InlineData("Security:Argon2:HashSizeBytes", "1", "Security:Argon2:Parallelism", "1")]
    public void 非法参数在启动校验时失败(
        string key1,
        string value1,
        string key2,
        string value2)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(BuildConfiguration((key1, value1), (key2, value2)));

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<Argon2Options>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    private static IPasswordHasher ResolveHasher(params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(BuildConfiguration(settings));

        var provider = services.BuildServiceProvider();

        // 哈希器是无状态 Singleton；其构造时已把选项实体取出，故 provider 的生命周期不影响后续使用。
        return provider.GetRequiredService<IPasswordHasher>();
    }

    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                setting => setting.Key,
                setting => setting.Value,
                StringComparer.Ordinal))
            .Build();
}
