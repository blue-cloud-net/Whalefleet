using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Whalefleet.Application.Containers;
using Whalefleet.Application.Common;

using Xunit;

namespace Whalefleet.UnitTests.Containers;

/// <summary>
/// 容器列表按 <b>compose 项目</b> 过滤的语义。
/// </summary>
/// <remarks>
/// <para><b>这条过滤是编排详情页的地基</b>：详情页的主体是「这个 Compose 项目下的容器」，
/// 而容器列表本身是全主机维度的。过滤若失效，用户会在编排详情页里看到一堆
/// 与该编排毫无关系的容器——而且<b>看不出是过滤出了问题</b>（列表看起来是正常的容器列表）。</para>
///
/// <para><b>三条必须守住的语义</b>：
/// ① 过滤<b>下推到守护进程</b>（不是拉全量回来再筛）；
/// ② 一次性容器（<c>docker compose run</c>）被剔除，但<b>只在按项目过滤时</b>；
/// ③ 非法项目名直接报错，而不是静默返回空列表。</para>
///
/// <para><b>为什么用假守护进程</b>：理由同 <see cref="ContainerListQueryTests"/>——
/// 只有真的发一次 HTTP，才能验到「查询串里带没带 filters」这件事。</para>
/// </remarks>
public class ContainerProjectFilterTests
{
    /// <summary>
    /// 造一个指向假守护进程的服务。
    /// </summary>
    /// <param name="daemon">假守护进程。</param>
    /// <returns>容器查询用例。</returns>
    private static ContainerService ServiceFor(FakeDockerDaemon daemon) =>
        new(
            new StubDockerHostAccessor(daemon.Endpoint),
            new NoopAuditLogger(),
            NullLogger<ContainerService>.Instance);

    /// <summary>
    /// 两个 compose 项目的容器 + 一个不属于任何项目的容器。
    /// </summary>
    private const string TwoProjectsBody = """
        [{"Id":"c1","Names":["/shop-api-1"],"Image":"nginx","ImageID":"sha256:aa","Command":"nginx",
          "Created":1735689600,"State":"running","Status":"Up 3 hours","Ports":[],
          "Labels":{"com.docker.compose.project":"shop","com.docker.compose.service":"api"}},
         {"Id":"c2","Names":["/shop-worker-1"],"Image":"busybox","ImageID":"sha256:bb","Command":"sh",
          "Created":1735689600,"State":"exited","Status":"Exited (0)","Ports":[],
          "Labels":{"com.docker.compose.project":"shop","com.docker.compose.service":"worker"}},
         {"Id":"c3","Names":["/blog-api-1"],"Image":"nginx","ImageID":"sha256:aa","Command":"nginx",
          "Created":1735689600,"State":"running","Status":"Up 1 hour","Ports":[],
          "Labels":{"com.docker.compose.project":"blog","com.docker.compose.service":"api"}},
         {"Id":"c4","Names":["/standalone"],"Image":"alpine","ImageID":"sha256:cc","Command":"sh",
          "Created":1735689600,"State":"running","Status":"Up 5 min","Ports":[]}]
        """;

