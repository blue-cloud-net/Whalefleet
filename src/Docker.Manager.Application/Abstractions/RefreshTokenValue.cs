namespace Docker.Manager.Application.Abstractions;

/// <summary>
/// 新签发的 refresh token。
/// </summary>
/// <param name="Raw">令牌原文——<b>只在签发的那一刻</b>返回给客户端，服务端不保存它。</param>
/// <param name="Hash">令牌原文的哈希——这是<b>落库</b>的值（<c>refresh_tokens.token_hash</c>）。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
/// <remarks>
/// <para><b>为什么把「原文」与「哈希」放在同一个返回值里</b>：两者必须由同一次随机数生成产生，
/// 若让调用方分别获取再自行哈希，就会出现"返回给客户端的原文"与"落库的哈希"不匹配的风险——
/// 那是极难排查的登录后立刻失效类故障。</para>
/// <para>数据库泄露时哈希无法反推出原文，因此该令牌仍不可被冒用。</para>
/// </remarks>
public sealed record RefreshTokenValue(string Raw, string Hash, DateTime ExpiresAt);
