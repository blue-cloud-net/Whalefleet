using System.Globalization;
using System.Security.Cryptography;

namespace Docker.Manager.Core.Auth;

/// <summary>
/// 密码哈希（值对象）——Argon2id 的 PHC 编码串。
/// </summary>
/// <remarks>
/// <para><b>本类型不做任何密码学计算</b>：它只负责 PHC 字符串
/// （<c>$argon2id$v=19$m=65536,t=3,p=4$&lt;salt&gt;$&lt;hash&gt;</c>）的<b>解析与生成</b>，
/// 因此可以留在 <c>Core</c>（零 NuGet 依赖）。真正的 Argon2id 计算在
/// <c>Infrastructure</c> 的 <c>Argon2PasswordHasher</c>。</para>
/// <para><b>为什么要有这个值对象</b>：算法参数（内存 / 迭代 / 并行度）与盐都必须随哈希一起持久化，
/// 否则日后调整参数或升级算法时，旧哈希将永远无法验证。把它们内聚进一个可解析、可比较的值对象，
/// 是「参数可演进」的前提。</para>
/// <para><b>相等性</b>：按<b>规范化编码串</b>比较（<see cref="ToString"/> 的输出），
/// 而不是按 <see cref="Salt"/> / <see cref="Digest"/> 数组引用——字节数组的默认相等性是引用比较，
/// 直接参与值对象相等性会得到"内容相同却不相等"的错误结果。</para>
/// <para><b>常量时间比较</b>：<see cref="IsMatch"/> 使用
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>，
/// 避免按字节短路比较泄漏摘要信息。</para>
/// <para><b>格式依据</b>：PHC 字符串格式（<c>phc-sf-spec</c>）；Base64 采用<b>标准字母表、无填充</b>。
/// Argon2 参数与版本号取值参考 RFC 9106。</para>
/// </remarks>
public sealed class PasswordHash : ValueObject
{
    /// <summary>
    /// 算法标识（PHC 串的第二个字段）。目前仅支持 Argon2id。
    /// </summary>
    public const string AlgorithmIdentifier = "argon2id";

    /// <summary>
    /// 支持的 Argon2 版本号（<c>0x13</c> = 19，即 RFC 9106 的 1.3 版）。
    /// </summary>
    /// <remarks>
    /// 刻意<b>只接受</b>这一个版本：遇到未知版本必须显式增加支持，而不是按"大概兼容"继续。
    /// </remarks>
    public const int SupportedVersion = 19;

    /// <summary>盐的最小长度（字节）。RFC 9106 要求 ≥ 8。</summary>
    public const int MinSaltLength = 8;

    /// <summary>盐的最大长度（字节）。</summary>
    public const int MaxSaltLength = 64;

    /// <summary>摘要的最小长度（字节）。</summary>
    public const int MinDigestLength = 4;

    /// <summary>摘要的最大长度（字节）。</summary>
    public const int MaxDigestLength = 256;

    /// <summary>内存开销的最小值（KiB）。</summary>
    public const int MinMemorySizeKiB = 8;

    /// <summary>内存开销的最大值（KiB，2 GiB）。</summary>
    public const int MaxMemorySizeKiB = 2 * 1024 * 1024;

    /// <summary>迭代次数的最小值。</summary>
    public const int MinIterations = 1;

    /// <summary>迭代次数的最大值。</summary>
    public const int MaxIterations = 100;

    /// <summary>并行度的最小值。</summary>
    public const int MinParallelism = 1;

    /// <summary>并行度的最大值（不超过典型 CPU 核心数上界）。</summary>
    public const int MaxParallelism = 64;

    private PasswordHash(
        int memorySizeKiB,
        int iterations,
        int parallelism,
        byte[] salt,
        byte[] digest)
    {
        this.MemorySizeKiB = memorySizeKiB;
        this.Iterations = iterations;
        this.Parallelism = parallelism;
        this.Salt = salt;
        this.Digest = digest;

        // 规范化编码串只算一次，并作为相等性依据。
        // 用 InvariantCulture 是刻意的：编码串是持久化格式，不能因运行环境的区域设置而变化。
        this.Encoded = String.Create(
            CultureInfo.InvariantCulture,
            $"${AlgorithmIdentifier}$v={SupportedVersion}"
            + $"$m={memorySizeKiB},t={iterations},p={parallelism}"
            + $"${EncodeBase64(salt)}${EncodeBase64(digest)}");
    }

    /// <summary>
    /// 规范化 PHC 编码串（<see cref="ToString"/> 的输出）。
    /// </summary>
    public string Encoded { get; }

