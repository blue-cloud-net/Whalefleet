using Whalefleet.Core.Auth;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Auth;

/// <summary>
/// <see cref="PasswordHash"/> 的编码 / 解析 / 相等性与边界校验测试。
/// </summary>
/// <remarks>
/// 本类型<b>不做密码学计算</b>（那是 <c>Infrastructure</c> 的 Argon2PasswordHasher 的职责），
/// 因此纯逻辑测试留在 UnitTests，无需真实哈希算力。
/// </remarks>
public class PasswordHashTests
{
    /// <summary>
    /// 合法的极小哈希串：<c>m=8, t=1, p=1</c>，盐 8 字节、摘要 4 字节（Base64 解出的均为全零）。
    /// </summary>
    private const string _minimalEncoded = "$argon2id$v=19$m=8,t=1,p=1$AAAAAAAAAAA$AAAAAA";

    [Fact]
    public void Create_保留全部参数并生成规范化编码串()
    {
        var salt = BuildSalt();
        var digest = BuildDigest();

        var hash = PasswordHash.Create(65536, 3, 4, salt, digest);

        hash.MemorySizeKiB.Should().Be(65536);
        hash.Iterations.Should().Be(3);
        hash.Parallelism.Should().Be(4);
        hash.Salt.Should().Equal(salt);
        hash.Digest.Should().Equal(digest);
        hash.Encoded.Should().Be(hash.ToString());
        hash.Encoded.Should().StartWith("$argon2id$v=19$m=65536,t=3,p=4$");
    }

    [Fact]
    public void Create_编码串的盐与摘要段不含Base64填充符()
    {
        var hash = PasswordHash.Create(65536, 3, 4, BuildSalt(), BuildDigest());

        // 按 '$' 切分：[0]空 [1]算法 [2]版本 [3]参数 [4]盐 [5]摘要
        var segments = hash.Encoded.Split('$');

        segments.Should().HaveCount(6);
        segments[4].Should().NotEndWith("=");
        segments[5].Should().NotEndWith("=");
    }

    [Fact]
    public void Create_复制入参数组_外部修改不影响哈希()
    {
        var salt = BuildSalt();
        var digest = BuildDigest();

        var hash = PasswordHash.Create(65536, 3, 4, salt, digest);
        var encodedBefore = hash.Encoded;

        // 篡改调用方持有的数组：值对象必须不受影响（否则"不可变"名不副实）。
        salt[0] = 0xFF;
        digest[0] = 0xFF;

        hash.Encoded.Should().Be(encodedBefore);
        hash.Salt[0].Should().NotBe(0xFF);
        hash.Digest[0].Should().NotBe(0xFF);
    }

    [Fact]
    public void Parse_往返一致()
    {
        var original = PasswordHash.Create(65536, 3, 4, BuildSalt(), BuildDigest());

        var parsed = PasswordHash.Parse(original.ToString());

        parsed.Should().Be(original);
        parsed.Encoded.Should().Be(original.Encoded);
    }

    [Fact]
    public void Parse_极小合法值可解析()
    {
        var hash = PasswordHash.Parse(_minimalEncoded);

        hash.MemorySizeKiB.Should().Be(8);
        hash.Iterations.Should().Be(1);
        hash.Parallelism.Should().Be(1);
        hash.Salt.Should().HaveCount(8);
        hash.Digest.Should().HaveCount(4);
    }

