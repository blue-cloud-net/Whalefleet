using System.Data.Common;

namespace Whalefleet.Application.Abstractions.Persistence;

/// <summary>
/// 数据库连接工厂：按配置创建一个<b>尚未打开</b>的 ADO.NET 连接。
/// </summary>
/// <remarks>
/// <para><b>为什么是抽象</b>：应用层与仓储需要"拿到连接"这件事，但不该知道底层是 SQLite、
/// 连接串长什么样、数据库文件放哪里——那些属于 Infrastructure 与配置。</para>
/// <para><b>连接生命周期</b>：调用方用 <c>using</c> 包裹，用完即释放。底层驱动通常自带连接池
/// （<c>Microsoft.Data.Sqlite</c> 即如此），因此"每次操作新开连接"并不意味着每次新建物理连接。</para>
/// <para><b>不提供 Unit of Work</b>：0.0.3 的业务操作都是单语句写入，暂不需要跨仓储事务；
/// 将来需要时再引入显式的事务范围抽象，而不是让本接口返回长命连接。</para>
/// </remarks>
public interface IDbConnectionFactory
{
    /// <summary>
    /// 创建一个未打开的数据库连接。调用方负责打开与释放。
    /// </summary>
    /// <returns>未打开的 <see cref="DbConnection"/>。</returns>
    DbConnection Create();
}
