namespace Docker.Manager.IntegrationTests.Infrastructure;

/// <summary>
/// 可控时间的测试用 <see cref="TimeProvider"/>（只覆写 <see cref="GetUtcNow"/>）。
/// </summary>
/// <remarks>
/// <para>工厂只用 <see cref="GetUtcNow"/> 判定空闲超时，因此在测试里把时间"拨快"即可
/// 验证 TTL 行为，无需真的等待 30 分钟。</para>
/// <para>刻意不引入 <c>Microsoft.Extensions.TimeProvider.Testing</c>：本测试只需要手工推进时钟，
/// 少一个包就少一处版本维护。</para>
/// </remarks>
internal sealed class TestTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _utcNow;

    /// <summary>
    /// 以指定时刻初始化。
    /// </summary>
    /// <param name="utcNow">初始 UTC 时刻。</param>
    public TestTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <summary>
    /// 把时钟向前拨动。
    /// </summary>
    /// <param name="delta">拨动的时长。</param>
    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _utcNow += delta;
        }
    }
}
