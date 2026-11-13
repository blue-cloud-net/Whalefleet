using FluentAssertions;

using Whalefleet.Core.Auth;

using Xunit;

namespace Whalefleet.UnitTests.Auth;

/// <summary>
/// <see cref="RefreshToken"/> 的状态判定与撤销行为测试。
/// </summary>
public class RefreshTokenTests
{
    private static readonly DateTime _issuedAt = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _expiresAt = _issuedAt.AddDays(7);

    private const string _tokenHash = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY";

    [Fact]
    public void Issue_写入全部属性且默认未撤销()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var token = RefreshToken.Issue(id, userId, _tokenHash, _expiresAt);

        token.Id.Should().Be(id);
        token.UserId.Should().Be(userId);
        token.TokenHash.Should().Be(_tokenHash);
        token.ExpiresAt.Should().Be(_expiresAt);
        token.RevokedAt.Should().BeNull();
        token.ReplacedByTokenHash.Should().BeNull();
    }

    [Fact]
    public void Issue_用户为空_抛出参数异常()
    {
        var act = () => RefreshToken.Issue(Guid.NewGuid(), Guid.Empty, _tokenHash, _expiresAt);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Issue_令牌哈希为空_抛出参数异常(string tokenHash)
    {
        var act = () => RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), tokenHash, _expiresAt);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsActiveAt_过期前为可用()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), _tokenHash, _expiresAt);

        token.IsActiveAt(_issuedAt).Should().BeTrue();
        token.IsActiveAt(_expiresAt.AddSeconds(-1)).Should().BeTrue();
    }

    [Fact]
    public void IsActiveAt_恰好到期即不可用()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), _tokenHash, _expiresAt);

        // 边界是半开区间：ExpiresAt 这一刻已经失效
        token.IsActiveAt(_expiresAt).Should().BeFalse();
        token.IsActiveAt(_expiresAt.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void IsActiveAt_已撤销即不可用_即使尚未过期()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), _tokenHash, _expiresAt);

        token.Revoke(_issuedAt.AddHours(1));

        token.IsActiveAt(_issuedAt.AddHours(2)).Should().BeFalse();
    }

    [Fact]
    public void Revoke_记录撤销时间()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), _tokenHash, _expiresAt);
        var revokedAt = _issuedAt.AddHours(1);

        token.Revoke(revokedAt);

        token.RevokedAt.Should().Be(revokedAt);
    }

    [Fact]
    public void Revoke_重复调用幂等_以最后一次为准()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), _tokenHash, _expiresAt);
        token.Revoke(_issuedAt.AddHours(1));

        token.Revoke(_issuedAt.AddHours(2));

        token.RevokedAt.Should().Be(_issuedAt.AddHours(2));
    }

    [Fact]
    public void Rotate_原地更新令牌并保留上一代哈希()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), _tokenHash, _expiresAt);
        var newHash = "bmV3LXRva2VuLWhhc2g";
        var newExpiry = _issuedAt.AddDays(8);

        token.Rotate(newHash, newExpiry, _issuedAt.AddHours(1));

        // 会话 id 不变（它就是会话本身），只有绑定的令牌换了。
        token.TokenHash.Should().Be(newHash);
        token.PreviousTokenHash.Should().Be(_tokenHash);
        token.ExpiresAt.Should().Be(newExpiry);
        token.RotatedAt.Should().Be(_issuedAt.AddHours(1));
        token.IsActiveAt(_issuedAt.AddHours(2)).Should().BeTrue();
    }

    [Fact]
    public void MatchesPreviousHash_命中上一代才为真()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), _tokenHash, _expiresAt);
        token.Rotate("bmV3LXRva2VuLWhhc2g", _expiresAt, _issuedAt.AddHours(1));

        token.MatchesPreviousHash(_tokenHash).Should().BeTrue();
        token.MatchesPreviousHash("bmV3LXRva2VuLWhhc2g").Should().BeFalse();
        token.MatchesPreviousHash("  ").Should().BeFalse();
    }

    [Fact]
    public void UpdateFingerprint_覆盖旧值且空白值被忽略()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), _tokenHash, _expiresAt, "old-fp");

        token.UpdateFingerprint("new-fp");
        token.DeviceFingerprint.Should().Be("new-fp");

        token.UpdateFingerprint("   ");
        token.DeviceFingerprint.Should().Be("new-fp");
    }

    [Fact]
    public void MarkCreated_写入创建审计()
    {
        var creator = Guid.NewGuid();
        var token = RefreshToken.Issue(Guid.NewGuid(), creator, _tokenHash, _expiresAt);

        token.MarkCreated(_issuedAt, creator);

        token.CreationTime.Should().Be(_issuedAt);
        token.CreatorId.Should().Be(creator);
    }
}
