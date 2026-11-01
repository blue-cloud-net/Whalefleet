using FluentAssertions;

using Whalefleet.Application.Common;
using Whalefleet.Application.Stacks;

using Xunit;

namespace Whalefleet.UnitTests.Stacks;

/// <summary>
/// <see cref="ComposePlanner"/> 的行为测试。
/// </summary>
/// <remarks>
/// 重点是三件事：<b>命名规则与 Compose 一致</b>、<b>启动顺序按依赖排</b>、
/// <b>配置哈希只受该服务自身的变化影响</b>（这是「滚动重启」的粒度依据）。
/// </remarks>
public class ComposePlannerTests
{
    /// <summary>
    /// 解析并规划一份 Compose 文件。
    /// </summary>
    /// <param name="projectName">项目名。</param>
    /// <param name="content">YAML 文本。</param>
    /// <returns>编排计划。</returns>
    private static ComposePlan Plan(string projectName, string content) =>
        ComposePlanner.Plan(projectName, ComposeParser.Parse(content));

    [Fact]
    public void 规划_默认网络总是存在()
    {
        var plan = Plan("demo", """
            services:
              web:
                image: nginx
            """);

        plan.Networks.Should().ContainSingle();
        plan.Networks[0].Name.Should().Be("default");
        plan.Networks[0].ActualName.Should().Be("demo_default");
    }

    [Fact]
    public void 规划_网络名按项目名加前缀()
    {
        var plan = Plan("demo", """
            services:
              web:
                image: nginx
                networks:
                  - backend
            networks:
              backend:
            """);

        plan.Networks.Should().Contain(network =>
            network.Name == "backend" && network.ActualName == "demo_backend");
    }

    [Fact]
    public void 规划_已带项目名前缀的网络名保持原样()
    {
        var plan = Plan("demo", """
            services:
              web:
                image: nginx
                networks:
                  - shared_net
            networks:
              shared_net:
                name: shared_net
            """);

        plan.Networks.Should().Contain(network =>
            network.Name == "shared_net" && network.ActualName == "shared_net");
    }

    [Fact]
    public void 规划_服务未写networks时接入默认网络()
    {
        var plan = Plan("demo", """
            services:
              web:
                image: nginx
            """);

        plan.Services[0].Networks.Should().BeEquivalentTo(["default"]);
    }

    [Fact]
    public void 规划_容器名按Compose规则拼接()
    {
        var plan = Plan("demo", """
            services:
              web:
                image: nginx
            """);

        plan.Services[0].ContainerName.Should().Be("demo-web-1");
    }

    [Fact]
    public void 规划_服务引用但顶层未声明的卷被隐式收录()
    {
        var plan = Plan("demo", """
            services:
              web:
                image: nginx
                volumes:
                  - data:/data
            """);

        plan.Volumes.Should().ContainSingle();
        plan.Volumes[0].ActualName.Should().Be("demo_data");
        plan.Services[0].Volumes[0].ActualName.Should().Be("demo_data");
    }

    [Fact]
    public void 规划_宿主路径挂载不进卷计划()
    {
        var plan = Plan("demo", """
            services:
              web:
                image: nginx
                volumes:
                  - /srv/data:/data
            """);

        plan.Volumes.Should().BeEmpty();
        plan.Services[0].Binds.Should().ContainSingle();
        plan.Services[0].Binds[0].Source.Should().Be("/srv/data");
    }

    [Fact]
    public void 规划_启动顺序按依赖排而不是按声明顺序()
    {
        var plan = Plan("demo", """
            services:
              web:
                image: nginx
                depends_on:
                  - db
              db:
                image: postgres
            """);

        plan.StartOrder.Should().BeEquivalentTo(["db", "web"], options => options.WithStrictOrdering());
    }

    [Fact]
    public void 规划_依赖成环_报1904并指出环()
    {
        var exception = Assert.Throws<BusinessException>(() => Plan("demo", """
            services:
              a:
                image: nginx
                depends_on:
                  - b
              b:
                image: nginx
                depends_on:
                  - a
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("循环依赖");
    }

    [Fact]
    public void 规划_服务引用不存在的网络_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => Plan("demo", """
            services:
              web:
                image: nginx
                networks:
                  - ghost
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("ghost");
    }

    [Fact]
    public void 规划_配置哈希只受该服务自身变化影响()
    {
        var before = Plan("demo", """
            services:
              web:
                image: nginx
                ports:
                  - "8080:80"
              db:
                image: postgres
            """);

        var after = Plan("demo", """
            services:
              web:
                image: nginx
                ports:
                  - "8080:80"
              db:
                image: postgres
                environment:
                  FOO: bar
            """);

        // db 变了不该影响 web 的哈希——否则「改一个服务重建全部」就发生了
        before.Services.Single(s => s.Service.Name == "web").ConfigHash
            .Should().Be(after.Services.Single(s => s.Service.Name == "web").ConfigHash);

        before.Services.Single(s => s.Service.Name == "db").ConfigHash
            .Should().NotBe(after.Services.Single(s => s.Service.Name == "db").ConfigHash);
    }

    /// <summary>
    /// 「改某个字段 → 配置哈希必须变」的用例集。
    /// </summary>
    /// <remarks>
    /// 用完整 YAML 而不是「拼字符串」：拼出来的缩进很容易出错，
    /// 而失败信息会指向 YAML 解析而不是被测的哈希逻辑，排查成本高。
    /// </remarks>
    public static TheoryData<string, string, string> HashChangeCases => new()
    {
        {
            "image",
            """
            services:
              web:
                image: nginx
            """,
            """
            services:
              web:
                image: nginx:1.27
            """
        },
        {
            "command",
            """
            services:
              web:
                image: nginx
                command: ["a"]
            """,
            """
            services:
              web:
                image: nginx
                command: ["b"]
            """
        },
        {
            "environment",
            """
            services:
              web:
                image: nginx
                environment:
                  FOO: bar
            """,
            """
            services:
              web:
                image: nginx
                environment:
                  FOO: baz
            """
        },
        {
            "ports",
            """
            services:
              web:
                image: nginx
                ports:
                  - "8080:80"
            """,
            """
            services:
              web:
                image: nginx
                ports:
                  - "9090:90"
            """
        },
        {
            "volumes",
            """
            services:
              web:
                image: nginx
                volumes:
                  - data:/data
            """,
            """
            services:
              web:
                image: nginx
                volumes:
                  - other:/data
            """
        },
        {
            "restart",
            """
            services:
              web:
                image: nginx
                restart: always
            """,
            """
            services:
              web:
                image: nginx
                restart: no
            """
        },
        {
            "healthcheck",
            """
            services:
              web:
                image: nginx
                healthcheck:
                  test: ["CMD", "true"]
            """,
            """
            services:
              web:
                image: nginx
                healthcheck:
                  test: ["CMD", "false"]
            """
        },
        {
            "networks",
            """
            services:
              web:
                image: nginx
                networks:
                  - default
            """,
            """
            services:
              web:
                image: nginx
                networks:
                  - other
            networks:
              other:
            """
        },
    };

    [Theory]
    [MemberData(nameof(HashChangeCases))]
    public void 规划_改服务的任一字段都会改变配置哈希(string field, string before, string after)
    {
        var baseline = Plan("demo", before);
        var modified = Plan("demo", after);

        modified.Services[0].ConfigHash
            .Should().NotBe(
                baseline.Services[0].ConfigHash,
                "改了 {0} 就必须重建该服务",
                field);
    }
}