    [Fact]
    public async Task 按项目过滤_过滤条件下推到守护进程()
    {
        // ⚠️ **过滤必须下推**：守护进程的标签过滤是索引级的，
        // 而「拉全机容器回来在内存里筛」的代价等于「列全机容器」——
        // 编排详情页每 5 秒轮询一次，两百个容器的宿主机上接口直接被打满。
        await using var daemon = new FakeDockerDaemon(TwoProjectsBody);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Project = "shop" });

        // 过滤串真的带上了（而不是「拉全量回来再筛」）
        daemon.LastQueryString.Should().Contain("filters");
        Uri.UnescapeDataString(daemon.LastQueryString ?? string.Empty)
            .Should().Contain("com.docker.compose.project=shop");

        // 而守护进程真的按它筛了：只剩这个项目的两个容器
        result.Items.Select(c => c.Id).Should().BeEquivalentTo(["c1", "c2"]);
    }

    [Fact]
    public async Task 不指定项目_不下发标签过滤()
    {
        // 普通容器列表不带任何 compose 过滤：它列的是**全机**容器。
        await using var daemon = new FakeDockerDaemon(TwoProjectsBody);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync("h", new ListContainersRequest { All = true });

        daemon.LastQueryString.Should().NotContain("com.docker.compose");
        result.Items.Should().HaveCount(4);
    }

    [Fact]
    public async Task compose服务名_从标签投影出来()
    {
        // 编排详情页要按「服务」组织视图，而容器名（shop-api-1）与服务名（api）不是一回事。
        await using var daemon = new FakeDockerDaemon(TwoProjectsBody);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Project = "shop" });

        result.Items.Should().HaveCount(2);
        result.Items.Select(c => c.ComposeService).Should().BeEquivalentTo(["api", "worker"]);
    }

    [Fact]
    public async Task 不属于任何项目的容器_服务名为空()
    {
        await using var daemon = new FakeDockerDaemon(TwoProjectsBody);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync("h", new ListContainersRequest { All = true });

        var standalone = result.Items.Single(c => c.Id == "c4");

        standalone.ComposeService.Should().BeNull();
    }

    [Fact]
    public async Task 一次性容器_按项目过滤时被剔除()
    {
        // `docker compose run` 产生的容器带 oneoff=True：
        // 它不是「某个服务」，混进服务视图会让服务数与声明的服务数对不上。
        var body = """
            [{"Id":"c1","Names":["/shop-api-1"],"Image":"nginx","ImageID":"sha256:aa","Command":"nginx",
              "Created":1735689600,"State":"running","Status":"Up","Ports":[],
              "Labels":{"com.docker.compose.project":"shop","com.docker.compose.service":"api"}},
             {"Id":"c9","Names":["/shop-run-abc"],"Image":"alpine","ImageID":"sha256:dd","Command":"sh",
              "Created":1735689600,"State":"exited","Status":"Exited (0)","Ports":[],
              "Labels":{"com.docker.compose.project":"shop","com.docker.compose.service":"api",
                        "com.docker.compose.oneoff":"True"}}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Project = "shop" });

        result.Items.Should().ContainSingle();
        result.Items[0].Id.Should().Be("c1");
    }

    [Fact]
    public async Task 一次性容器_普通列表里照常显示()
    {
        // ⚠️ **只在按项目过滤时剔除**：普通列表里它就是用户自己起的容器，
        // 把它藏起来等于「用户明明起了一个容器，列表里却看不到」。
        var body = """
            [{"Id":"c9","Names":["/shop-run-abc"],"Image":"alpine","ImageID":"sha256:dd","Command":"sh",
              "Created":1735689600,"State":"exited","Status":"Exited (0)","Ports":[],
              "Labels":{"com.docker.compose.project":"shop","com.docker.compose.oneoff":"True"}}]
            """;

        await using var daemon = new FakeDockerDaemon(body);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync("h", new ListContainersRequest { All = true });

        result.Items.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Shop")]
    [InlineData("shop project")]
    [InlineData("-shop")]
    [InlineData("shop/other")]
    [InlineData("shop=other")]
    public async Task 非法项目名_直接报参数错误(string project)
    {
        // 项目名会拼进守护进程的标签过滤串：
        // 放进 "=" 会变成「另一个过滤条件」，症状是「筛出来的容器莫名其妙少几个」。
        // 因此必须先按 Compose 项目名规则校验。
        await using var daemon = new FakeDockerDaemon(TwoProjectsBody);
        var service = ServiceFor(daemon);

        var act = async () => await service.ListAsync(
            "h", new ListContainersRequest { All = true, Project = project });

        var exception = await act.Should().ThrowAsync<BusinessException>();

        exception.Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task 空白项目名_当作未指定()
    {
        await using var daemon = new FakeDockerDaemon(TwoProjectsBody);
        var service = ServiceFor(daemon);

        var result = await service.ListAsync(
            "h", new ListContainersRequest { All = true, Project = "   " });

        result.Total.Should().Be(4);
        daemon.LastQueryString.Should().NotContain("com.docker.compose");
    }
}