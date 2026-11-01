using FluentAssertions;

using Whalefleet.Application.Common;
using Whalefleet.Application.Stacks;

using Xunit;

namespace Whalefleet.UnitTests.Stacks;

/// <summary>
/// <see cref="ComposeParser"/> 的行为测试。
/// </summary>
/// <remarks>
/// <para>本测试的核心目标是<b>钉住「不支持的字段必须报错」这条语义</b>。
/// 解析器最容易出的错不是「解析不出来」，而是<b>静默忽略</b>——
/// 那会产出「YAML 里写了的没生效」这类最难排查的故障。</para>
/// <para>因此每个「暂不支持」的字段都有一条用例，
/// 且都断言错误消息里带上了完整的字段路径（用户据此能定位到 YAML 里的那几行）。</para>
/// </remarks>
public class ComposeParserTests
{
    /// <summary>
    /// 一份最小的合法 Compose 文件。
    /// </summary>
    private const string _minimal = """
        services:
          web:
            image: nginx:1.27
        """;

    [Fact]
    public void 解析_最小文件_得到一个服务()
    {
        var document = ComposeParser.Parse(_minimal);

        document.Services.Should().HaveCount(1);
        document.Services[0].Name.Should().Be("web");
        document.Services[0].Image.Should().Be("nginx:1.27");
        document.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void 解析_顶层version字段_忽略但给出提示()
    {
        var document = ComposeParser.Parse("""
            version: "3.9"
            services:
              web:
                image: nginx
            """);

        document.Warnings.Should().ContainSingle(warning => warning.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public void 解析_顶层name字段_提示项目名以Stack名称为准()
    {
        var document = ComposeParser.Parse("""
            name: my-project
            services:
              web:
                image: nginx
            """);

        document.Warnings.Should().ContainSingle(warning => warning.Contains("name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("env_file")]
    [InlineData("configs")]
    [InlineData("secrets")]
    [InlineData("extends")]
    [InlineData("profiles")]
    [InlineData("deploy")]
    [InlineData("ports")]
    [InlineData("container_name")]
    [InlineData("hostname")]
    [InlineData("user")]
    public void 解析_服务含不支持字段_报1904且带字段路径(string field)
    {
        var content = $$"""
            services:
              web:
                image: nginx
                {{field}}: something
            """;

        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse(content));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain($"services.web.{field}");
    }

    [Fact]
    public void 解析_服务缺少image_报1904并说明不支持build()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                command: nginx
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("services.web");
        exception.Message.Should().Contain("image");
    }

    [Fact]
    public void 解析_顶层含不支持字段_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
            configs:
              - a
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("configs");
    }

    [Fact]
    public void 解析_含变量插值_报1904而不是替换成空串()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx:${TAG}
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("services.web.image");
    }

    [Fact]
    public void 解析_含相对bind路径_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
                volumes:
                  - ./data:/var/lib/data
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("services.web.volumes");
    }

    [Fact]
    public void 解析_端口用长语法_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
                ports:
                  - target: 80
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("services.web.ports");
    }

