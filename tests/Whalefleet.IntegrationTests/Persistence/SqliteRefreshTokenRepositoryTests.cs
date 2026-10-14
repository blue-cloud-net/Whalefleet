using Whalefleet.Core.Auth;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Whalefleet.IntegrationTests.Persistence;

/// <summary>
/// <c>SqliteRefreshTokenRepository</c> 的持久化行为集成测试。
/// </summary>
/// <remarks>
/// 令牌表不参与软删除语义，因此这组用例反过来验证「<b>没有</b>被软删除过滤误伤」：
/// 撤销（<c>revoked_at</c> 置位）后记录仍能被读到，只有 <c>DeleteExpiredAsync</c> 才真正删行。
/// </remarks>
public class SqliteRefreshTokenRepositoryTests
{
    private static readonly Guid _operatorId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _otherUserId = new("44444444-4444-4444-4444-444444444444");

    private const string _tokenHash = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY";

    [Fact]
    public async Task AddAsync_填充创建审计并可按哈希读回()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();
        var token = IssueToken("hash-a", PersistenceTestHost.Now.UtcDateTime.AddDays(7));

        await tokens.AddAsync(token);
        var loaded = await tokens.FindByHashAsync("hash-a");

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(token.Id);
        loaded.UserId.Should().Be(_operatorId);
        loaded.TokenHash.Should().Be("hash-a");
        loaded.ExpiresAt.Should().Be(PersistenceTestHost.Now.UtcDateTime.AddDays(7));
        loaded.ExpiresAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.RevokedAt.Should().BeNull();
        loaded.ReplacedByTokenHash.Should().BeNull();

