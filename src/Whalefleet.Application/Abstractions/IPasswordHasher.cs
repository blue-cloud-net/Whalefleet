namespace Whalefleet.Application.Abstractions;

/// <summary>
/// 密码哈希器：把明文密码变成可持久化的哈希串，并校验明文是否匹配哈希。
/// </summary>
/// <remarks>
/// <para><b>为什么是抽象</b>：与 <see cref="IDockerClientFactory"/> 同属"外部资源接口"——
/// 算法实现依赖第三方库（<c>Konscious.Security.Cryptography.Argon2</c>），不能进 <c>Core</c>
/// （<c>Core</c> 不得引用任何 NuGet）。抽成接口后，换库或调整参数只需改 <c>Infrastructure</c> 的一个实现类。</para>
/// <para><b>为什么是同步方法</b>：Argon2id 是<b>纯 CPU 密集</b>计算，没有 I/O。
/// 把它包成 <c>Task</c> 只会把工作推到线程池却不带来任何并发收益，反而掩盖了"这一调用会占用一个线程若干十毫秒"的事实。
/// 调用方若需限流/排队，应在自己的层面处理。</para>
/// <para><b>输出格式</b>：PHC 编码串（<c>$argon2id$v=19$m=…,t=…,p=…$salt$hash</c>），
/// 算法参数与盐随哈希一同持久化，因此日后调参不会让旧哈希失效。
/// 具体格式与解析规则见 <c>Whalefleet.Core.Auth.PasswordHash</c>。</para>
/// <para><b>安全要求</b>：实现<b>不得</b>把明文密码写入日志、异常消息或任何持久化存储；
/// 校验必须使用常量时间比较。</para>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>
    /// 计算明文密码的哈希（每次调用都使用新的随机盐）。
    /// </summary>
    /// <param name="password">明文密码；不得为空或全空白。</param>
    /// <returns>可直接落库的 PHC 编码串。</returns>
    /// <exception cref="ArgumentException"><paramref name="password"/> 为空或全空白。</exception>
    string Hash(string password);

    /// <summary>
    /// 校验明文密码是否与给定哈希匹配。
    /// </summary>
    /// <param name="password">待校验的明文密码。</param>
    /// <param name="encodedHash">此前由 <see cref="Hash"/> 产出的 PHC 编码串。</param>
    /// <returns>匹配返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>绝不抛异常</b>：密码为空、哈希为 <see langword="null"/>、哈希格式非法或被篡改时，
    /// 一律返回 <see langword="false"/>。登录接口依赖这一点——一个损坏的库行应当导致"认证失败"（401），
    /// 而不是"服务器内部错误"（500）。
    /// </remarks>
    bool Verify(string password, string encodedHash);

    /// <summary>
    /// 按与真实校验<b>相同的计算成本</b>执行一次必然失败的校验。
    /// </summary>
    /// <param name="password">调用方收到的明文密码（不校验其内容）。</param>
    /// <remarks>
    /// <para><b>用途：抵御用户名枚举的时序攻击</b>。登录时若"用户不存在"立即返回、
    /// 而"密码错误"要等 Argon2 算完，那么<b>响应耗时本身就是"该用户名是否存在"的信号</b>。
    /// 在用户不存在的分支上调用本方法，可让两条失败路径的耗时对齐。</para>
    /// <para>实现必须始终基于<b>当前配置的参数</b>计算，否则参数一改，两条路径的耗时又会分开。</para>
    /// <para>返回值无意义（必然失败），因此本方法不返回 <see langword="bool"/>——
    /// 避免调用方误以为它能用来判断什么。</para>
    /// </remarks>
    void PerformDummyVerification(string password);
}
