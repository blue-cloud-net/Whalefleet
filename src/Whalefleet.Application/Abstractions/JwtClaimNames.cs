namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 本项目 access token 使用的声明名。
/// </summary>
/// <remarks>
/// <para><b>为什么要显式定义而不是用 JWT 库的常量</b>：声明名是<b>签发方与验证方之间的契约</b>，
/// 而两侧分属不同程序集（<c>Infrastructure</c> 签发、<c>Api</c> 验证）。若各自引用 JWT 库的常量，
/// 契约就隐式依赖于"两个项目引用了同一个库的同一版本"这一偶然事实；集中定义则让契约显式可见。</para>
/// <para>本类的值与 JWT 规范中的注册名一致（<c>sub</c> / <c>unique_name</c> / <c>jti</c>），
/// <c>role</c> 是自定义短名——配合 Api 层关闭入站声明映射（<c>MapInboundClaims = false</c>），
/// 使 <c>[Authorize(Roles = "admin")]</c> 的行为不依赖框架的默认映射表。</para>
/// <para>测试刻意使用 JWT 库的常量来断言这些值，从而在"我们的常量"与"库的规范名"之间形成交叉校验。</para>
/// </remarks>
public static class JwtClaimNames
{
    /// <summary>
    /// 用户主键（JWT 注册名 <c>sub</c>）。
    /// </summary>
    public const string Subject = "sub";

    /// <summary>
    /// 用户名（JWT 注册名 <c>unique_name</c>）。
    /// </summary>
    public const string UniqueName = "unique_name";

    /// <summary>
    /// 令牌唯一标识（JWT 注册名 <c>jti</c>）。
    /// </summary>
    public const string Jti = "jti";

    /// <summary>
    /// <b>会话 id</b>（自定义短名 <c>sid</c>；0.13.0 起）。
    /// </summary>
    /// <remarks>
    /// <para>值是 <c>refresh_tokens.id</c>（UUID）——<b>一个会话一行</b>，轮换时行不变、
    /// 只更新 <c>token_hash</c>。因此 access token 在轮换前后始终指向同一个会话。</para>
    /// <para>有了它，管理员才能「按会话吊销」：吊销该行后，该会话签发的 access token
    /// 在下一次请求即失效，而不必等 15 分钟过期。</para>
    /// <para>⚠️ <b>与 <see cref="Jti"/> 不是一回事</b>：<c>jti</c> 每次签发都变（令牌级），
    /// <c>sid</c> 在会话存续期内不变（会话级）。混淆两者会让「会话列表」把一次登录
    /// 显示成多条记录。</para>
    /// </remarks>
    public const string SessionId = "sid";

    /// <summary>
    /// 角色名（自定义短名，非 JWT 注册名）。
    /// </summary>
    public const string Role = "role";
}
