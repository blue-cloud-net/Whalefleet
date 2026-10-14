using Whalefleet.Application.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 进程内登录失败计数器（基于 <see cref="IMemoryCache"/>）。
/// </summary>
/// <remarks>
/// <para><b>阈值与窗口是契约而非配置</b>：<c>docs/constraints.md</c> 明确写了
/// 「按 IP + 用户名 5 次/分钟」，因此这里实现为常量。做成配置项会立刻产生
/// 「代码默认值与文档不一致」的漂移风险，而收益（运维自行调参）在当前阶段并不存在。
/// 真正需要调参时再引入配置，并同步改文档。</para>
/// <para><b>窗口不因持续失败而顺延</b>：窗口自<b>第一次</b>失败起算，到点即清零并重新计数。
/// 若改成「每次失败都重置窗口」，持续攻击会让被攻击的账号被无限期锁死（DoS 面）；
/// 固定窗口的代价是攻击者仍可每分钟试 5 次，对足够熵的口令可以接受。</para>
/// <para><b>窗口判定由本类负责，缓存过期只用于内存回收</b>：<see cref="IMemoryCache"/> 的绝对过期
/// 依赖系统时钟、无法在测试里快进，因此窗口的权威判定用注入的 <see cref="TimeProvider"/> 做，
/// 缓存 TTL 仅避免键无限累积。</para>
/// <para><b>已知边界——单进程</b>：计数存在本进程内存里，多实例部署时限流会被按实例数放大。
/// 多实例是 1.0.0 之后的目标，届时改为共享存储。</para>
/// <para><b>键里必须含归一化后的用户名</b>：用户名唯一性不区分大小写，
/// 若 <c>Alice</c> 与 <c>alice</c> 分别计数，换个大小写即可绕过限制。</para>
/// </remarks>
internal sealed class InMemoryLoginAttemptTracker : ILoginAttemptTracker
{
    /// <summary>
    /// 窗口内允许的最大失败次数。
    /// </summary>
    public const int MaxFailuresPerWindow = 5;

    /// <summary>
    /// 计数窗口长度。
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 缓存键的维度分隔符。用 NUL 而非冒号，避免"IP 里含冒号"（IPv6）造成键碰撞。
    /// </summary>
    private const char KeySeparator = '\u0000';

    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 初始化计数器。
    /// </summary>
    /// <param name="cache">内存缓存（由 DI 提供的单例）。</param>
    /// <param name="timeProvider">时间源（窗口判定用）。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public InMemoryLoginAttemptTracker(IMemoryCache cache, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _cache = cache;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public bool IsBlocked(string clientIp, string username) =>
        this.ReadWindow(BuildKey(clientIp, username)).Failures >= MaxFailuresPerWindow;

    /// <inheritdoc />
    public int RecordFailure(string clientIp, string username)
    {
        var key = BuildKey(clientIp, username);
        var current = this.ReadWindow(key);

        // 保留原窗口起点：连续失败不会把窗口往后推（见类型备注）。
        var updated = current with { Failures = current.Failures + 1 };

        // TTL 只做内存回收用；窗口的权威判定在 ReadWindow 里。
        _cache.Set(key, updated, Window);

        return updated.Failures;
    }

    /// <inheritdoc />
    public void Reset(string clientIp, string username) =>
        _cache.Remove(BuildKey(clientIp, username));

    /// <summary>
    /// 读取当前窗口；键不存在或窗口已过期时，返回一个<b>从当下开始</b>的空窗口。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <returns>当前窗口状态。</returns>
    private FailureWindow ReadWindow(string key)
    {
        var now = _timeProvider.GetUtcNow();

        if (_cache.TryGetValue(key, out var cached)
            && cached is FailureWindow window
            && now - window.StartedAt < Window)
        {
            return window;
        }

        return new FailureWindow(0, now);
    }

    private static string BuildKey(string clientIp, string username) =>
        String.Concat(clientIp, KeySeparator, username.ToLowerInvariant());

    /// <summary>
    /// 一个计数窗口的状态。
    /// </summary>
    /// <param name="Failures">窗口内的失败次数。</param>
    /// <param name="StartedAt">窗口起始时刻（第一次失败的时刻）。</param>
    private readonly record struct FailureWindow(int Failures, DateTimeOffset StartedAt);
}
