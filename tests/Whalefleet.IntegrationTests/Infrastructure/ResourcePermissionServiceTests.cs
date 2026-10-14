using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;
using Whalefleet.Core.Users;
using Whalefleet.Infrastructure.Permissions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whalefleet.IntegrationTests.Infrastructure;

/// <summary>
/// <c>ResourcePermissionService</c> 的可见性策略。
/// </summary>
/// <remarks>
/// <para>当前策略只有两条：管理员全可见；非管理员全拒绝（逐资源授权表排在 0.3.0）。</para>
/// <para><b>为什么专门测「拒绝」这一条</b>：它是 fail-closed 的体现——拿不准就不放行。
/// 一旦有人为了"先把功能跑起来"把它改成放行，这套测试会立刻响；而如果没有这条用例，
/// 那种改动不会有任何可观察的失败（界面照常工作，只是普通用户突然能停生产容器）。</para>
/// </remarks>
public class ResourcePermissionServiceTests
{
    private static readonly DockerHost _host = DockerHost.Register(
        Guid.NewGuid(),
        "production",
        DockerHostType.Tcp,
        new HostEndpoint("tcp://prod.example:2375"));

    [Theory]
    [InlineData(Role.AdminName)]
    [InlineData("ADMIN")]
    public async Task 管理员_放行(string role)
    {
        var service = CreateService(role);

        var act = async () => await service.EnsureHostVisibleAsync(Guid.NewGuid(), _host);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("viewer")]
    [InlineData(null)]
    public async Task 非管理员_拒绝并给出1501(string? role)
    {
        var service = CreateService(role);

        var act = async () => await service.EnsureHostVisibleAsync(Guid.NewGuid(), _host);

        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Code.Should().Be(ErrorCodes.HostForbidden);
    }

    [Fact]
    public async Task 拒绝消息里要说明授权何时提供()
    {
        var service = CreateService("viewer");

        var act = async () => await service.EnsureHostVisibleAsync(Guid.NewGuid(), _host);

        // 使用者看到的是 403，必须能从消息里知道"这不是 bug，是还没做的功能"。
        (await act.Should().ThrowAsync<BusinessException>())
            .Which.Message.Should().Contain("0.3.0");
    }

    [Fact]
    public async Task 主机为null时抛参数异常()
    {
        var service = CreateService(Role.AdminName);

        var act = async () => await service.EnsureHostVisibleAsync(Guid.NewGuid(), null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static ResourcePermissionService CreateService(string? role) =>
        new(new StubCurrentUser(role), NullLogger<ResourcePermissionService>.Instance);

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
}
