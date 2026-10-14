using Whalefleet.Api.Configuration;
using Whalefleet.IntegrationTests;
using FluentAssertions;
using Xunit;

namespace Whalefleet.IntegrationTests.Configuration;

/// <summary>
/// SPA 产物托管的端到端测试（0.0.15）。
/// </summary>
/// <remarks>
/// 这组用例守护的是**部署契约**：生产镜像里 API 进程要同源提供前端产物。
/// 最容易出的错是回退路由把 API 端点也吞了——
/// 症状是「接口返回一段 HTML」，前端表现为 JSON 解析失败，而日志里全是 200，
/// 几乎不可能靠读日志定位。</remarks>
public sealed class SpaHostingTests
{
    /// <summary>
    /// 建一个包含最小前端产物（index.html）的临时目录。
    /// </summary>
    /// <returns>产物根目录。</returns>
    private static string CreateSpaRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "whalefleet-tests", $"spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "index.html"),
            "<!doctype html><html><body><div id=\"app\"></div></body></html>");
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "assets", "app.js"), "console.log(1);");
        return root;
    }

    /// <summary>
    /// 根目录命中时返回绝对路径。
    /// </summary>
    [Fact]
    public void Resolve_WithExistingIndex_ShouldReturnAbsolutePath()
    {
        var root = CreateSpaRoot();

        var resolved = SpaRootResolver.Resolve(
            new SpaOptions { RootPath = root },
            contentRoot: AppContext.BaseDirectory);

        resolved.Should().Be(root);
    }

    /// <summary>
    /// 目录存在但里面没有入口文件时返回 null——那是「构建失败却没中断」的典型产物。
    /// </summary>
    [Fact]
    public void Resolve_WithEmptyDirectory_ShouldReturnNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "whalefleet-tests", $"spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var resolved = SpaRootResolver.Resolve(
            new SpaOptions { RootPath = root },
            contentRoot: AppContext.BaseDirectory);

        resolved.Should().BeNull();
    }

    /// <summary>
    /// 目录不存在时返回 null。
    /// </summary>
    [Fact]
    public void Resolve_WithMissingDirectory_ShouldReturnNull()
    {
        var resolved = SpaRootResolver.Resolve(
            new SpaOptions { RootPath = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}") },
            contentRoot: AppContext.BaseDirectory);

        resolved.Should().BeNull();
    }

    /// <summary>
    /// 相对路径按内容根目录解析，而不是按进程当前工作目录。
    /// </summary>
    /// <remarks>
    /// 两者在容器里可能不同（ENTRYPOINT 的 WORKDIR 与 ContentRoot 不必然一致），
    /// 按 cwd 解析会得到一个「看起来该存在却不存在」的目录。
    /// </remarks>
    [Fact]
    public void Resolve_WithRelativePath_ShouldAnchorOnContentRoot()
    {
        var contentRoot = CreateSpaRoot();

        var resolved = SpaRootResolver.Resolve(
            new SpaOptions { RootPath = "." },
            contentRoot: contentRoot);

        resolved.Should().Be(Path.GetFullPath(contentRoot));
    }

    /// <summary>
    /// 启用 SPA 但产物缺失时必须启动失败，而不是提供 404。
    /// </summary>
    [Fact]
    public void EnabledWithoutArtifacts_ShouldFailToStart()
    {
        using var factory = new ApiFactory()
            .WithSetting("Spa:Enabled", "true")
            .WithSetting("Spa:RootPath", Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}"));

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Spa*");
    }

    /// <summary>
    /// 启用 SPA 时，<c>/</c> 返回 index.html，静态资源可直取。
    /// </summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task WithArtifacts_ShouldServeIndexAndAssets()
    {
        var root = CreateSpaRoot();

        using var factory = new ApiFactory()
            .WithSetting("Spa:Enabled", "true")
            .WithSetting("Spa:RootPath", root);

        using var client = factory.CreateClient();

        using var index = await client.GetAsync("/");
        index.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await index.Content.ReadAsStringAsync()).Should().Contain("id=\"app\"");

        using var asset = await client.GetAsync("/assets/app.js");
        asset.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    /// <summary>
    /// SPA 深链回退到 index.html（前端用 <c>createWebHistory</c>）。
    /// </summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task WithArtifacts_ShouldFallbackDeepLinksToIndex()
    {
        var root = CreateSpaRoot();

        using var factory = new ApiFactory()
            .WithSetting("Spa:Enabled", "true")
            .WithSetting("Spa:RootPath", root);

        using var client = factory.CreateClient();

        // 这条路径在服务端没有任何端点；不回退的话刷新页面就是 404。
        using var response = await client.GetAsync("/hosts/local/containers");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("id=\"app\"");
    }

    /// <summary>
    /// 回退路由不得吞掉 API 端点——这是本组测试最重要的一条。
    /// </summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task WithArtifacts_ShouldNotSwallowApiEndpoints()
    {
        var root = CreateSpaRoot();

        using var factory = new ApiFactory()
            .WithSetting("Spa:Enabled", "true")
            .WithSetting("Spa:RootPath", root);

        using var client = factory.CreateClient();

        // 未登录访问受保护端点：必须是 401 的 ApiResult，而不是 index.html。
        using var response = await client.PostAsync(
            "/api/v1/hosts/list",
            content: new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    /// <summary>
    /// 未知 API 路径仍走状态码页返回 ApiResult，而不是落到前端页面。
    /// </summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task WithArtifacts_ShouldKeepApiErrorShapeForUnknownApiRoutes()
    {
        var root = CreateSpaRoot();

        using var factory = new ApiFactory()
            .WithSetting("Spa:Enabled", "true")
            .WithSetting("Spa:RootPath", root);

        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().Contain("\"code\"");
        raw.Should().NotContain("<!doctype html>");
    }

    /// <summary>
    /// 关闭 SPA 时不注册回退，未知路径由状态码页返回 ApiResult。
    /// </summary>
    /// <returns>异步任务。</returns>
    [Fact]
    public async Task WithSpaDisabled_ShouldNotFallback()
    {
        using var factory = new ApiFactory().WithSetting("Spa:Enabled", "false");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/hosts/local/containers");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().Contain("\"code\"");
        raw.Should().NotContain("<!doctype html>");
    }
}