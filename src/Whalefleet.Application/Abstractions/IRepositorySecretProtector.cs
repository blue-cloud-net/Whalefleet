namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 仓库口令的加密 / 解密（外部资源抽象）。
/// </summary>
/// <remarks>
/// <para><b>为什么是抽象而不是直接在应用层调 AES</b>：密钥的<b>来源</b>是部署期关注点
/// （数据卷上的文件、0600 权限、随备份一起带走），实现依赖具体的密钥管理策略。
/// 与 <see cref="IPasswordHasher"/> 同构：算法在 Infrastructure，接口在 Application。</para>
/// <para><b>实现必须是认证加密（AES-GCM）</b>：密文被篡改时必须<b>解密失败</b>，
/// 而不是解出一段垃圾明文——后者会让系统拿着错误的凭据去登录别人的仓库。</para>
/// <para><b>安全要求</b>：实现<b>不得</b>把明文口令写入日志、异常消息或任何响应；
/// 异常消息里也不得包含密文内容。</para>
/// </remarks>
public interface IRepositorySecretProtector
{
    /// <summary>
    /// 把明文口令加密成可安全落库的密文。
    /// </summary>
    /// <param name="plaintext">明文口令。</param>
    /// <returns>密文（可直接存入 TEXT 列）。</returns>
    string Protect(string plaintext);

    /// <summary>
    /// 解密。
    /// </summary>
    /// <param name="protectedText">此前由 <see cref="Protect"/> 产出的密文。</param>
    /// <returns>明文口令。</returns>
    /// <exception cref="Common.BusinessException">
    /// 密文损坏或密钥不匹配（归成 <c>1000</c>，不暴露内部细节）。
    /// </exception>
    string Unprotect(string protectedText);
}