        loaded.CreationTime.Should().Be(PersistenceTestHost.Now.UtcDateTime);
        loaded.CreatorId.Should().Be(_operatorId);
    }

    [Fact]
    public async Task 落库形态_未撤销时revoked_at为NULL()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();
        await tokens.AddAsync(IssueToken(_tokenHash, PersistenceTestHost.Now.UtcDateTime.AddDays(7)));

        (await host.QueryScalarAsync("SELECT user_id FROM refresh_tokens")).Should().Be(_operatorId.ToString("D"));
        (await host.QueryScalarAsync("SELECT token_hash FROM refresh_tokens")).Should().Be(_tokenHash);
        (await host.QueryScalarAsync("SELECT revoked_at FROM refresh_tokens")).Should().BeNull();
        (await host.QueryScalarAsync("SELECT replaced_by_token_hash FROM refresh_tokens")).Should().BeNull();
        (await host.QueryScalarAsync("SELECT expires_at FROM refresh_tokens")).Should().StartWith("2026-10-17 08:00:00");
    }

    [Fact]
    public async Task FindByHashAsync_未知哈希_返回null()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();

        (await tokens.FindByHashAsync("does-not-exist")).Should().BeNull();
    }

    [Fact]
    public async Task QueryByIdAsync_按主键查询_与按哈希查询结果一致()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();
        var token = IssueToken("hash-a", PersistenceTestHost.Now.UtcDateTime.AddDays(7));
        await tokens.AddAsync(token);

        var byId = await tokens.QueryByIdAsync(token.Id);

        byId.Should().NotBeNull();
        byId!.Id.Should().Be(token.Id);
        byId.TokenHash.Should().Be("hash-a");
        (await tokens.QueryByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_物理删除单条_与软删除语义不同()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();
        var token = IssueToken("hash-a", PersistenceTestHost.Now.UtcDateTime.AddDays(7));
        var other = IssueToken("hash-b", PersistenceTestHost.Now.UtcDateTime.AddDays(7));

        await tokens.AddAsync(token);
        await tokens.AddAsync(other);

        // 刷新令牌不参与软删除：RemoveAsync 走 DELETE，行真的消失（对照 users/hosts/roles）
        (await tokens.RemoveByIdAsync(token.Id)).Should().BeTrue();
        (await tokens.QueryByIdAsync(token.Id)).Should().BeNull();
        (await host.QueryScalarAsync("SELECT COUNT(1) FROM refresh_tokens")).Should().Be("1");

        (await tokens.RemoveByIdAsync(token.Id)).Should().BeFalse();
        (await tokens.FindByHashAsync("hash-b")).Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_持久化撤销与轮换字段()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();
        var token = IssueToken("hash-a", PersistenceTestHost.Now.UtcDateTime.AddDays(7));
        await tokens.AddAsync(token);

        token.Revoke(PersistenceTestHost.Now.UtcDateTime.AddMinutes(5), "hash-b");
        (await tokens.UpdateAsync(token)).Should().BeTrue();

        var loaded = (await tokens.FindByHashAsync("hash-a"))!;
        loaded.IsActiveAt(PersistenceTestHost.Now.UtcDateTime.AddMinutes(6)).Should().BeFalse();
        loaded.RevokedAt.Should().Be(PersistenceTestHost.Now.UtcDateTime.AddMinutes(5));
        loaded.ReplacedByTokenHash.Should().Be("hash-b");

        (await host.QueryScalarAsync("SELECT revoked_at FROM refresh_tokens")).Should().StartWith("2026-10-10 08:05:00");
    }

    [Fact]
    public async Task UpdateAsync_记录不存在_返回false()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();
        var token = IssueToken("hash-ghost", PersistenceTestHost.Now.UtcDateTime.AddDays(7));

        (await tokens.UpdateAsync(token)).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAllForUserAsync_只撤销该用户尚未撤销的令牌()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();

        var active1 = IssueToken("hash-1", PersistenceTestHost.Now.UtcDateTime.AddDays(7));
        var active2 = IssueToken("hash-2", PersistenceTestHost.Now.UtcDateTime.AddDays(7));
        var alreadyRevoked = IssueToken("hash-3", PersistenceTestHost.Now.UtcDateTime.AddDays(7));
        var otherUser = RefreshToken.Issue(
            Guid.NewGuid(),
            _otherUserId,
            "hash-other",
            PersistenceTestHost.Now.UtcDateTime.AddDays(7));

        await tokens.AddAsync(active1);
        await tokens.AddAsync(active2);
        await tokens.AddAsync(alreadyRevoked);
        await tokens.AddAsync(otherUser);

        var earlierRevocation = PersistenceTestHost.Now.UtcDateTime.AddHours(1);
        alreadyRevoked.Revoke(earlierRevocation);
        await tokens.UpdateAsync(alreadyRevoked);

        var revokedAt = PersistenceTestHost.Now.UtcDateTime.AddHours(2);
        var affected = await tokens.RevokeAllForUserAsync(_operatorId, revokedAt);

        affected.Should().Be(2);
        (await tokens.FindByHashAsync("hash-1"))!.RevokedAt.Should().Be(revokedAt);
        (await tokens.FindByHashAsync("hash-2"))!.RevokedAt.Should().Be(revokedAt);

        // 已撤销的保持原撤销时间（不会被刷新），其他用户的令牌不受影响
        (await tokens.FindByHashAsync("hash-3"))!.RevokedAt.Should().Be(earlierRevocation);
        (await tokens.FindByHashAsync("hash-other"))!.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteExpiredAsync_只物理删除已过期的令牌()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();

        var expired = IssueToken("hash-expired", PersistenceTestHost.Now.UtcDateTime.AddDays(-1));
        var stillValid = IssueToken("hash-valid", PersistenceTestHost.Now.UtcDateTime.AddDays(1));

        await tokens.AddAsync(expired);
        await tokens.AddAsync(stillValid);

        var deleted = await tokens.DeleteExpiredAsync(PersistenceTestHost.Now.UtcDateTime);

        deleted.Should().Be(1);
        (await tokens.FindByHashAsync("hash-expired")).Should().BeNull();
        (await tokens.FindByHashAsync("hash-valid")).Should().NotBeNull();
        (await host.QueryScalarAsync("SELECT COUNT(1) FROM refresh_tokens")).Should().Be("1");
    }

    [Fact]
    public async Task 令牌哈希唯一约束_同一哈希不可重复写入()
    {
        await using var host = new PersistenceTestHost(_operatorId);
        await host.InitializeAsync();

        var tokens = host.Services.GetRequiredService<IRefreshTokenRepository>();
        await tokens.AddAsync(IssueToken(_tokenHash, PersistenceTestHost.Now.UtcDateTime.AddDays(7)));

        var act = () => tokens.AddAsync(IssueToken(_tokenHash, PersistenceTestHost.Now.UtcDateTime.AddDays(7)));

        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }

    private static RefreshToken IssueToken(string tokenHash, DateTime expiresAt) =>
        RefreshToken.Issue(Guid.NewGuid(), _operatorId, tokenHash, expiresAt);
}
