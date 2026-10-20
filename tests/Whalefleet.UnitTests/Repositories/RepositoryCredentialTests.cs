using Whalefleet.Core.Repositories;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Registries;

/// <summary>
/// <see cref="RepositoryCredential"/> 实体的不变量测试。
/// </summary>
/// <remarks>
/// 本实体最关键的不变量是<b>「实体里永远只有密文，没有明文口令」</b>——
/// 它保证「把实体整个序列化进日志」不会泄露凭据。另有两条：
/// 账号与口令必须成对，以及别名必须可读且不含控制字符。
/// </remarks>
public class RepositoryCredentialTests
{
    private const string _cipherText = "aGVsbG8td29ybGQtY2lwaGVy";

    [Fact]
    public void Register_保存别名地址与账号()
    {
        var id = Guid.NewGuid();

        var credential = RepositoryCredential.Register(
            id,
            "  内网 Harbor  ",
            "  HTTPS://Harbor.Local:5000/  ",
            "  deploy-bot  ",
            _cipherText,
            "  CI 用  ");

        credential.Id.Should().Be(id);
        credential.Name.Should().Be("内网 Harbor");
        credential.ServerAddress.Should().Be("harbor.local:5000");
        credential.Username.Should().Be("deploy-bot");
        credential.EncryptedPassword.Should().Be(_cipherText);
        credential.Description.Should().Be("CI 用");
        credential.HasCredentials.Should().BeTrue();
        credential.IsDeleted.Should().BeFalse();
    }

    /// <summary>
    /// 匿名仓库也允许登记（记录地址与别名，便于统一管理）。
    /// </summary>
    [Fact]
    public void Register_匿名访问_账号与口令均为空()
    {
        var credential = RepositoryCredential.Register(
            Guid.NewGuid(),
            "公共仓库",
            "registry-1.docker.io");

        credential.Username.Should().BeEmpty();
        credential.EncryptedPassword.Should().BeEmpty();
        credential.HasCredentials.Should().BeFalse();
        credential.Description.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_空白账号视为匿名(string? username)
    {
        var credential = RepositoryCredential.Register(
            Guid.NewGuid(),
            "公共仓库",
            "registry-1.docker.io",
            username);

        credential.Username.Should().BeEmpty();
        credential.HasCredentials.Should().BeFalse();
    }

    /// <summary>
    /// 半截凭据推镜像时只会得到「认证失败」，而用户看不出是自己的配置缺了一半。
    /// </summary>
    [Fact]
    public void Register_有账号无口令_报参数错()
    {
        var act = () => RepositoryCredential.Register(
            Guid.NewGuid(),
            "内网 Harbor",
            "harbor.local:5000",
            "deploy-bot",
            encryptedPassword: null);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*必须提供口令*");
    }

    [Fact]
    public void Register_有口令无账号_报参数错()
    {
        var act = () => RepositoryCredential.Register(
            Guid.NewGuid(),
            "内网 Harbor",
            "harbor.local:5000",
            username: null,
            encryptedPassword: _cipherText);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*必须填写账号*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("换行\n名字")]
    [InlineData("制表\t名字")]
    public void Register_别名非法_报参数错(string? name)
    {
        var act = () => RepositoryCredential.Register(Guid.NewGuid(), name!, "harbor.local:5000");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_别名超长_报参数错()
    {
        var tooLong = new string('a', RepositoryCredential.MaxNameLength + 1);

        var act = () => RepositoryCredential.Register(Guid.NewGuid(), tooLong, "harbor.local:5000");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*长度*");
    }

    [Fact]
    public void Register_地址非法_报参数错()
    {
        var act = () => RepositoryCredential.Register(Guid.NewGuid(), "内网", "not a valid addr");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Register_描述为空白_存为null()
    {
        var credential = RepositoryCredential.Register(
            Guid.NewGuid(),
            "内网 Harbor",
            "harbor.local:5000",
            description: "   ");

        credential.Description.Should().BeNull();
    }

    [Fact]
    public void Delete_打删除标记且与时间保持不变式()
    {
        var credential = RepositoryCredential.Register(Guid.NewGuid(), "内网", "harbor.local:5000");
        var now = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc);

        credential.Delete(now, Guid.NewGuid());

        credential.IsDeleted.Should().BeTrue();
        credential.DeletionTime.Should().Be(now);

        credential.Restore();

        credential.IsDeleted.Should().BeFalse();
        credential.DeletionTime.Should().BeNull();
    }
}
