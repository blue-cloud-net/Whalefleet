using System.Net;
using System.Net.Http.Json;
using Docker.Manager.Api.DependencyInjection;
using Docker.Manager.Api.Middleware;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Hosts;
using Docker.Manager.Core.Users;
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
    private StubPermissionService _permission = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        _permission = new StubPermissionService();

        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IDockerHostAccessor>(HostsWithLocal());
        builder.Services.AddSingleton<ICurrentUser>(new StubCurrentUser(Role.AdminName));
        builder.Services.AddSingleton<IResourcePermissionService>(_permission);
        builder.Services.AddApiCurrentHost();

        _app = builder.Build();
        _app.UseRouting();
        _app.Use(MapBusinessFailureToStatusCode);
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

    [Fact]
    public async Task 无权限_在进入端点之前返回403()
    {
        _permission.Deny = true;

        var response = await _client.PostAsync("/api/v1/hosts/local/probe", content: null);

        // 端点根本没跑：这是「批量保护」的价值——无需每个端点自己写一遍权限校验。
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 全局作用域_不做主机可见性校验()
    {
        // 全局端点没有主机上下文，不该因为权限服务拒绝而被拦（它压根不针对某台主机）。
        _permission.Deny = true;

        var response = await _client.PostAsync("/api/v1/global-probe", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
    /// 把业务失败映射成状态码。
    /// </summary>
    /// <remarks>
    /// 真实应用里这一步由 <c>ExceptionHandlingMiddleware</c> 统一完成；本测试只关心
    /// 「未知主机会在中间件层失败、且不进入端点」，因此用最小映射代替整条错误管道，
    /// 避免把被测对象和错误处理耦在一起。
    /// </remarks>
    private static async Task MapBusinessFailureToStatusCode(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (BusinessException exception)
            when (exception.Code == ErrorCodes.HostNotFound || exception.Code == ErrorCodes.HostForbidden)
        {
            context.Response.StatusCode = exception.Code == ErrorCodes.HostNotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status403Forbidden;
        }
    }

    /// <summary>探针端点的响应体：回显端点读到的「当前主机」。</summary>
    private sealed record ProbePayload(string? HostId);

    /// <summary>当前用户替身：角色可切换，用于验证管理员/非管理员两条路径。</summary>
    /// <remarks>
    /// 用显式字段而不是主构造器参数：主构造器参数同时被捕获进状态又用来初始化属性时会触发 CS9124。
    /// </remarks>
    private sealed class StubCurrentUser : ICurrentUser
    {
        private readonly bool _authenticated;

        internal StubCurrentUser(string? role)
        {
            this.Role = role;
            _authenticated = role is not null;
            this.UserId = role is null ? null : Guid.NewGuid();
        }

        public bool IsAuthenticated => _authenticated;

        public Guid? UserId { get; }

        public string? Username => "stub";

        public string? Role { get; }
    }

    /// <summary>
    /// 权限服务替身：默认放行，<see cref="Deny"/> 打开时拒绝。
    /// </summary>
    /// <remarks>真实实现（管理员放行 / 非管理员拒绝）由 <c>ResourcePermissionServiceTests</c> 覆盖。</remarks>
    private sealed class StubPermissionService : IResourcePermissionService
    {
        /// <summary>是否拒绝访问（模拟非管理员访问未授权主机）。</summary>
        internal bool Deny { get; set; }

        public Task EnsureHostVisibleAsync(
            Guid userId,
            DockerHost host,
            CancellationToken cancellationToken = default) =>
            this.Deny
                ? throw new BusinessException(ErrorCodes.HostForbidden, $"无权访问主机「{host.Name}」")
                : Task.CompletedTask;

        public bool IsHostVisible(Guid userId, DockerHost host) => !this.Deny;
    }

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