    [Fact]
    public void Parse_参数键序不敏感()
    {
        var hash = PasswordHash.Parse("$argon2id$v=19$p=1,t=2,m=16$AAAAAAAAAAA$AAAAAA");

        hash.MemorySizeKiB.Should().Be(16);
        hash.Iterations.Should().Be(2);
        hash.Parallelism.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("argon2id$v=19$m=8,t=1,p=1$AAAAAAAAAAA$AAAAAA")] // 缺少前导 '$'
    [InlineData("$argon2i$v=19$m=8,t=1,p=1$AAAAAAAAAAA$AAAAAA")] // 算法不是 argon2id
    [InlineData("$argon2id$v=16$m=8,t=1,p=1$AAAAAAAAAAA$AAAAAA")] // 版本不受支持
    [InlineData("$argon2id$m=8,t=1,p=1$AAAAAAAAAAA$AAAAAA")] // 缺少版本段
    [InlineData("$argon2id$v=19$m=8,t=1$AAAAAAAAAAA$AAAAAA")] // 缺少 p
    [InlineData("$argon2id$v=19$m=8,t=1,p=1,x=0$AAAAAAAAAAA$AAAAAA")] // 未知参数键
    [InlineData("$argon2id$v=19$m=8,t=1,p=1$AAAAAAAAAAA$AAAAAA$extra")] // 多余字段
    [InlineData("$argon2id$v=19$m=8,t=1,p=1$$AAAAAA")] // 盐为空
    [InlineData("$argon2id$v=19$m=8,t=1,p=1$********$AAAAAA")] // 盐不是合法 Base64
    [InlineData("$argon2id$v=19$m=4,t=1,p=1$AAAAAAAAAAA$AAAAAA")] // 内存低于下限
    [InlineData("$argon2id$v=19$m=8,t=0,p=1$AAAAAAAAAAA$AAAAAA")] // 迭代次数低于下限
    [InlineData("$argon2id$v=19$m=8,t=101,p=1$AAAAAAAAAAA$AAAAAA")] // 迭代次数高于上限
    [InlineData("$argon2id$v=19$m=8,t=1,p=0$AAAAAAAAAAA$AAAAAA")] // 并行度低于下限
    [InlineData("$argon2id$v=19$m=8,t=1,p=2$AAAAAAAAAAA$AAAAAA")] // 内存 < 8 × 并行度
    [InlineData("$argon2id$v=19$m=8,t=1,p=1$AAAAAA$AAAAAA")] // 盐过短（4 字节）
    [InlineData("$argon2id$v=19$m=8,t=1,p=1$AAAAAAAAAAA$AA")] // 摘要过短（1 字节）
    public void Parse_非法串_抛出参数异常(string encoded)
    {
        var act = () => PasswordHash.Parse(encoded);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$m=4,t=1,p=1$AAAAAAAAAAA$AAAAAA")]
    public void TryParse_非法串_返回false且不抛出(string encoded)
    {
        var succeeded = PasswordHash.TryParse(encoded, out var hash);

        succeeded.Should().BeFalse();
        hash.Should().BeNull();
    }

    [Fact]
    public void TryParse_null_返回false()
    {
        PasswordHash.TryParse(null, out var hash).Should().BeFalse();
        hash.Should().BeNull();
    }

    [Fact]
    public void TryParse_合法串_返回true()
    {
        PasswordHash.TryParse(_minimalEncoded, out var hash).Should().BeTrue();
        hash.Should().NotBeNull();
    }

    [Fact]
    public void Create_盐或摘要为null_抛出参数空异常()
    {
        var saltNull = () => PasswordHash.Create(65536, 3, 4, null!, BuildDigest());
        var digestNull = () => PasswordHash.Create(65536, 3, 4, BuildSalt(), null!);

        saltNull.Should().Throw<ArgumentNullException>();
        digestNull.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(65)]
    public void Create_盐长度越界_抛出参数异常(int saltLength)
    {
        var act = () => PasswordHash.Create(65536, 3, 4, new byte[saltLength], BuildDigest());

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(257)]
    public void Create_摘要长度越界_抛出参数异常(int digestLength)
    {
        var act = () => PasswordHash.Create(65536, 3, 4, BuildSalt(), new byte[digestLength]);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 1)] // 迭代次数为 0
    [InlineData(101, 1)] // 迭代次数超上限
    [InlineData(3, 0)] // 并行度为 0
    [InlineData(3, 65)] // 并行度超上限
    public void Create_迭代次数或并行度越界_抛出参数异常(int iterations, int parallelism)
    {
        var act = () => PasswordHash.Create(65536, iterations, parallelism, BuildSalt(), BuildDigest());

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(16, 4)] // 8 × 4 = 32 KiB > 16，违反 m ≥ 8p
    [InlineData(31, 4)] // 同上，边界再收紧一格
    public void Create_内存低于八倍并行度_抛出参数异常(int memorySizeKiB, int parallelism)
    {
        var act = () => PasswordHash.Create(memorySizeKiB, 3, parallelism, BuildSalt(), BuildDigest());

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(3 * 1024 * 1024)]
    public void Create_内存开销越界_抛出参数异常(int memorySizeKiB)
    {
        var act = () => PasswordHash.Create(memorySizeKiB, 3, 1, BuildSalt(), BuildDigest());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsMatch_摘要相同为真_不同或长度不同为假()
    {
        var digest = BuildDigest();
        var hash = PasswordHash.Create(65536, 3, 4, BuildSalt(), digest);

        hash.IsMatch(BuildDigest()).Should().BeTrue();

        var other = BuildDigest();
        other[0] ^= 0xFF;
        hash.IsMatch(other).Should().BeFalse();

        hash.IsMatch(new byte[digest.Length - 1]).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_摘要为null_抛出参数空异常()
    {
        var hash = PasswordHash.Create(65536, 3, 4, BuildSalt(), BuildDigest());

        var act = () => hash.IsMatch(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 相等性_按编码串判定()
    {
        var salt = BuildSalt();
        var digest = BuildDigest();

        var first = PasswordHash.Create(65536, 3, 4, salt, digest);
        var same = PasswordHash.Create(65536, 3, 4, salt, digest);

        var differentSalt = PasswordHash.Create(65536, 3, 4, BuildSalt(seed: 1), digest);
        var differentParams = PasswordHash.Create(65536, 4, 4, salt, digest);

        first.Should().Be(same);
        first.GetHashCode().Should().Be(same.GetHashCode());
        first.Should().NotBe(differentSalt);
        first.Should().NotBe(differentParams);
    }

    private static byte[] BuildSalt(int length = 16, int seed = 0)
    {
        var salt = new byte[length];
        for (var i = 0; i < length; i++)
        {
            salt[i] = (byte)(seed + i);
        }

        return salt;
    }

    private static byte[] BuildDigest(int length = 32, int seed = 0xA0)
    {
        var digest = new byte[length];
        for (var i = 0; i < length; i++)
        {
            digest[i] = (byte)(seed + i);
        }

        return digest;
    }
}
