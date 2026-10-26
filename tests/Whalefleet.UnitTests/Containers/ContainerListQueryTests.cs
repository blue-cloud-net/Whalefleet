using System.Globalization;
using System.Text;
using Whalefleet.Application.Containers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whalefleet.UnitTests.Containers;

/// <summary>
/// 容器列表的<b>查询语义</b>：状态筛选、关键字搜索、端口顺序。
/// </summary>
/// <remarks>
/// <para><b>这三条语义各自对应一个真实缺陷</b>：
/// 端口顺序 → 界面上的端口行会随刷新来回跳（守护进程返回的顺序是随机的）；
/// 关键字搜索 → 用户搜不到明明在列表里的容器；
/// 状态筛选 → 「仅未运行」这个选项根本表达不了（旧的布尔开关只有两态）。</para>
///
/// <para><b>为什么用假守护进程而不是 mock <c>IDockerClient</c></b>：理由同
/// <see cref="ContainerListPerformanceTests"/>——HTTP 往返 + JSON 反序列化
/// 才是真实使用中最接近的一段，而端口顺序恰恰是在反序列化之后才被整理的。</para>
/// </remarks>
public class ContainerListQueryTests
{
    /// <summary>造一个指向假守护进程的服务。</summary>
    /// <param name="daemon">假守护进程。</param>
    /// <returns>容器查询用例。</returns>
    private static ContainerService ServiceFor(FakeDockerDaemon daemon) =>
        new(new StubDockerHostAccessor(daemon.Endpoint), NullLogger<ContainerService>.Instance);

    [Fact]
    public async Task 端口顺序_守护进程返回的顺序随机时_对外结果始终一致()
    {
        // 同一个容器、同样的端口集合，只是守护进程这次给的是另一种排列。
        // 守护进程的 Ports 数组来自 Go 的 map 迭代，顺序本来就每次都可能不同。
        var first = """
            [{"Id":"c1","Names":["/app"],"Image":"nginx","ImageID":"sha256:aa","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up 3 hours",
              "Ports":[
                {"PrivatePort":443,"PublicPort":8443,"Type":"tcp","IP":"0.0.0.0"},
                {"PrivatePort":80,"PublicPort":8080,"Type":"tcp","IP":"0.0.0.0"}
              ]}]
            """;

        var second = """
            [{"Id":"c1","Names":["/app"],"Image":"nginx","ImageID":"sha256:aa","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up 3 hours",
              "Ports":[
                {"PrivatePort":80,"PublicPort":8080,"Type":"tcp","IP":"0.0.0.0"},
                {"PrivatePort":443,"PublicPort":8443,"Type":"tcp","IP":"0.0.0.0"}
              ]}]
            """;

        await using var daemon = new FakeDockerDaemon(call => call == 0 ? first : second);
        var service = ServiceFor(daemon);

        var one = await service.ListAsync("h", new ListContainersRequest { All = true });
        var two = await service.ListAsync("h", new ListContainersRequest { All = true });

        one.Items.Should().ContainSingle();
        two.Items.Should().ContainSingle();

        // 两次调用的对外顺序必须一致：否则界面上的端口行会随刷新来回跳
        one.Items[0].Ports.Select(p => p.PrivatePort).Should().Equal(80, 443);
        two.Items[0].Ports.Select(p => p.PrivatePort).Should().Equal(80, 443);
    }