    /// <summary>
    /// 内存开销（KiB）。
    /// </summary>
    public int MemorySizeKiB { get; }

    /// <summary>
    /// 迭代次数（time cost）。
    /// </summary>
    public int Iterations { get; }

    /// <summary>
    /// 并行度（lanes）。
    /// </summary>
    public int Parallelism { get; }

    /// <summary>
    /// 盐（随机字节，随哈希一同持久化）。
    /// </summary>
    public byte[] Salt { get; }

    /// <summary>
    /// 摘要（哈希输出）。
    /// </summary>
    public byte[] Digest { get; }

    /// <summary>
    /// 用已计算出的参数与摘要构造哈希。
    /// </summary>
    /// <param name="memorySizeKiB">内存开销（KiB），须 ≥ <c>8 × parallelism</c>。</param>
    /// <param name="iterations">迭代次数。</param>
    /// <param name="parallelism">并行度。</param>
    /// <param name="salt">盐（长度 8–64 字节）。</param>
    /// <param name="digest">摘要（长度 4–256 字节）。</param>
    /// <returns>新建的哈希值对象。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="salt"/> 或 <paramref name="digest"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">任一参数越界，或内存低于 <c>8 × parallelism</c>。</exception>
    public static PasswordHash Create(
        int memorySizeKiB,
        int iterations,
        int parallelism,
        byte[] salt,
        byte[] digest)
    {
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(digest);

        ValidateParallelism(parallelism);
        ValidateMemorySize(memorySizeKiB, parallelism);
        ValidateIterations(iterations);
        ValidateSalt(salt);
        ValidateDigest(digest);

        // 复制入参：避免调用方后续改动数组污染已构造的值对象（值对象必须不可变）。
        return new PasswordHash(
            memorySizeKiB,
            iterations,
            parallelism,
            [.. salt],
            [.. digest]);
    }

    /// <summary>
    /// 解析 PHC 编码串。
    /// </summary>
    /// <param name="encoded">形如 <c>$argon2id$v=19$m=65536,t=3,p=4$&lt;salt&gt;$&lt;hash&gt;</c> 的字符串。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="ArgumentException">字符串为空、结构不符、算法/版本不受支持或参数越界。</exception>
    public static PasswordHash Parse(string encoded)
    {
        if (!TryParse(encoded, out var hash))
        {
            throw new ArgumentException($"不是合法的 Argon2id PHC 编码串：{Describe(encoded)}", nameof(encoded));
        }

        return hash;
    }

    /// <summary>
    /// 尝试解析 PHC 编码串。
    /// </summary>
    /// <param name="encoded">待解析字符串，可为 <see langword="null"/>。</param>
    /// <param name="hash">解析成功时返回哈希值对象；否则为 <see langword="null"/>。</param>
    /// <returns>解析成功返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 校验登录输入时用本方法：<b>格式非法的哈希一律视为"不匹配"</b>，不应抛出异常
    /// （否则一个被篡改的库行就能让登录接口返回 500 而不是 401）。
    /// </remarks>
    public static bool TryParse(string? encoded, [NotNullWhen(true)] out PasswordHash? hash)
    {
        hash = null;

        if (String.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        // 结构：<空>algorithm v=<版本> m=..,t=..,p=.. salt digest
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0)
        {
            return false;
        }

        if (!String.Equals(parts[1], AlgorithmIdentifier, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryReadVersion(parts[2], out var version) || version != SupportedVersion)
        {
            return false;
        }

        if (!TryReadParameters(parts[3], out var memorySizeKiB, out var iterations, out var parallelism))
        {
            return false;
        }

        if (!TryDecodeBase64(parts[4], out var salt) || !TryDecodeBase64(parts[5], out var digest))
        {
            return false;
        }

        try
        {
            hash = Create(memorySizeKiB, iterations, parallelism, salt, digest);
            return true;
        }
        catch (ArgumentException)
        {
            // 参数越界（例如 m < 8p）：按"格式非法"处理，不向上抛。
            return false;
        }
    }

    /// <summary>
    /// 以常量时间判断给定摘要是否与本哈希的摘要一致。
    /// </summary>
    /// <param name="candidateDigest">待比较的摘要（由哈希器重新计算得出）。</param>
    /// <returns>一致返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidateDigest"/> 为 <see langword="null"/>。</exception>
    public bool IsMatch(byte[] candidateDigest)
    {
        ArgumentNullException.ThrowIfNull(candidateDigest);

        return CryptographicOperations.FixedTimeEquals(this.Digest, candidateDigest);
    }

    /// <summary>
    /// 返回规范化的 PHC 编码串（可直接落库）。
    /// </summary>
    public override string ToString() => this.Encoded;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        // 按规范化编码串比较：等价于「算法 + 版本 + 参数 + 盐 + 摘要」全部相同。
        yield return this.Encoded;
    }

