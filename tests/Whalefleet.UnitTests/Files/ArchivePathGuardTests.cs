using FluentAssertions;

using Whalefleet.Application.Common;
using Whalefleet.Application.Files;

using Xunit;

namespace Whalefleet.UnitTests.Files;

/// <summary>
/// <see cref="ArchivePathGuard"/> 的边界测试。
/// </summary>
/// <remarks>
/// 这个类挡的是「用户给的路径」——它是文件浏览与写入唯一的纵深防御点，
/// 而它的错误后果很重（<c>..</c> 漏判就能写到容器根之外）。
/// 因此每条拒绝规则都有独立用例，而不是只测「正常路径能过」。
/// </remarks>
public class ArchivePathGuardTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/etc", "/etc")]
    [InlineData("etc", "/etc")]
    [InlineData("", "/")]
    [InlineData("./etc/", "/etc")]
    [InlineData("/etc/./nginx", "/etc/nginx")]
    [InlineData("//etc//nginx//", "/etc/nginx")]
    [InlineData("/data\\sub", "/data/sub")]
    public void Normalize_ShouldProduceAbsolutePathWithoutTrailingSlash(string input, string expected)
    {
        ArchivePathGuard.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Normalize_ShouldTreatMissingPathAsRoot(string? input)
    {
        ArchivePathGuard.Normalize(input).Should().Be(
            "/",
            "列举与写入时省略 path 是常态，应当等价于根目录而不是报错");
    }

    [Theory]
    [InlineData("/..")]
    [InlineData("/etc/../root")]
    [InlineData("../etc/passwd")]
    [InlineData("/data/sub/../../etc")]
    public void Normalize_ShouldRejectParentTraversal(string input)
    {
        var act = () => ArchivePathGuard.Normalize(input);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.InvalidFilePath,
                "上跳引用一律拒绝，而不是「往上跳一层」——路径拼错时静默跳到别处是最危险的行为");
    }

    [Fact]
    public void Normalize_ShouldRejectNulCharacter()
    {
        var act = () => ArchivePathGuard.Normalize("/etc/pass\0wd");

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.InvalidFilePath);
    }

    [Fact]
    public void Normalize_ShouldRejectOverlongPath()
    {
        var path = "/" + new string('a', ArchivePathGuard.MaxPathLength);

        var act = () => ArchivePathGuard.Normalize(path);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.InvalidFilePath);
    }

    [Fact]
    public void Normalize_ShouldRejectOverlongSegment()
    {
        var path = "/" + new string('a', ArchivePathGuard.MaxSegmentLength + 1);

        var act = () => ArchivePathGuard.Normalize(path);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.InvalidFilePath);
    }

    [Theory]
    [InlineData("/", null)]
    [InlineData("/etc", "/")]
    [InlineData("/etc/nginx", "/etc")]
    public void GetParent_ShouldStopAtRoot(string path, string? expected)
    {
        ArchivePathGuard.GetParent(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("/", "")]
    [InlineData("/etc", "etc")]
    [InlineData("/etc/nginx.conf", "nginx.conf")]
    public void GetName_ShouldReturnLastSegment(string path, string expected)
    {
        ArchivePathGuard.GetName(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("/data", "nginx.conf", "/data/nginx.conf")]
    [InlineData("/", "nginx.conf", "/nginx.conf")]
    public void Combine_ShouldJoinPathAndName(string directory, string name, string expected)
    {
        ArchivePathGuard.Combine(directory, name).Should().Be(expected);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Combine_ShouldRejectNamesThatEscapeOrNest(string name)
    {
        var act = () => ArchivePathGuard.Combine("/data", name);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.InvalidFilePath,
                "条目名来自外部数据，拼路径前必须校验——否则一个 .. 就能写到目标目录之外");
    }
}
