using Whalefleet.Application.Common;
using Whalefleet.Application.Permissions;
using Whalefleet.Core.Permissions;
using FluentAssertions;
using Xunit;

namespace Whalefleet.UnitTests.Permissions;

/// <summary>
/// <see cref="PermissionNames"/> 的规范化与错误码分流测试。
/// </summary>
/// <remarks>
/// <para><b>为什么错误码的分流要单独钉住</b>：<c>1514</c>（未知名）与 <c>1000</c>（参数不合法）
/// 长得极像，实现里顺手写成同一个码不会有任何编译期或运行期信号——
/// <c>1514</c> 在 <c>ErrorCodes</c>、<c>ErrorCodeCatalog</c>、前端 <c>ErrorCode</c> 表与
/// <c>docs/api-spec.md</c> 四处都登记齐了，唯独抛出点漏掉时，没有任何守卫测试会失败。</para>
/// <para>本组用例就是那个守卫。</para>
/// </remarks>
public class PermissionNamesTests
{
    [Fact]
    public void Normalize_转小写去重并按目录顺序排列()
    {
        var result = PermissionNames.Normalize(
            ["VOLUME.WRITE", "host.read", "volume.write", " container.read "],
            "permissions");

        // 顺序由目录决定而不是由输入决定：数据库的 SELECT 不承诺排序，
        // 不归一的话同一个角色两次读出来的顺序可能不同，界面上的勾选框就会跟着抖。
        result.Should().BeEquivalentTo(
            [Permission.HostRead, Permission.ContainerRead, Permission.VolumeWrite],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void Normalize_未知权限名返回1514()
    {
        var act = () => PermissionNames.Normalize(["host.read", "container.explode"], "permissions");

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.PermissionUnknown);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("ALL")]
    [InlineData("root")]
    [InlineData("7")]
    public void Normalize_常见的拼错写法都归到1514(string permission)
    {
        // 客户端最容易犯的就是这几种，给它独立码界面才能提示「请从权限目录里选」
        var act = () => PermissionNames.Normalize([permission], "permissions");

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.PermissionUnknown);
    }

    [Fact]
    public void Normalize_空串权限标识返回1000而不是1514()
    {
        // 1000 = 参数本身不合法；1514 = 名字不在目录里。
        // 空白项属于前者——它连「一个名字」都不是。
        var act = () => PermissionNames.Normalize(["host.read", "  "], "permissions");

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Normalize_不允许为空且给了空数组时返回1000()
    {
        var act = () => PermissionNames.Normalize([], "permissions", allowEmpty: false);

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Normalize_允许为空时空数组与null都得到空集()
    {
        // 「先建角色、后配权限」是真实的管理路径，
        // 此时「不选任何权限」不该被当成非法输入。
        PermissionNames.Normalize([], "permissions", allowEmpty: true).Should().BeEmpty();
        PermissionNames.Normalize(null, "permissions", allowEmpty: true).Should().BeEmpty();
    }

    [Fact]
    public void Normalize_校验发生在返回之前_不会产出部分结果()
    {
        // 顺序很重要：前面的合法项不能因为后面一项非法就被「先收下」
        var act = () => PermissionNames.Normalize(
            [Permission.HostRead, Permission.UserWrite, "nope"],
            "permissions");

        act.Should().Throw<BusinessException>()
            .Which.Code.Should().Be(ErrorCodes.PermissionUnknown);
    }
}
