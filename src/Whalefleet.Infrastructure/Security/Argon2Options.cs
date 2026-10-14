namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// Argon2id 密码哈希参数（配置节 <c>Security:Argon2</c>）。
/// </summary>
/// <remarks>
/// <para><b>参数取值</b>：默认 <c>m=64 MiB, t=3, p=4</c>，高于 OWASP 的最低要求
/// （<c>m=19 MiB, t=2, p=1</c>）。内存开销是抗 GPU/ASIC 并行破解的主要手段，因此优先加内存而非迭代次数。</para>
/// <para><b>调参是安全的</b>：参数随每条哈希一起编码存储（见 <c>PasswordHash</c>），
/// 因此调大参数只影响新哈希，旧哈希仍按其自身的参数校验通过；用户下次改密时自然升级到新参数。</para>
/// <para><b>内存成本提醒</b>：每次哈希/校验都会占用 <see cref="MemorySizeKiB"/> KiB 内存。
/// 容器内存受限时需相应下调，否则并发登录会触发 OOM。</para>
/// </remarks>
public sealed class Argon2Options
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Security:Argon2";

    /// <summary>
    /// 内存开销（KiB）。默认 65536 = 64 MiB。
    /// </summary>
    public int MemorySizeKiB { get; set; } = 65536;

    /// <summary>
    /// 迭代次数（time cost）。默认 3。
    /// </summary>
    public int Iterations { get; set; } = 3;

    /// <summary>
    /// 并行度（lanes）。默认 4。
    /// </summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>
    /// 盐长度（字节）。默认 16。
    /// </summary>
    public int SaltSizeBytes { get; set; } = 16;

    /// <summary>
    /// 摘要长度（字节）。默认 32。
    /// </summary>
    public int HashSizeBytes { get; set; } = 32;
}