    [Fact]
    public void 解析_depends_on指向不存在的服务_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
                depends_on:
                  - db
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("db");
    }

    [Fact]
    public void 解析_环境变量用映射写法_转成KEY等于VALUE()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                environment:
                  FOO: bar
                  EMPTY:
            """);

        document.Services[0].Environment.Should().BeEquivalentTo(["FOO=bar", "EMPTY="]);
    }

    [Fact]
    public void 解析_环境变量用列表写法_保留原样()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                environment:
                  - FOO=bar
                  - BAZ=qux
            """);

        document.Services[0].Environment.Should().BeEquivalentTo(["FOO=bar", "BAZ=qux"]);
    }

    [Fact]
    public void 解析_环境变量缺值_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
                environment:
                  - FOO
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
        exception.Message.Should().Contain("services.web.environment");
    }

    [Fact]
    public void 解析_端口短语法_拆成结构化三元组()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                ports:
                  - "8080:80"
                  - "90"
                  - "127.0.0.1:8443:443"
                  - "53:53/udp"
            """);

        var ports = document.Services[0].Ports;

        ports.Should().HaveCount(4);

        ports[0].HostPort.Should().Be(8080);
        ports[0].ContainerPort.Should().Be(80);
        ports[0].Protocol.Should().Be("tcp");

        ports[1].HostPort.Should().BeNull();
        ports[1].ContainerPort.Should().Be(90);

        ports[2].HostIp.Should().Be("127.0.0.1");
        ports[2].HostPort.Should().Be(8443);
        ports[2].ContainerPort.Should().Be(443);

        ports[3].Protocol.Should().Be("udp");
    }

    [Fact]
    public void 解析_端口越界_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
                ports:
                  - "70000:80"
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
    }

    [Fact]
    public void 解析_卷短语法_区分具名卷与宿主路径()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                volumes:
                  - data:/var/lib/data
                  - /srv/logs:/var/log
                  - /srv/conf:/etc/conf:ro
            """);

        var volumes = document.Services[0].Volumes;

        volumes.Should().HaveCount(3);
        volumes[0].IsBind.Should().BeFalse();
        volumes[0].Source.Should().Be("data");
        volumes[1].IsBind.Should().BeTrue();
        volumes[1].Source.Should().Be("/srv/logs");
        volumes[2].ReadOnly.Should().BeTrue();
    }

    [Fact]
    public void 解析_命令用字符串_按shell规则拆分()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                command: sh -c "echo hello world"
            """);

        document.Services[0].Command.Should().BeEquivalentTo(["sh", "-c", "echo hello world"]);
    }

    [Fact]
    public void 解析_命令用列表_原样保留()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                command: ["sh", "-c", "echo hi"]
            """);

        document.Services[0].Command.Should().BeEquivalentTo(["sh", "-c", "echo hi"]);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("always")]
    [InlineData("on-failure")]
    [InlineData("unless-stopped")]
    public void 解析_合法重启策略_原样保留(string policy)
    {
        var document = ComposeParser.Parse($$"""
            services:
              web:
                image: nginx
                restart: {{policy}}
            """);

        document.Services[0].Restart.Should().Be(policy);
    }

    [Fact]
    public void 解析_非法重启策略_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
                restart: sometimes
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
    }

    [Fact]
    public void 解析_健康检查_测试串按CMD_SHELL处理()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                healthcheck:
                  test: curl -f http://localhost/ || exit 1
                  interval: 30s
                  timeout: 5s
                  start_period: 40s
                  retries: 3
            """);

        var healthCheck = document.Services[0].HealthCheck;

        healthCheck.Should().NotBeNull();
        healthCheck!.Test.Should().BeEquivalentTo(["CMD-SHELL", "curl -f http://localhost/ || exit 1"]);
        healthCheck.IntervalSeconds.Should().Be(30);
        healthCheck.TimeoutSeconds.Should().Be(5);
        healthCheck.StartPeriodSeconds.Should().Be(40);
        healthCheck.Retries.Should().Be(3);
    }

    [Fact]
    public void 解析_健康检查用数组_原样保留()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                healthcheck:
                  test: ["CMD", "curl", "-f", "http://localhost/"]
            """);

        document.Services[0].HealthCheck!.Test.Should().BeEquivalentTo(["CMD", "curl", "-f", "http://localhost/"]);
    }

    [Fact]
    public void 解析_健康检查禁用_等价于NONE()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                healthcheck:
                  disable: true
            """);

        document.Services[0].HealthCheck!.Test.Should().BeEquivalentTo(["NONE"]);
    }

    [Fact]
    public void 解析_健康检查禁用与test并存_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
                healthcheck:
                  disable: true
                  test: ["CMD", "true"]
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
    }

    [Fact]
    public void 解析_健康检查缺test_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("""
            services:
              web:
                image: nginx
                healthcheck:
                  interval: 30s
            """));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
    }

    [Fact]
    public void 解析_顶层网络与卷_读出定义()
    {
        var document = ComposeParser.Parse("""
            services:
              web:
                image: nginx
                networks:
                  - backend
                volumes:
                  - data:/data
            networks:
              backend:
                name: shared-backend
                external: true
            volumes:
              data:
            """);

        document.Networks.Should().ContainSingle();
        document.Networks[0].Name.Should().Be("backend");
        document.Networks[0].ActualName.Should().Be("shared-backend");
        document.Networks[0].External.Should().BeTrue();

        document.Volumes.Should().ContainSingle();
        document.Volumes[0].Name.Should().Be("data");
        document.Volumes[0].External.Should().BeFalse();
    }

    [Fact]
    public void 解析_不是合法YAML_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("services:\n  - [unclosed"));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
    }

    [Fact]
    public void 解析_缺少services段_报1904()
    {
        var exception = Assert.Throws<BusinessException>(() => ComposeParser.Parse("networks: {}"));

        exception.Code.Should().Be(ErrorCodes.ComposeInvalid);
    }
}
