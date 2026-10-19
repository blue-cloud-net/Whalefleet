using System.Security.Cryptography;
using System.Text;
using Whalefleet.Application.Common;
using Whalefleet.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace Whalefleet.IntegrationTests.Security;

/// <summary>
/// Registry 口令加密（<see cref="RegistrySecretCipher"/>）的测试。
/// </summary>
/// <remarks>
/// <para>本组用例断言的是<b>三条安全性质</b>，而不是「能加密能解密」这种平凡正确：</para>
/// <list type="number">
///   <item><description><b>密文不泄露明文</b>——落库的是密文，不是口令；</description></item>
///   <item><description><b>同一口令两次加密得到不同密文</b>（随机 nonce），
///   否则攻击者可以从密文的重复中推断口令相同；</description></item>
///   <item><description><b>密文被篡改时解密必须失败</b>（GCM 认证标签），
///   而不是解出一段垃圾明文——后者会让系统拿着错误的凭据去登录别人的仓库。</description></item>
/// </list>
/// <para>放在 IntegrationTests 而非 UnitTests：<see cref="RegistrySecretCipher"/> 是
/// Infrastructure 的 internal 类型，而 UnitTests <b>不得</b>引用 Infrastructure
/// （依赖方向约束）。</para>
/// </remarks>
public class RegistrySecretCipherTests
{
    private static readonly byte[] _key =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    ];

    [Fact]
    public void 加密后能原样解出()
    {
        var cipher = RegistrySecretCipher.Encrypt("s3cr3t-p@ssw0rd", _key);

        RegistrySecretCipher.Decrypt(cipher, _key).Should().Be("s3cr3t-p@ssw0rd");
    }

    [Fact]
    public void 密文不含明文()
    {
        const string plaintext = "super-secret-value";

        var cipher = RegistrySecretCipher.Encrypt(plaintext, _key);

        cipher.Should().NotContain(plaintext);
        Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(cipher)))
            .Should().NotContain(plaintext);
    }

    /// <summary>
    /// 随机 nonce 的直接后果：同一口令两次加密必须得到<b>不同</b>密文。
    /// </summary>
    [Fact]
    public void 同一口令两次加密得到不同密文()
    {
        var first = RegistrySecretCipher.Encrypt("same-password", _key);
        var second = RegistrySecretCipher.Encrypt("same-password", _key);

        first.Should().NotBe(second);
        first.Should().NotBe(second);
    }

    [Fact]
    public void 用错密钥解密失败()
    {
        var cipher = RegistrySecretCipher.Encrypt("s3cr3t", _key);
        var otherKey = RandomNumberGenerator.GetBytes(RegistrySecretCipher.KeySizeBytes);

        var act = () => RegistrySecretCipher.Decrypt(cipher, otherKey);

        act.Should().Throw<CryptographicException>();
    }

    /// <summary>
    /// 密文被改一个字节也必须解不开——这是 GCM 认证标签的价值所在。
    /// </summary>
    [Fact]
    public void 密文被篡改后解密失败()
    {
        var cipher = RegistrySecretCipher.Encrypt("s3cr3t", _key);
        var payload = Convert.FromBase64String(PadBase64(cipher));

        // 改动密文部分的最后一个字节（不碰 nonce 与 tag 的语义位）
        payload[^1] ^= 0xFF;

        var tampered = Convert.ToBase64String(payload);

        var act = () => RegistrySecretCipher.Decrypt(tampered, _key);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void 密文被截断后解密失败()
    {
        var cipher = RegistrySecretCipher.Encrypt("s3cr3t", _key);
        var payload = Convert.FromBase64String(PadBase64(cipher));

        var act = () => RegistrySecretCipher.Decrypt(
            Convert.ToBase64String(payload[..8]),
            _key);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void 密文不是合法Base64Url时抛格式错()
    {
        var act = () => RegistrySecretCipher.Decrypt("这不是 base64!!", _key);

        act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 判定密文格式_空值返回false(string? value)
    {
        RegistrySecretCipher.IsProtectedText(value).Should().BeFalse();
    }

    [Fact]
    public void 判定密文格式_本类产出的密文返回true()
    {
        var cipher = RegistrySecretCipher.Encrypt("s3cr3t", _key);

        RegistrySecretCipher.IsProtectedText(cipher).Should().BeTrue();
    }

    [Fact]
    public void 判定密文格式_普通文本返回false()
    {
        RegistrySecretCipher.IsProtectedText("plain-text-password").Should().BeFalse();
    }

    [Fact]
    public void 密钥长度不对时抛加密异常()
    {
        var act = () => RegistrySecretCipher.Encrypt("s3cr3t", new byte[16]);

        act.Should().Throw<CryptographicException>()
            .WithMessage("*32*");
    }

    /// <summary>
    /// Base64Url 编码去掉了 <c>=</c> 填充，解码前需要补回来。
    /// </summary>
    private static string PadBase64(string base64Url)
    {
        var normalized = base64Url.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;

        return padding switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };
    }
}
