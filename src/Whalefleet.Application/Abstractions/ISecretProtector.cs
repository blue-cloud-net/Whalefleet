namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 通用敏感文本加解密（外部资源抽象）。
/// </summary>
/// <remarks>
/// <para>与 <see cref="IRepositorySecretProtector"/> 同构，但语义更通用：
/// 通知渠道的 Webhook 令牌 / SMTP 密码同样需要加密落库。</para>
/// <para><b>复用同一把密钥</b>：实现与仓库口令共用数据卷上的 <c>repository.key</c>，
/// 该文件已纳入备份脚本。新增独立密钥文件只会多一个「漏备份」的机会。</para>
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// 加密。
    /// </summary>
    /// <param name="plaintext">明文。</param>
    /// <returns>可安全落库的密文。</returns>
    string Protect(string plaintext);

    /// <summary>
    /// 解密。
    /// </summary>
    /// <param name="protectedText">由 <see cref="Protect"/> 产出的密文。</param>
    /// <returns>明文。</returns>
    string Unprotect(string protectedText);
}