    [Fact]
    public async Task 端口顺序_同容器端口时IPv4排在IPv6之前()
    {
        // `-p 8080:80` 在 Docker 里就是两条记录：0.0.0.0 与 ::。
        var body = """
            [{"Id":"c1","Names":["/app"],"Image":"nginx","ImageID":"sha256:aa","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up 3 hours",
              "Ports":[
                {"PrivatePort":80,"PublicPort":8080,"Type":"tcp","IP":"::"},
                {"PrivatePort":80,"PublicPort":8080,"Type":"tcp","IP":"0.0.0.0"},
                {"PrivatePort":80,"PublicPort":8080,"Type":"tcp","IP":"192.168.1.10"}
              ]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync("h", new ListContainersRequest { All = true });

        var ips = result.Items[0].Ports.Select(p => p.Ip).ToArray();

        // 具体 IPv4 优先，其次 IPv4 通配，最后才是 IPv6 家族
        ips.Should().Equal("192.168.1.10", "0.0.0.0", "::");
    }

    [Fact]
    public async Task 端口顺序_未映射的端口排在最后()
    {
        var body = """
            [{"Id":"c1","Names":["/app"],"Image":"nginx","ImageID":"sha256:aa","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up 3 hours",
              "Ports":[
                {"PrivatePort":443,"PublicPort":null,"Type":"tcp","IP":null},
                {"PrivatePort":80,"PublicPort":8080,"Type":"tcp","IP":"0.0.0.0"}
              ]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync("h", new ListContainersRequest { All = true });

        var ports = result.Items[0].Ports;

        ports[0].PublicPort.Should().Be(8080);
        ports[1].PublicPort.Should().BeNull();
    }

    [Fact]
    public async Task 搜索_命中名称镜像与ID()
    {
        var body = """
            [{"Id":"aaa111","Names":["/web-frontend"],"Image":"nginx:alpine","ImageID":"sha256:aa","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up 3 hours","Ports":[]},
             {"Id":"bbb222","Names":["/db"],"Image":"postgres:16","ImageID":"sha256:bb","Command":"postgres",
              "Created":1735689600,"State":"exited","Status":"Exited (0)","Ports":[]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var byName = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Search = "front" });
        var byImage = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Search = "postgres" });
        var byId = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Search = "bbb222" });

        byName.Items.Should().ContainSingle().Which.Name.Should().Be("web-frontend");
        byImage.Items.Should().ContainSingle().Which.Name.Should().Be("db");
        byId.Items.Should().ContainSingle().Which.Name.Should().Be("db");
    }

    [Fact]
    public async Task 搜索_不匹配启动命令()
    {
        // 命令里常含密钥与参数，把它当搜索面既容易命中无关内容，
        // 也会让用户以为「搜出来的就是相关的那个」。
        var body = """
            [{"Id":"aaa111","Names":["/web-frontend"],"Image":"nginx:alpine","ImageID":"sha256:aa",
              "Command":"java -jar /opt/app.jar --spring.profiles.active=prod",
              "Created":1735689600,"State":"running","Status":"Up 3 hours","Ports":[]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Search = "spring.profiles" });

        result.Total.Should().Be(0);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task 搜索_不区分大小写()
    {
        var body = """
            [{"Id":"aaa111","Names":["/Web-Frontend"],"Image":"nginx:alpine","ImageID":"sha256:aa","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up 3 hours","Ports":[]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Search = "WEB-FRONT" });

        result.Total.Should().Be(1);
    }

    [Fact]
    public async Task 状态筛选_仅运行中_不含已停止与已暂停以外的状态()
    {
        var body = """
            [{"Id":"a1","Names":["/run"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up","Ports":[]},
             {"Id":"a2","Names":["/paused"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"paused","Status":"Up (Paused)","Ports":[]},
             {"Id":"a3","Names":["/exit"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"exited","Status":"Exited (0)","Ports":[]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { State = ContainerStateFilter.Running });

        // 已暂停算「还活着」，与 docker ps 的口径一致
        result.Items.Select(c => c.Id).Should().BeEquivalentTo(["a1", "a2"]);
        result.Total.Should().Be(2);
    }

    [Fact]
    public async Task 状态筛选_仅未运行_含从未启动的容器()
    {
        var body = """
            [{"Id":"a1","Names":["/run"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up","Ports":[]},
             {"Id":"a2","Names":["/exit"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"exited","Status":"Exited (0)","Ports":[]},
             {"Id":"a3","Names":["/created"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"created","Status":"Created","Ports":[]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { State = ContainerStateFilter.Stopped });

        result.Items.Select(c => c.Id).Should().BeEquivalentTo(["a2", "a3"]);
        result.Total.Should().Be(2);
    }

    [Fact]
    public async Task 状态筛选_未知取值按全部处理()
    {
        // 筛选值来自客户端，版本错配时不该让整个列表 400。
        var body = """
            [{"Id":"a1","Names":["/run"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up","Ports":[]},
             {"Id":"a2","Names":["/exit"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"exited","Status":"Exited (0)","Ports":[]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { State = "不存在的状态" });

        result.Total.Should().Be(2);
    }

    [Fact]
    public async Task 旧的all参数_语义不变()
    {
        // 旧客户端不发 state，只发 all：true = 全部，false / 省略 = 仅运行中。
        var body = """
            [{"Id":"a1","Names":["/run"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up","Ports":[]},
             {"Id":"a2","Names":["/exit"],"Image":"nginx","ImageID":"sha256:a","Command":"nginx",
              "Created":1735689600,"State":"exited","Status":"Exited (0)","Ports":[]}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var all = await service.ListAsync("h", new ListContainersRequest { All = true });
        var runningOnly = await service.ListAsync("h", new ListContainersRequest { All = false });
        var omitted = await service.ListAsync("h", new ListContainersRequest());

        all.Total.Should().Be(2);
        runningOnly.Items.Select(c => c.Id).Should().Equal("a1");
        omitted.Items.Select(c => c.Id).Should().Equal("a1");
    }

    [Fact]
    public async Task 过滤后_total与分页自洽()
    {
        var body = BuildContainersJson(10);

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var first = await service.ListAsync(
            "h",
            new ListContainersRequest { All = true, Search = "app", PageIndex = 0, PageSize = 3 });
        var second = await service.ListAsync(
            "h",
            new ListContainersRequest { All = true, Search = "app", PageIndex = 1, PageSize = 3 });

        // 过滤发生在分页之前：total 必须是**过滤后**的条数
        first.Total.Should().Be(10);
        second.Total.Should().Be(10);

        var ids = first.Items.Concat(second.Items).Select(c => c.Id).ToArray();
        ids.Should().HaveCount(6);
        ids.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// 造 10 个名字都含 <c>app</c> 的容器。
    /// </summary>
    /// <param name="count">容器个数。</param>
    /// <returns>列表响应 JSON。</returns>
    private static string BuildContainersJson(int count)
    {
        var builder = new StringBuilder(count * 256);
        builder.Append('[');

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var id = i.ToString("x12", CultureInfo.InvariantCulture);

            builder.Append(CultureInfo.InvariantCulture, $$"""
                {
                  "Id": "{{id}}",
                  "Names": ["/app-{{id}}"],
                  "Image": "nginx:alpine",
                  "ImageID": "sha256:{{new string('a', 64)}}",
                  "Command": "nginx -g daemon off;",
                  "Created": 1735689600,
                  "State": "running",
                  "Status": "Up 3 hours",
                  "Ports": []
                }
                """);
        }

        builder.Append(']');

        return builder.ToString();
    }
}
