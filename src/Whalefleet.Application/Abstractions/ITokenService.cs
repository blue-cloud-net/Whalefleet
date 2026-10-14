namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 令牌服务：签发 access token（JWT）与 refresh token。
/// </summary>
/// <remarks>
/// <para><b>为什么是抽象</b>：与 <see cref="IPasswordHasher"/> 同理——实现依赖 JWT 库与签名密钥配置，
/// 属于基础设施关注点，不能进 <c>Core</c>；抽成接口后应用层只表达"需要签发令牌"，不关心算法与库。</para>
/// <para><b>生命周期与配置由实现负责</b>：令牌有效期、签发者、受众、签名密钥全部来自实现侧的配置，
/// 因此本接口<b>不</b>暴露这些参数——调用方只提供"给谁签"，不决定"签多久"。</para>
/// <para><b>时间由调用方传入</b>：与实体审计一致，时间显式传入而非在实现里取当前时间，
/// 这样单元测试可以用假时间断言过期时刻，无需等待或偷改系统时钟。</para>
/// </remarks>
public interface ITokenService
{
    /// <summary>
    /// 签发 access token。
    /// </summary>
    /// <param name="userId">用户主键（写入 <c>sub</c> 声明）。</param>
    /// <param name="username">用户名（写入 <c>unique_name</c> 声明）。</param>
    /// <param name="roleName">角色名（写入 <c>role</c> 声明，供 <c>[Authorize(Roles = …)]</c> 使用）。</param>
    /// <param name="utcNow">当前 UTC 时间（用于计算过期时刻）。</param>
    /// <returns>令牌原文与过期时间。</returns>
    /// <exception cref="ArgumentException">任一字符串参数为空或全空白。</exception>
    AccessToken CreateAccessToken(Guid userId, string username, string roleName, DateTime utcNow);

    /// <summary>
    /// 生成一个新的 refresh token（<b>随机字节，非 JWT</b>）。
    /// </summary>
    /// <param name="utcNow">当前 UTC 时间（用于计算过期时刻）。</param>
    /// <returns>令牌原文、落库用哈希与过期时间。</returns>
    RefreshTokenValue CreateRefreshToken(DateTime utcNow);

    /// <summary>
    /// 计算 refresh token 原文的落库哈希。
    /// </summary>
    /// <param name="rawToken">客户端提交的令牌原文。</param>
    /// <returns>可直接用于 <c>IRefreshTokenRepository.FindByHashAsync</c> 的哈希。</returns>
    /// <remarks>
    /// 刷新与登出时用它把"客户端给的原文"换算成"库里的哈希"。
    /// 同一实现内 <see cref="CreateRefreshToken"/> 与本法必须使用<b>同一种</b>哈希算法。
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="rawToken"/> 为空或全空白。</exception>
    string HashRefreshToken(string rawToken);
}
