using Whalefleet.Application.Registries;

namespace Whalefleet.Application.Abstractions;

/// <summary>
/// Registry 凭据解析（供推镜像时按地址取凭据）。
/// </summary>
/// <remarks>
/// <para><b>为什么单独抽一个接口，而不是让镜像用例依赖 <c>RegistryService</c></b>：
/// 镜像用例真正需要的只是「按地址拿一条凭据」这一件事，
/// 而 <see cref="RegistryService"/> 还带着列表 / 新增 / 删除 / 测试连通性等一堆 CRUD。
/// 直接依赖它等于让「推镜像」被「Registry 配置页」的重构连累。</para>
/// <para><b>实现由 <see cref="RegistryService"/> 承担</b>，注册时两个身份指向同一实例
/// （<c>AddScoped&lt;RegistryService&gt;()</c> 之后用委托再注册一次接口），
/// 因此凭据解析与配置管理走的是同一套解密与查找逻辑，不会出现两套口径。</para>
/// </remarks>
public interface IRegistryCredentialResolver
{
    /// <summary>
    /// 解析某个地址对应的 Registry 凭据。
    /// </summary>
    /// <param name="serverAddress">服务地址（<c>host</c> 或 <c>host:port</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>凭据；未登记该地址时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>返回的明文口令只在单次调用内存在</b>：调用方把它交给 Docker 客户端后即丢弃。
    /// 它不进日志、不进响应、不落库。
    /// </remarks>
    Task<RegistryCredentialValue?> ResolveCredentialAsync(
        string serverAddress,
        CancellationToken cancellationToken = default);
}