    private static bool TryReadVersion(string segment, out int version)
    {
        version = 0;

        if (!segment.StartsWith("v=", StringComparison.Ordinal))
        {
            return false;
        }

        return Int32.TryParse(
            segment.AsSpan(2),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out version);
    }

    private static bool TryReadParameters(
        string segment,
        out int memorySizeKiB,
        out int iterations,
        out int parallelism)
    {
        memorySizeKiB = 0;
        iterations = 0;
        parallelism = 0;

        var seenMemory = false;
        var seenIterations = false;
        var seenParallelism = false;

        foreach (var pair in segment.Split(','))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                return false;
            }

            var key = pair[..separatorIndex];
            var valueSpan = pair.AsSpan(separatorIndex + 1);

            if (!Int32.TryParse(
                    valueSpan,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return false;
            }

            // 键序不敏感（PHC 规范化顺序为 m,t,p，但解析时宽容读取）。
            switch (key)
            {
                case "m" when !seenMemory:
                    memorySizeKiB = value;
                    seenMemory = true;
                    break;
                case "t" when !seenIterations:
                    iterations = value;
                    seenIterations = true;
                    break;
                case "p" when !seenParallelism:
                    parallelism = value;
                    seenParallelism = true;
                    break;
                default:
                    // 未知键或重复键：拒绝，避免"看起来合法但语义未知"的串被接受。
                    return false;
            }
        }

        return seenMemory && seenIterations && seenParallelism;
    }

    private static bool TryDecodeBase64(string segment, out byte[] bytes)
    {
        bytes = [];

        if (segment.Length == 0)
        {
            return false;
        }

        // PHC 使用标准字母表但省略 '=' 填充，解析前补回。
        var padding = (4 - (segment.Length % 4)) % 4;
        var padded = padding == 0 ? segment : segment + new String('=', padding);

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeBase64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=');

    private static void ValidateMemorySize(int memorySizeKiB, int parallelism)
    {
        if (memorySizeKiB is < MinMemorySizeKiB or > MaxMemorySizeKiB)
        {
            throw new ArgumentException(
                $"内存开销须在 {MinMemorySizeKiB}–{MaxMemorySizeKiB} KiB 之间，实际为 {memorySizeKiB}",
                nameof(memorySizeKiB));
        }

        // RFC 9106：内存（以块计）必须至少是 8 × lanes。
        if (memorySizeKiB < 8 * parallelism)
        {
            throw new ArgumentException(
                $"内存开销（{memorySizeKiB} KiB）不得小于 8 × 并行度（{8 * parallelism} KiB）",
                nameof(memorySizeKiB));
        }
    }

    private static void ValidateIterations(int iterations)
    {
        if (iterations is < MinIterations or > MaxIterations)
        {
            throw new ArgumentException(
                $"迭代次数须在 {MinIterations}–{MaxIterations} 之间，实际为 {iterations}",
                nameof(iterations));
        }
    }

    private static void ValidateParallelism(int parallelism)
    {
        if (parallelism is < MinParallelism or > MaxParallelism)
        {
            throw new ArgumentException(
                $"并行度须在 {MinParallelism}–{MaxParallelism} 之间，实际为 {parallelism}",
                nameof(parallelism));
        }
    }

    private static void ValidateSalt(byte[] salt)
    {
        if (salt.Length is < MinSaltLength or > MaxSaltLength)
        {
            throw new ArgumentException(
                $"盐长度须在 {MinSaltLength}–{MaxSaltLength} 字节之间，实际为 {salt.Length}",
                nameof(salt));
        }
    }

    private static void ValidateDigest(byte[] digest)
    {
        if (digest.Length is < MinDigestLength or > MaxDigestLength)
        {
            throw new ArgumentException(
                $"摘要长度须在 {MinDigestLength}–{MaxDigestLength} 字节之间，实际为 {digest.Length}",
                nameof(digest));
        }
    }

    /// <summary>
    /// 生成用于异常消息的短描述——<b>绝不回显完整串</b>（其中含盐与摘要）。
    /// </summary>
    private static string Describe(string? encoded) =>
        String.IsNullOrEmpty(encoded)
            ? "（空）"
            : $"「{encoded[..Math.Min(16, encoded.Length)]}…」（长度 {encoded.Length}）";
}
