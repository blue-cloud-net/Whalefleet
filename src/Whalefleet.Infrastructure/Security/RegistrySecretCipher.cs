using System.Security.Cryptography;
using System.Text;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// Registry 口令的加密 / 解密（AES-GCM）。
/// </summary>
/// <remarks>
/// <para><b>为什么用 AES-GCM 而不是 AES-CBC</b>：GCM 是带认证的加密模式，
/// 密文被篡改（哪怕只改一个字节）时解密会直接失败，而不是解出一段垃圾明文。
/// 凭据被篡改的后果是「拿错误的账号去登录别人的仓库」，
/// 因此「密文损坏必须响亮地失败」比「静默解出垃圾」重要得多。</para>
/// <para><b>每次加密都用随机 nonce</b>：同一口令两次加密得到不同密文，
/// 因此无法从密文长度或内容推断口令强度，也无法用「密文相同 → 口令相同」做关联分析。
/// nonce 与密文一起存储（见 <see cref="Encrypt"/> 的输出格式）。</para>
/// <para><b>输出格式</b>：<c>base64url(nonce ‖ tag ‖ ciphertext)</c>。
/// 单个字符串、无分隔符——放进 SQLite 的 TEXT 列不需要再处理转义，
/// 也不会因为某个字段被截断而出现「前半段能解后半段不能」的诡异状态。</para>
/// <para><b>密钥从哪来</b>：由 <see cref="RegistrySecretKeyStore"/> 提供（与 JWT 密钥同源，
/// 落在数据卷下 0600 的文件里）。本类只管加解密，不关心密钥来源。</para>
/// </remarks>
internal static class RegistrySecretCipher
{
    /// <summary>
    /// AES-GCM 的 nonce 字节数（96 位，是 GCM 的推荐长度）。
    /// </summary>
    internal const int NonceSizeBytes = 12;

    /// <summary>
    /// GCM 认证标签字节数（128 位）。
    /// </summary>
    internal const int TagSizeBytes = 16;

    /// <summary>
    /// 密钥字节数（AES-256）。
    /// </summary>
    internal const int KeySizeBytes = 32;

    /// <summary>
    /// 把明文口令加密成可安全落库的密文。
    /// </summary>
    /// <param name="plaintext">明文口令。</param>
    /// <param name="key">密钥材料（长度须为 <see cref="KeySizeBytes"/>）。</param>
    /// <returns>Base64Url 文本（<c>nonce ‖ tag ‖ ciphertext</c>）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="CryptographicException">密钥长度不合法，或加密失败。</exception>
    internal static string Encrypt(string plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeySizeBytes)
        {
            throw new CryptographicException(
                $"Registry 密钥长度必须为 {KeySizeBytes} 字节，实际为 {key.Length} 字节");
        }

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSizeBytes];

        try
        {
            using (var aes = new AesGcm(key, TagSizeBytes))
            {
                aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
            }
        }
        finally
        {
            // 明文缓冲不留在内存里：它是被保护的东西本身。
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        var payload = new byte[NonceSizeBytes + TagSizeBytes + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, payload, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(cipherBytes, 0, payload, NonceSizeBytes + TagSizeBytes, cipherBytes.Length);

        return Base64UrlEncode(payload);
    }

    /// <summary>
    /// 解密 <see cref="Encrypt"/> 产出的密文。
    /// </summary>
    /// <param name="protectedText">Base64Url 密文。</param>
    /// <param name="key">密钥材料（长度须为 <see cref="KeySizeBytes"/>）。</param>
    /// <returns>明文口令。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="FormatException">密文不是合法的 Base64Url 文本。</exception>
    /// <exception cref="CryptographicException">
    /// 密文长度不对、密钥不匹配，或密文被篡改（认证标签校验失败）。
    /// </exception>
    internal static string Decrypt(string protectedText, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeySizeBytes)
        {
            throw new CryptographicException(
                $"Registry 密钥长度必须为 {KeySizeBytes} 字节，实际为 {key.Length} 字节");
        }

        var payload = Base64UrlDecode(protectedText);

        if (payload.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Registry 密文长度不合法");
        }

        var nonce = payload[..NonceSizeBytes];
        var tag = payload[NonceSizeBytes..(NonceSizeBytes + TagSizeBytes)];
        var cipherBytes = payload[(NonceSizeBytes + TagSizeBytes)..];

        var plainBytes = new byte[cipherBytes.Length];

        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            // 密钥不对或密文被改过——两者都表现为「解不开」。
            // 消息里刻意不带任何密文内容。
            throw new CryptographicException("Registry 密文校验失败（密钥不匹配或密文已被篡改）", ex);
        }

        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <summary>
    /// 判断一段文本是否为合法密文（用于「这条配置到底带没带口令」这类判定）。
    /// </summary>
    /// <param name="protectedText">待判定的文本。</param>
    /// <returns>是本类产出的密文格式返回 <see langword="true"/>。</returns>
    internal static bool IsProtectedText(string? protectedText)
    {
        if (String.IsNullOrEmpty(protectedText))
        {
            return false;
        }

        try
        {
            var payload = Base64UrlDecode(protectedText);
            return payload.Length >= NonceSizeBytes + TagSizeBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Base64Url 编码（无填充）。
    /// </summary>
    private static string Base64UrlEncode(byte[] payload) =>
        Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// Base64Url 解码（容忍缺失的 <c>=</c> 填充与标准 Base64 字母表）。
    /// </summary>
    private static byte[] Base64UrlDecode(string text)
    {
        var normalized = text.Trim().Replace('-', '+').Replace('_', '/');

        // 补齐填充位：Base64Url 编码时去掉了 '='，解码需要按长度补回。
        var padding = normalized.Length % 4;
        if (padding == 2)
        {
            normalized += "==";
        }
        else if (padding == 3)
        {
            normalized += "=";
        }
        else if (padding == 1)
        {
            throw new FormatException("Registry 密文的 Base64Url 长度非法");
        }

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Registry 密文不是合法的 Base64Url 文本", ex);
        }
    }
}
