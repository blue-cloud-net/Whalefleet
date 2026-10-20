using Whalefleet.Application.Images;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Images;

/// <summary>
/// 镜像引用解析（<see cref="ImageReference.Split"/> 与
/// <see cref="ImageReference.ExtractServerAddress"/>）的测试。
/// </summary>
/// <remarks>
/// 这两个纯函数决定「推镜像时把哪个地址当成 仓库、用哪个标签推」。
/// 判错的后果很隐蔽：把 <c>org/app:v1</c> 的 <c>org</c> 当成 仓库地址，
/// 就会往一个不存在的主机上推，守护进程只回一句「unauthorized」。
/// </remarks>
public class ImageReferenceTests
{
    [Theory]
    [InlineData("nginx", "nginx", "latest")]
    [InlineData("nginx:1.27", "nginx", "1.27")]
    [InlineData("harbor.local:5000/app", "harbor.local:5000/app", "latest")]
    [InlineData("harbor.local:5000/app:v1", "harbor.local:5000/app", "v1")]
    [InlineData("org/app:v1", "org/app", "v1")]
    public void Split_按冒号在斜杠之后判标签(string input, string repository, string tag)
    {
        var (actualRepository, actualTag) = ImageReference.Split(input);

        actualRepository.Should().Be(repository);
        actualTag.Should().Be(tag);
    }

    /// <summary>
    /// 端口号在斜杠<b>左边</b>，因此不是标签——这与 Docker 自己的判据一致。
    /// </summary>
    [Fact]
    public void Split_端口号不算标签()
    {
        var (repository, tag) = ImageReference.Split("harbor.local:5000/app");

        repository.Should().Be("harbor.local:5000/app");
        tag.Should().Be("latest");
    }

    [Theory]
    [InlineData("harbor.local:5000/app:v1", "harbor.local:5000")]
    [InlineData("harbor.local/app:v1", "harbor.local")]
    [InlineData("localhost:5000/app", "localhost:5000")]
    [InlineData("localhost/app", "localhost")]
    [InlineData("registry.example.com/a/b/c:v1", "registry.example.com")]
    public void ExtractServerAddress_带点或冒号的前缀算Registry(string repository, string expected)
    {
        ImageReference.ExtractServerAddress(repository).Should().Be(expected);
    }

    /// <summary>
    /// Docker Hub 的官方命名空间（<c>org/app</c>）里的 <c>org</c> <b>不是</b> 仓库。
    /// </summary>
    [Theory]
    [InlineData("nginx", "registry-1.docker.io")]
    [InlineData("org/app", "registry-1.docker.io")]
    [InlineData("library/nginx", "registry-1.docker.io")]
    [InlineData("myorg/myapp:v1", "registry-1.docker.io")]
    public void ExtractServerAddress_无前缀按DockerHub处理(string repository, string expected)
    {
        ImageReference.ExtractServerAddress(repository).Should().Be(expected);
    }

    [Fact]
    public void ExtractServerAddress_前缀统一转小写()
    {
        ImageReference.ExtractServerAddress("Harbor.Local:5000/app:v1")
            .Should().Be("harbor.local:5000");
    }
}
