using System.Reflection;

using FluentAssertions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Xunit;

namespace Whalefleet.IntegrationTests.Permissions;

/// <summary>
/// 权限策略注册的守卫测试。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：<c>[Authorize(Policy = "…")]</c> 引用一个<b>没注册</b>的策略名时，
/// ASP.NET Core 抛的是 <see cref="InvalidOperationException"/>（"The AuthorizationPolicy named '…'
/// was not found."），而不是返回 403——
/// 结果是端点一律 <b>500</b>，且错误信息与「权限不足」毫无关系。</para>
/// <para><b>这个坑已经踩过一次</b>：0.13.0 的 <c>team.read</c> / <c>team.write</c> 两条策略
/// 在 <c>PermissionPolicies</c> 里定义了、在 <c>AdminTeamsController</c> 上引用了，
/// 却<b>没在 <c>AddPermissionAuthorization</c> 里注册</c>——
/// 于是 <c>/api/v1/admin/teams/*</c> 全部 500，而当时的集成测试里<b>一个团队用例都没有</b>，
/// CI 全绿。</para>
/// <para><b>为什么用反射扫控制器而不是只断言几个常量</b>：新增策略时最容易漏的是
/// 「写了引用、忘了注册」。扫一遍所有控制器上的 <see cref="AuthorizeAttribute"/>，
/// 新增任何一条策略都会在下次 CI 里被逼着注册。</para>
/// </remarks>
public class PermissionPolicyRegistrationTests : IClassFixture<ApiFactory>
{
    /// <summary>
    /// 初始化测试。
    /// </summary>
    /// <param name="factory">集成测试宿主工厂。</param>
    public PermissionPolicyRegistrationTests(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        this.Factory = factory;
    }

    /// <summary>
    /// 集成测试宿主工厂。
    /// </summary>
    private ApiFactory Factory { get; }

    /// <summary>
    /// 控制器上引用的每一个权限策略都必须在授权选项里注册过。
    /// </summary>
    [Fact]
    public void 控制器引用的权限策略必须已注册()
    {
        var options = this.Factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        var referenced = typeof(Program)
            .Assembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type
                .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Concat(type
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))))
            .Select(attribute => attribute.Policy)
            .OfType<string>()
            .Where(policy => !String.IsNullOrWhiteSpace(policy))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(policy => policy, StringComparer.Ordinal)
            .ToList();

        referenced.Should().NotBeEmpty("仓库里至少有一个控制器引用了权限策略");

        var missing = referenced
            .Where(policy => options.GetPolicy(policy) is null)
            .ToList();

        missing.Should().BeEmpty(
            "这些策略被控制器引用但没有在 AddPermissionAuthorization 里注册——"
            + "未注册的策略名会让 ASP.NET Core 抛 InvalidOperationException，端点一律 500");
    }

    /// <summary>
    /// <c>PermissionPolicies</c> 里定义的每一个常量都必须有对应的注册。
    /// </summary>
    /// <remarks>
    /// <b>为什么这条也值得守</b>：策略常量是「策略名」的单一来源，
    /// 而注册在另一个文件里。两处漂移时，症状完全一样（500），
    /// 但只有「常量 ↔ 注册」这条断言能把它挡在 CI 之前。
    /// </remarks>
    [Fact]
    public void 权限策略常量必须全部注册()
    {
        var options = this.Factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        var declared = typeof(Whalefleet.Api.Security.PermissionPolicies)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        declared.Should().NotBeEmpty();

        var missing = declared
            .Where(policy => options.GetPolicy(policy) is null)
            .ToList();

        missing.Should().BeEmpty("权限策略常量必须与 AddPermissionAuthorization 的注册一一对应");
    }
}
