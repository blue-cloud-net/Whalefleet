using FluentAssertions;

using Whalefleet.Application.Images;

using Xunit;

namespace Whalefleet.UnitTests.Images;

/// <summary>
/// <see cref="RegistryRepositoryPath"/> 与 <see cref="RegistryAuthChallenge"/> 的测试。
/// </summary>
/// <remarks>
/// <para><b>为什么钉这两条</b>：它们都是「按规范算出来的字符串」，一旦算错，
/// 症状是仓库返回 404 或 401——而那个 404 看起来像「镜像不存在」，
/// 用户会去改镜像名，越改越不对。</para>
/// <para>覆盖的规则：
/// <list type="number">
///   <item><description>官方镜像补 <c>library/</c> 命名空间（不补就是 404）；</description></item>
///   <item><description>Docker Hub 的三个别名归一成 <c>registry-1.docker.io</c>；</description></item>
///   <item><description>自建仓库的主机前缀被剥离，端口保留在主机一侧；</description></item>
///   <item><description><c>WWW-Authenticate</c> 的引号值可以含逗号与冒号。</description></item>
/// </list></para>
/// </remarks>
public class RegistryRepositoryPathTests
{
    [Theory]
    [InlineData("nginx", "registry-1.docker.io", "library/nginx")]
    [InlineData("library/nginx", "registry-1.docker.io", "library/nginx")]
    [InlineData("bitnami/nginx", "registry-1.docker.io", "bitnami/nginx")]
    [InlineData("docker.io/library/nginx", "registry-1.docker.io", "library/nginx")]
    [InlineData("index.docker.io/bitnami/nginx", "registry-1.docker.io", "bitnami/nginx")]
    [InlineData("harbor.local:5000/app", "harbor.local:5000", "app")]
    [InlineData("harbor.local:5000/team/app", "harbor.local:5000", "team/app")]
    [InlineData("localhost/app", "localhost", "app")]
    public void 换算成仓库坐标(string repository, string expectedServer, string expectedPath)
    {
        var target = RegistryRepositoryPath.From(repository);

        target.ServerAddress.Should().Be(expectedServer);
        target.Repository.Should().Be(expectedPath);
    }

    [Fact]
    public void 挑战头_解析出三个参数()
    {
        var challenge = RegistryAuthChallenge.TryParse(
            "Bearer realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\","
            + "scope=\"repository:library/nginx:pull\"");

        challenge.Should().NotBeNull();
        challenge!.Scheme.Should().Be("Bearer");
        challenge.Realm.Should().Be("https://auth.docker.io/token");
        challenge.Service.Should().Be("registry.docker.io");
        challenge.Scope.Should().Be("repository:library/nginx:pull");
    }

    [Fact]
    public void 挑战头_引号值里的逗号不会把值切断()
    {
        var challenge = RegistryAuthChallenge.TryParse(
            "Bearer realm=\"https://auth.local/token?a=1,b=2\",service=\"registry.local\"");

        challenge!.Realm.Should().Be("https://auth.local/token?a=1,b=2");
        challenge.Service.Should().Be("registry.local");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Negotiate")]
    [InlineData("Basic realm=\"registry\"")]
    public void 挑战头_不受支持的方案返回空(string header)
    {
        var challenge = RegistryAuthChallenge.TryParse(header);

        if (String.Equals(header, "Basic realm=\"registry\"", StringComparison.Ordinal))
        {
            // Basic 是受支持的：只是没有 service / scope。
            challenge.Should().NotBeNull();
            challenge!.Realm.Should().Be("registry");
            challenge.Scope.Should().BeNull();

            return;
        }

        challenge.Should().BeNull();
    }
}
