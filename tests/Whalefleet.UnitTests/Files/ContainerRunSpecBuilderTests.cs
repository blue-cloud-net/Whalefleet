using Docker.DotNet.Models;

using FluentAssertions;

using Whalefleet.Application.Files;

using Xunit;

namespace Whalefleet.UnitTests.Files;

/// <summary>
/// <see cref="ShellQuoting"/> 的单元测试。
/// </summary>
public sealed class ShellQuotingTests
{
    [Theory]
    [InlineData("nginx", "nginx")]
    [InlineData("my-app_1", "my-app_1")]
    [InlineData("/etc/nginx/nginx.conf", "/etc/nginx/nginx.conf")]
    [InlineData("8080:80", "8080:80")]
    [InlineData("KEY=value", "KEY=value")]
    public void Quote_ShouldLeavePlainValueBare(string value, string expected)
    {
        ShellQuoting.Quote(value).Should().Be(expected);
    }

    [Fact]
    public void Quote_ShouldWrapValueWithSpaces()
    {
        ShellQuoting.Quote("hello world").Should().Be("'hello world'");
    }

    [Fact]
    public void Quote_ShouldEscapeSingleQuote()
    {
        // 单引号内无法转义，标准做法是「关引号 → \' → 再开引号」
        ShellQuoting.Quote("it's").Should().Be("'it'\\''s'");
    }

    [Theory]
    [InlineData("$HOME", "'$HOME'")]
    [InlineData("a; rm -rf /", "'a; rm -rf /'")]
    [InlineData("`id`", "'`id`'")]
    public void Quote_ShouldWrapValuesShellWouldInterpret(string value, string expected)
    {
        ShellQuoting.Quote(value).Should().Be(expected);
    }

    [Fact]
    public void Quote_ShouldRenderEmptyValueAsEmptyLiteral()
    {
        ShellQuoting.Quote(String.Empty).Should().Be("''");
    }

