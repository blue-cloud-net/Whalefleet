using System.Security.Cryptography;
using System.Text;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Core.Auth;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Docker.Manager.Infrastructure.Security;

/// <summary>
/// 基于 <c>Konscious.Security.Cryptography.Argon2</c>（MIT）的 Argon2id 密码哈希器。
/// </summary>
/// <remarks>
/// <para><b>职责边界</b>：本类只做「计算摘要」与「校验摘要」；PHC 编码串的拼接与解析全部委托给
/// <see cref="PasswordHash"/>（<c>Core</c>）。这样换成别的 Argon2 实现时，落库格式与解析逻辑无需改动。</para>
/// <para><b>无状态且线程安全</b>：不持有任何可变状态（每次调用新建 <see cref="Argon2id"/> 实例），
/// 因此注册为 Singleton。</para>
/// <para><b>常量时间比较</b>：比较交由 <see cref="PasswordHash.IsMatch"/>（内部用
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>），
/// 本类<b>不</b>使用 <c>==</c> 或 <c>SequenceEqual</c> 比较摘要。</para>
/// <para><b>敏感数据</b>：明文密码的 UTF-8 字节在计算后立即清零；盐与摘要是公开的（本来就要随哈希存储），
/// 无需清零。</para>
/// </remarks>
internal sealed class Argon2PasswordHasher : IPasswordHasher
{
    private readonly Argon2Options _options;

    /// <summary>
    /// 哑元哈希（惰性求值）。
    /// </summary>
    /// <remarks>
    /// 用一个随机密码生成一次，其明文无从得知，因此校验永不可能成功。
    /// 惰性求值让它只在真的出现「用户不存在」时才付出那一次生成成本；
    /// 用<b>当前配置的参数</b>生成，保证与真实校验的耗时一致。
    /// </remarks>
    private readonly Lazy<string> _dummyHash;

    /// <summary>
    /// 初始化哈希器。
    /// </summary>
    /// <param name="options">Argon2 参数（启动时已校验）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 <see langword="null"/>。</exception>
    public Argon2PasswordHasher(IOptions<Argon2Options> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _dummyHash = new Lazy<string>(
            () => this.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // 每次都用新的随机盐：相同密码两次哈希必然不同，杜绝"相同密码哈希相同"这一可被离线彩虹表利用的特征。
        var salt = RandomNumberGenerator.GetBytes(_options.SaltSizeBytes);

        var digest = DeriveDigest(
            password,
            salt,
            _options.MemorySizeKiB,
            _options.Iterations,
            _options.Parallelism,
            _options.HashSizeBytes);

        return PasswordHash.Create(
            _options.MemorySizeKiB,
            _options.Iterations,
            _options.Parallelism,
            salt,
            digest).ToString();
    }

    /// <inheritdoc />
    public bool Verify(string password, string encodedHash)
    {
        // 任何异常输入都返回 false 而不抛异常：登录路径依赖这一点（损坏的库行 → 401 而非 500）。
        if (String.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!PasswordHash.TryParse(encodedHash, out var expected))
        {
            return false;
        }

        // 用"该哈希自身的参数"重算，而不是当前配置的默认参数——这正是把参数编进哈希串的意义。
        var candidate = DeriveDigest(
            password,
            expected.Salt,
            expected.MemorySizeKiB,
            expected.Iterations,
            expected.Parallelism,
            expected.Digest.Length);

        return expected.IsMatch(candidate);
    }

    /// <inheritdoc />
    public void PerformDummyVerification(string password)
    {
        if (String.IsNullOrEmpty(password))
        {
            return;
        }

        // 结果必然为 false（没人知道哑元哈希的明文），刻意丢弃。
        _ = this.Verify(password, _dummyHash.Value);
    }

    private static byte[] DeriveDigest(
        string password,
        byte[] salt,
        int memorySizeKiB,
        int iterations,
        int parallelism,
        int hashSizeBytes)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = memorySizeKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };

            return argon2.GetBytes(hashSizeBytes);
        }
        finally
        {
            // 明文副本不留在堆上等待 GC。
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
