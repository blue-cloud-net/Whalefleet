using Docker.Manager.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace Docker.Manager.IntegrationTests.Persistence;

/// <summary>
/// <c>SqliteConnectionFactory</c> 的连接串组装测试。
/// </summary>
/// <remarks>
/// <para><b>为什么单独测这个</b>：连接串是「配置 → 驱动行为」之间唯一的接线点，
/// 而这里的正确性无法从业务测试反推——业务测试只关心「能读写」，
/// 池化开关接错（比如配置写了却没拼进连接串）不会有任何可观察的业务症状，
/// 却会让集成测试重新暴露在全局清池的互踩之下。</para>
/// <para><b>为什么不留垃圾</b>：<c>Create()</c> 只构造连接对象、不打开连接，
/// 因此数据库文件永远不会落盘；路径都落在已存在的临时目录下，工厂的目录准备是空操作。</para>
/// </remarks>
public class SqliteConnectionFactoryTests
{
    /// <summary>
    /// 未显式配置时，连接串应保持池化开启（生产默认值，性能相关）。
    /// </summary>
    [Fact]
    public void 未配置时连接串默认开启池化()
    {
        var factory = CreateFactory(new SqliteOptions { DatabasePath = SampleDatabasePath() });

        ConnectionStringOf(factory).Should().Contain("Pooling=True");
    }

    /// <summary>
    /// 显式关闭时，连接串必须真的带上 <c>Pooling=False</c>（集成测试的隔离前提）。
    /// </summary>
    [Fact]
    public void 配置为false时连接串关闭池化()
    {
        var factory = CreateFactory(new SqliteOptions
        {
            DatabasePath = SampleDatabasePath(),
            Pooling = false,
        });

        ConnectionStringOf(factory).Should().Contain("Pooling=False");
    }

    /// <summary>
    /// 关闭池化不应影响原有连接串关键字（外键、超时、打开模式）。
    /// </summary>
    /// <remarks>
    /// 守的是「改一个关键字时手滑删掉别的」——外键约束一旦静默丢失，
    /// 受影响的是全套仓储测试，排查成本远高于这里的一行断言。
    /// </remarks>
    [Fact]
    public void 关闭池化不影响其他连接串关键字()
    {
        var factory = CreateFactory(new SqliteOptions
        {
            DatabasePath = SampleDatabasePath(),
            CommandTimeoutSeconds = 17,
            Pooling = false,
        });

        var connectionString = ConnectionStringOf(factory);

        connectionString.Should().Contain("Foreign Keys=True");
        connectionString.Should().Contain("Default Timeout=17");
        connectionString.Should().Contain("Mode=ReadWriteCreate");
    }

    /// <summary>
    /// 取一个位于「已存在的临时目录」下的数据库路径，使工厂的目录准备逻辑成为空操作。
    /// </summary>
    /// <returns>数据库文件路径。</returns>
    private static string SampleDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"dm-factory-test-{Guid.NewGuid():N}.sqlite");

    private static SqliteConnectionFactory CreateFactory(SqliteOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options));

    private static string ConnectionStringOf(SqliteConnectionFactory factory)
    {
        using var connection = factory.Create();

        return connection.ConnectionString;
    }
}
