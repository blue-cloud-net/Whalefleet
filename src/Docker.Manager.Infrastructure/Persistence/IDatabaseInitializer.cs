namespace Docker.Manager.Infrastructure.Persistence;

/// <summary>
/// 数据库初始化器：把「架构迁移 + WAL 启用 + 默认数据种子」打包成一次启动期调用。
/// </summary>
/// <remarks>
/// <para><b>由宿主在开始服务之前显式调用</b>（见 <c>Program.cs</c>），而不是藏在
/// <c>IHostedService</c> 里：内置 Web 宿主的 <c>StartAsync</c> 与其它托管服务的启动顺序
/// 取决于注册次序，一旦排在后面，第一个请求就可能撞上「no such table」。
/// 显式调用把失败点固定在启动阶段——建表失败宁可启动失败。</para>
/// <para><b>幂等</b>：重复调用安全（迁移按版本跳过；默认主机只在表为空时种入）。</para>
/// </remarks>
public interface IDatabaseInitializer
{
    /// <summary>
    /// 执行初始化。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
