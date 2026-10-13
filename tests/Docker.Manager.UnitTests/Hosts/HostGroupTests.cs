using Docker.Manager.Core.Hosts;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.UnitTests.Hosts;

/// <summary>
/// <see cref="HostGroup"/> 实体的不变量与行为测试。
/// </summary>
/// <remarks>
/// 分组本身很薄（三个字段 + 归一化），因此这里重点覆盖**边界与归一化**：
/// 名称去空白、长度上下界、控制字符、描述空白视为未填写。
/// 这些规则一旦放宽，界面排版与日志会被用户输入直接破坏。
/// </remarks>
public class HostGroupTests
{
    [Fact]
    public void Create_保存名称与排序权重()
    {
        var id = Guid.NewGuid();

        var group = HostGroup.Create(id, "production", "线上生产负载", 10);

        group.Id.Should().Be(id);
        group.Name.Should().Be("production");
        group.Description.Should().Be("线上生产负载");
        group.SortOrder.Should().Be(10);
        group.IsDeleted.Should().BeFalse();

        // 审计由仓储填充，实体自身不取时间与用户
        group.CreationTime.Should().Be(default(DateTime));
        group.CreatorId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Create_去掉名称与描述的首尾空白()
    {
        var group = HostGroup.Create(Guid.NewGuid(), "  staging  ", "  预发布  ", 20);

        group.Name.Should().Be("staging");
        group.Description.Should().Be("预发布");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_名称为空_抛异常(string? name)
    {
        var act = () => HostGroup.Create(Guid.NewGuid(), name!, null, 0);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_名称超长_抛异常()
    {
        var tooLong = new string('a', HostGroup.MaxNameLength + 1);

        var act = () => HostGroup.Create(Guid.NewGuid(), tooLong, null, 0);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_名称达上限_允许()
    {
        var maxLength = new string('a', HostGroup.MaxNameLength);

        var group = HostGroup.Create(Guid.NewGuid(), maxLength, null, 0);

        group.Name.Should().HaveLength(HostGroup.MaxNameLength);
    }

    [Fact]
    public void Create_名称含控制字符_抛异常()
    {
        // 换行会同时破坏界面排版与日志格式，必须在实体层拒掉
        var act = () => HostGroup.Create(Guid.NewGuid(), "prod\nction", null, 0);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_描述为空白_存为null(string? description)
    {
        var group = HostGroup.Create(Guid.NewGuid(), "staging", description, 0);

        group.Description.Should().BeNull();
    }

    [Fact]
    public void Create_描述超长_抛异常()
    {
        var tooLong = new string('d', HostGroup.MaxDescriptionLength + 1);

        var act = () => HostGroup.Create(Guid.NewGuid(), "staging", tooLong, 0);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rename_允许改名并归一化()
    {
        var group = HostGroup.Create(Guid.NewGuid(), "production", null, 10);

        group.Rename("  线上  ");

        // 与其他实体不同，分组名可改：它只是展示标签，不进 URL、不进 JWT，也没有外键引用
        group.Name.Should().Be("线上");
    }

    [Fact]
    public void Rename_非法名称_抛异常且不改动()
    {
        var group = HostGroup.Create(Guid.NewGuid(), "production", null, 10);

        var act = () => group.Rename("   ");

        act.Should().Throw<ArgumentException>();
        group.Name.Should().Be("production", "改名失败不该留下半改状态");
    }

    [Fact]
    public void Describe_空串表示清空()
    {
        var group = HostGroup.Create(Guid.NewGuid(), "staging", "预发布验证环境", 20);

        group.Describe("   ");

        group.Description.Should().BeNull();
    }

    [Fact]
    public void Reorder_更新权重()
    {
        var group = HostGroup.Create(Guid.NewGuid(), "local", null, 30);

        group.Reorder(5);

        group.SortOrder.Should().Be(5);
    }
}