    [Fact]
    public void Quote_ShouldRejectNull()
    {
        var act = () => ShellQuoting.Quote(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

/// <summary>
/// <see cref="ContainerRunSpecBuilder"/> 的单元测试。
/// </summary>
/// <remarks>
/// 夹具手写而不是从运行中的容器取：这些断言验的是「字段到参数」的映射关系，
/// 靠真实容器会引入一堆与本用例无关的差异（镜像标签、端口、卷都不可控）。
/// </remarks>
public sealed class ContainerRunSpecBuilderTests
{
    [Fact]
    public void BuildDockerRun_ShouldIncludeImageAndName()
    {
        var container = CreateContainer();

        var command = ContainerRunSpecBuilder.BuildDockerRun(container);

        command.Should().StartWith("docker run ");
        command.Should().Contain("--name web");
        command.Should().Contain("nginx:1.27");
    }

    [Fact]
    public void BuildDockerRun_ShouldQuoteEnvironmentWithSpaces()
    {
        var container = CreateContainer(configure: container =>
            container.Config!.Env = ["GREETING=hello world"]);

        var command = ContainerRunSpecBuilder.BuildDockerRun(container);

        command.Should().Contain("--env 'GREETING=hello world'");
    }

    [Fact]
    public void BuildDockerRun_ShouldPublishHostIpAndProtocol()
    {
        var container = CreateContainer(configure: container =>
            container.HostConfig!.PortBindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal)
            {
                ["53/udp"] = [new PortBinding { HostIP = "127.0.0.1", HostPort = "5353" }],
            });

        var command = ContainerRunSpecBuilder.BuildDockerRun(container);

        command.Should().Contain("--publish 127.0.0.1:5353:53/udp");
    }

    [Fact]
    public void BuildDockerRun_ShouldUseInspectMountsNotHostConfigMounts()
    {
        // 用 -v 起的容器 HostConfig.Mounts 是空的，只看它会生成一条没有卷的命令
        var container = CreateContainer(configure: container =>
        {
            container.HostConfig!.Mounts = [];
            container.Mounts =
            [
                new MountPoint { Type = "volume", Name = "data", Source = "/var/lib/docker/volumes/data/_data", Destination = "/data", RW = false },
            ];
        });

        var command = ContainerRunSpecBuilder.BuildDockerRun(container);

        command.Should().Contain("--volume /var/lib/docker/volumes/data/_data:/data:ro");
    }

    [Fact]
    public void BuildDockerRun_ShouldMapOnFailureWithRetries()
    {
        var container = CreateContainer(configure: container =>
            container.HostConfig!.RestartPolicy = new RestartPolicy
            {
                Name = RestartPolicyKind.OnFailure,
                MaximumRetryCount = 3,
            });

        var command = ContainerRunSpecBuilder.BuildDockerRun(container);

        command.Should().Contain("--restart on-failure:3");
    }

    [Fact]
    public void BuildDockerRun_ShouldFormatHealthCheckDurationWithUnit()
    {
        // 直接把 TimeSpan 转字符串会得到 00:00:30，Docker 会把它当成 0
        var container = CreateContainer(configure: container =>
            container.Config!.Healthcheck = new HealthcheckConfig
            {
                Test = ["CMD-SHELL", "curl -f http://localhost/ || exit 1"],
                Interval = TimeSpan.FromSeconds(30),
                Timeout = TimeSpan.FromSeconds(5),
                Retries = 3,
            });

        var command = ContainerRunSpecBuilder.BuildDockerRun(container);

        command.Should().Contain("--health-interval 30s");
        command.Should().Contain("--health-timeout 5s");
        command.Should().Contain("--health-retries 3");
    }

    [Fact]
    public void BuildDockerRun_ShouldAppendCommandArguments()
    {
        var container = CreateContainer(configure: container =>
            container.Config!.Cmd = ["nginx", "-g", "daemon off;"]);

        var command = ContainerRunSpecBuilder.BuildDockerRun(container);

        command.Should().EndWith("nginx -g 'daemon off;'");
    }

    [Fact]
    public void BuildComposeFragment_ShouldNormalizeServiceName()
    {
        // Docker 允许大写与下划线，compose 服务名不允许
        var container = CreateContainer(configure: container => container.Name = "/My_Web");

        var fragment = ContainerRunSpecBuilder.BuildComposeFragment(container);

        fragment.Should().Contain("  my_web:");
    }

    [Fact]
    public void BuildComposeFragment_ShouldDropComposeBookkeepingLabels()
    {
        var container = CreateContainer(configure: container =>
            container.Config!.Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["com.docker.compose.project"] = "shop",
                ["com.docker.compose.service"] = "web",
                ["team"] = "platform",
            });

        var fragment = ContainerRunSpecBuilder.BuildComposeFragment(container);

        fragment.Should().Contain("team: platform");
        fragment.Should().NotContain("com.docker.compose");
    }

    [Fact]
    public void BuildComposeFragment_ShouldUseNetworkModeForHostNetwork()
    {
        // host / none 是模式，不能写进 networks 列表（那里只放自建网络）
        var container = CreateContainer(configure: container => container.HostConfig!.NetworkMode = "host");

        var fragment = ContainerRunSpecBuilder.BuildComposeFragment(container);

        fragment.Should().Contain("network_mode: host");
        fragment.Should().NotContain("    networks:");
    }

    [Fact]
    public void BuildComposeFragment_ShouldListNamedNetwork()
    {
        var container = CreateContainer(configure: container => container.HostConfig!.NetworkMode = "shop_default");

        var fragment = ContainerRunSpecBuilder.BuildComposeFragment(container);

        fragment.Should().Contain("    networks:");
        fragment.Should().Contain("      - shop_default");
    }

    private static ContainerInspectResponse CreateContainer(Action<ContainerInspectResponse>? configure = null)
    {
        var container = new ContainerInspectResponse
        {
            ID = Guid.NewGuid().ToString("N"),
            Name = "/web",
            Image = "sha256:" + Guid.NewGuid().ToString("N"),
            Config = new ContainerConfig
            {
                Image = "nginx:1.27",
                Env = [],
                Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            },
            HostConfig = new HostConfig
            {
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
                PortBindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal),
            },
            Mounts = [],
        };

        configure?.Invoke(container);

        return container;
    }
}
