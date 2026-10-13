using Dapper;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.IntegrationTests.Persistence;

/// <summary>
/// Dapper 全局映射（<c>DapperTypeMaps</c>）的守门测试。
/// </summary>
/// <remarks>
/// <para><b>为什么值得单独守</b>：<c>MatchNamesWithUnderscores</c> 是进程级开关，
/// 漏配<b>不会报错</b>，只会让 <c>creation_time</c> 之类的列静默喂不进属性，
/// 表现为"实体字段全是默认值"这种最难查的故障。</para>
/// <para>各类自定义 <c>ITypeHandler</c>（枚举文本、Guid 规范文本、UTC 时间）的<b>效果</b>
/// 由 <see cref="SqliteUserRepositoryTests"/>、<see cref="SqliteHostRepositoryTests"/>、
/// <see cref="SqliteRefreshTokenRepositoryTests"/> 的"落库形态"用例覆盖。</para>
/// </remarks>
public class DapperTypeMapsTests
{
    [Fact]
    public async Task 创建连接工厂_即完成列名风格映射注册()
    {
        // 构造 PersistenceTestHost 会解析 IDbConnectionFactory，进而触发 DapperTypeMaps.Initialize
        await using var host = new PersistenceTestHost();

        await host.InitializeAsync();

        DefaultTypeMap.MatchNamesWithUnderscores.Should().BeTrue();
    }
}
