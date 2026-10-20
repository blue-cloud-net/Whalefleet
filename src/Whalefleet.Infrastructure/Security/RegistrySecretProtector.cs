using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// <see cref="IRegistrySecretProtector"/> 的实现：基于 AES-GCM 与数据卷上的密钥文件。
/// </summary>
/// <remarks>
/// <para><b>异常不外泄细节</b>：解密失败（密钥不匹配、密文被篡改）在内部是
/// <see cref="System.Security.Cryptography.CryptographicException"/>，
/// 但对外一律转成 <c>1000</c>（参数非法）。理由：异常消息会进 HTTP 响应，
/// 而「密钥不匹配」这类信息对调用方没有可操作性——能做的只有「重新填一次口令」。</para>
/// <para><b>不记录任何口令内容</b>：本类<b>刻意没有</b> <c>ILogger</c> 字段——
/// 一旦有日志，就多了一个「顺手打个 debug 日志」的机会，而那等于把口令写进日志文件。</para>
/// </remarks>
internal sealed class RegistrySecretProtector : IRegistrySecretProtector
{
    private readonly RegistrySecretKeyStore _keyStore;

    /// <summary>
    /// 初始化保护器。
    /// </summary>
    /// <param name="keyStore">密钥来源。</param>
    /// <exception cref="ArgumentNullException"><paramref name="keyStore"/> 为 <see langword="null"/>。</exception>
    public RegistrySecretProtector(RegistrySecretKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(keyStore);

        _keyStore = keyStore;
    }

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        return RegistrySecretCipher.Encrypt(plaintext, _keyStore.GetKey());
    }

    /// <inheritdoc />
    public string Unprotect(string protectedText)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedText);

        try
        {
            return RegistrySecretCipher.Decrypt(protectedText, _keyStore.GetKey());
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException
            or FormatException)
        {
            // 密钥不匹配或密文损坏。对外只说「口令密文无法解密，请重新填写」，
            // 不暴露「是密钥不对还是密文被改」——两者对用户的动作是一样的。
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                "Registry 口令密文无法解密（密钥与数据不匹配），请重新填写该 Registry 的口令");
        }
    }
}
