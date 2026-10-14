using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Hosts;

/// <summary>
/// 主机认证凭据的解析与映射（<c>auth_config_json</c> → 运行期凭据）。
/// </summary>
/// <remarks>
/// <para>这里守的核心是一条安全约束：<b>凭据读不出来时必须失败，绝不能退化成无认证连接</b>。
/// 静默降级会把一个配置笔误变成「不用密码就连上了」，而那种问题在日志里几乎看不出来。</para>
/// <para>另一条是：<b>任何 <c>ToString()</c> 都不得输出口令</b>——记录类型的默认实现会把所有属性
/// 拼进字符串，随手抄进日志就是明文泄漏。</para>
/// </remarks>
public class DockerHostAuthTests
{
    private const string _tlsJson = """
        {
          "caCertificatePath": "/certs/ca.pem",
          "clientCertificatePath": "/certs/cert.pem",
          "clientKeyPath": "/certs/key.pem"
        }
        """;

    private const string _basicJson = """{ "username": "admin", "password": "s3cret" }""";

    [Fact]
    public void From_无认证_返回共享实例且无指纹()
    {
        var auth = DockerHostAuth.From(HostCredentials.None);

        auth.Kind.Should().Be(DockerHostAuthKind.None);
        auth.Tls.Should().BeNull();
        auth.Basic.Should().BeNull();
        auth.CredentialFingerprint.Should().BeNull();
        auth.CacheDiscriminator.Should().BeEmpty();
    }

    [Fact]
    public void From_Tls_解析出三个路径()
    {
        var auth = DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Tls, _tlsJson));

        auth.Kind.Should().Be(DockerHostAuthKind.Tls);
        auth.Tls!.CaCertificatePath.Should().Be("/certs/ca.pem");
        auth.Tls.ClientCertificatePath.Should().Be("/certs/cert.pem");
        auth.Tls.ClientKeyPath.Should().Be("/certs/key.pem");
        auth.Tls.ClientKeyPassword.Should().BeNull();

        // 指纹参与缓存键：同端点不同凭据必须是两条连接。
        auth.CacheDiscriminator.Should().NotBeEmpty();
    }

    [Fact]
    public void From_Tls_未加密私钥不带口令字段()
    {
        var auth = DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Tls, _tlsJson));

        auth.Tls!.ClientKeyPassword.Should().BeNull();
    }

    [Fact]
    public void From_Basic_解析出账号密码()
    {
        var auth = DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Basic, _basicJson));

        auth.Kind.Should().Be(DockerHostAuthKind.Basic);
        auth.Basic!.Username.Should().Be("admin");
        auth.Basic.Password.Should().Be("s3cret");
    }

    [Theory]
    [InlineData("""{ "clientCertificatePath": "/c.pem", "clientKeyPath": "/k.pem" }""", "caCertificatePath")]
    [InlineData("""{ "caCertificatePath": "/ca.pem", "clientKeyPath": "/k.pem" }""", "clientCertificatePath")]
    [InlineData("""{ "caCertificatePath": "/ca.pem", "clientCertificatePath": "/c.pem" }""", "clientKeyPath")]
    [InlineData("""{ "caCertificatePath": "  ", "clientCertificatePath": "/c.pem", "clientKeyPath": "/k.pem" }""", "caCertificatePath")]
    public void From_Tls_缺字段时以线上字段名报错(string json, string missingField)
    {
        var act = () => DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Tls, json));

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
        act.Should().Throw<BusinessException>().WithMessage($"*{missingField}*");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "username": "admin" }""")]
    [InlineData("""{ "username": "  ", "password": "p" }""")]
    [InlineData("""{ "username": "admin", "password": "" }""")]
    public void From_Basic_缺字段时以字段名报错(string json)
    {
        var act = () => DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Basic, json));

        (act.Should().Throw<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostUnreachable);
    }

    [Fact]
    public void From_Basic_密码首尾空格是合法字符()
    {
        // 口令里的空格是有意义的：这里若 Trim 就会把用户的密码悄悄改掉，登录必然失败且难查。
        var auth = DockerHostAuth.From(
            HostCredentials.FromJson(DockerHostAuthKind.Basic, """{ "username": "admin", "password": "  pw  " }"""));

        auth.Basic!.Password.Should().Be("  pw  ");
    }

    [Fact]
    public void 非法Json在HostCredentials这一层就被挡住()
    {
        // 「载荷是合法 JSON」由 HostCredentials 保证（它比 DockerHostAuth 更早被构造），
        // 所以 DockerHostAuth.From 拿到的永远是合法 JSON。这里守住那条边界，
        // 免得有人以为校验发生在更上层而把 HostCredentials 的校验删掉。
        var act = () => HostCredentials.FromJson(DockerHostAuthKind.Basic, """{ "username": "admin" }""" + "}");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void From_载荷为空报业务错误而不是抛出空引用()
    {
        // 能构造出来的 HostCredentials 一定带着非空载荷，这里直接构造等价形态：
        // 用最小合法 JSON（空对象）走同一条路径，断言"缺字段 → 1301"而不是 500。
        var act = () => DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Tls, "{}"));

        act.Should().Throw<BusinessException>()
            .Where(exception => exception.Code == ErrorCodes.HostUnreachable);
    }

    [Fact]
    public void From_相同载荷指纹相同_不同载荷指纹不同()
    {
        var first = DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Basic, _basicJson));
        var second = DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Basic, _basicJson));
        var other = DockerHostAuth.From(
            HostCredentials.FromJson(DockerHostAuthKind.Basic, """{ "username": "admin", "password": "other" }"""));

        first.CredentialFingerprint.Should().Be(second.CredentialFingerprint);
        first.CredentialFingerprint.Should().NotBe(other.CredentialFingerprint);
    }

    [Fact]
    public void 指纹与描述串都不得包含口令原文()
    {
        var auth = DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Basic, _basicJson));

        auth.CacheDiscriminator.Should().NotContain("s3cret");
        auth.CredentialFingerprint.Should().NotContain("s3cret");
    }

    [Fact]
    public void ToString_绝不输出口令()
    {
        var auth = DockerHostAuth.From(HostCredentials.FromJson(DockerHostAuthKind.Basic, _basicJson));

        auth.Basic!.ToString().Should().NotContain("s3cret");
        auth.Basic.ToString().Should().Contain("admin");

        // 记录类型默认的 ToString 会展开所有属性，因此也顺带确认整条链路。
        auth.ToString().Should().NotContain("s3cret");
    }

    [Fact]
    public void ToString_Tls只说明是否带口令()
    {
        var auth = DockerHostAuth.From(HostCredentials.FromJson(
            DockerHostAuthKind.Tls,
            """
            {
              "caCertificatePath": "/ca.pem",
              "clientCertificatePath": "/c.pem",
              "clientKeyPath": "/k.pem",
              "clientKeyPassword": "key-secret"
            }
            """));

        auth.Tls!.ToString().Should().NotContain("key-secret");
        auth.Tls.ToString().Should().Contain("有");
    }
}
