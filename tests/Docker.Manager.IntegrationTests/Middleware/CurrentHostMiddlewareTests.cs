using System.Net;
using System.Net.Http.Json;
using Docker.Manager.Api.DependencyInjection;
using Docker.Manager.Api.Middleware;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Hosts;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Docker.Manager.IntegrationTests.Middleware;

/// <summary>
/// <c>CurrentHostMiddleware</c> 的端到端行为：请求路径 → 路由值 → <c>ICurrentHost</c> → 端点。
/// </summary>
/// <remarks>
/// <para><b>为什么自己搭一条最小管道</b>：0.0.8 还没有任何主机作用域端点（容器 / 镜像 API 在 0.0.9），
/// 若等它们落地再测，这段「从路径解析当前主机」的逻辑就要先裸奔一个迭代。这里用
/// <see cref="TestServer"/> 接一条最小管道 + 探针端点把链路真正跑通，
/// 且注册方式（<c>AddApiCurrentHost()</c> / <c>UseCurrentHost()</c>）与 <c>Program.cs</c> 完全一致——
/// 测试里另搞一套接线正是漏测的来源。</para>
/// <para>覆盖三种情形：已知主机（含大小写规范化）、未知主机（1300）、无主机段（全局作用域）。</para>
/// </remarks>
// 释放入口是 IAsyncLifetime.DisposeAsync，而 CA1001 只认同步 IDisposable 模式。
// 抑制必须写在类声明之前：诊断报在类型声明那一行，写在类体内对它无效。
#pragma warning disable CA1001
public class CurrentHostMiddlewareTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IDockerHostAccessor>(HostsWithLocal());
        builder.Services.AddApiCurrentHost();

        _app = builder.Build();
        _app.UseRouting();
        _app.Use(MapHostNotFoundTo404);
        _app.UseCurrentHost();
        _app.MapPost("/api/v1/hosts/{hostId}/probe", (ICurrentHost currentHost) =>
            Results.Json(new ProbePayload(currentHost.HostId)));
        _app.MapPost("/api/v1/global-probe", (ICurrentHost currentHost) =>
            Results.Json(new ProbePayload(currentHost.HostId)));

        await _app.StartAsync();

        _client = _app.GetTestClient();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task 主机作用域_端点能读到当前主机()
    {
        var response = await _client.PostAsync("/api/v1/hosts/local/probe", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ProbePayload>();

        payload!.HostId.Should().Be("local");
    }

    [Fact]
    public async Task 主机作用域_路径大小写不影响取值()
    {
        var response = await _client.PostAsync("/api/v1/hosts/LOCAL/probe", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ProbePayload>();

        // 取的是库里的 Name（规范形态）而不是原始路径片段，
        // 否则「同一台主机」会在缓存键与比较上出现两种写法。
        payload!.HostId.Should().Be("local");
    }

    [Fact]
    public async Task 主机不存在_在进入端点之前就失败()
    {
        var response = await _client.PostAsync("/api/v1/hosts/nope/probe", content: null);

        // 端点根本没跑（响应体为空，不是探针的输出）。
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 全局作用域_无主机上下文且不报错()
    {
        var response = await _client.PostAsync("/api/v1/global-probe", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ProbePayload>();

        // null = 无主机上下文，是正常状态：全局端点本来就不针对某台主机。
        payload!.HostId.Should().BeNull();
    }

    private static StubHostAccessor HostsWithLocal()
    {
        var accessor = new StubHostAccessor();
        accessor.Add(DockerHost.Register(
            Guid.NewGuid(),
            "local",
            DockerHostType.Socket,
            new HostEndpoint("/var/run/docker.sock")));

        return accessor;
    }

    /// <summary>
    /// 把「主机不存在」映射成 404。
    /// </summary>
    /// <remarks>
    /// 真实应用里这一步由 <c>ExceptionHandlingMiddleware</c> 统一完成；本测试只关心
    /// 「未知主机在中间件层就失败、不会进入端点」，因此用最小映射代替整条错误管道，
    /// 避免把被测对象和错误处理耦在一起。
    /// </remarks>
    private static async Task MapHostNotFoundTo404(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (BusinessException exception) when (exception.Code == ErrorCodes.HostNotFound)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    /// <summary>探针端点的响应体：回显端点读到的「当前主机」。</summary>
    private sealed record ProbePayload(string? HostId);

    /// <summary>
    /// 主机访问器替身：只实现本测试用到的解析，其余方法一旦被调用即失败。
    /// </summary>
    private sealed class StubHostAccessor : IDockerHostAccessor
    {
        private readonly Dictionary<string, DockerHost> _hosts = new(StringComparer.OrdinalIgnoreCase);

        internal void Add(DockerHost host) => _hosts[host.Name] = host;

        public Task<DockerHost> ResolveAsync(string hostId, CancellationToken cancellationToken = default) =>
            _hosts.TryGetValue(hostId, out var host)
                ? Task.FromResult(host)
                : throw new BusinessException(ErrorCodes.HostNotFound, $"主机 {hostId} 不存在");

        public Task<Docker.DotNet.IDockerClient> GetClientAsync(
            string hostId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本测试不取客户端。");

        public Task<bool> TestConnectionAsync(
            DockerHostConnection connection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本测试不测连通性。");

        public void Invalidate(DockerHostConnection connection) =>
            throw new NotSupportedException("本测试不失效客户端。");
    }
}
