using Whalefleet.Core.Repositories;

namespace Whalefleet.Application.Repositories;

/// <summary>
/// 仓库配置列表项。
/// </summary>
/// <param name="Id">配置主键。</param>
/// <param name="Name">别名（管理员可读的名字）。</param>
/// <param name="ServerAddress">规范化后的服务地址（<c>host</c> 或 <c>host:port</c>）。</param>
/// <param name="Username">登录账号；为空表示匿名访问。</param>
/// <param name="HasCredentials">是否带凭据。</param>
/// <param name="Description">备注。</param>
/// <param name="CreatedAt">创建时间（UTC）。</param>
/// <param name="LastModifiedAt">最后修改时间（UTC）；从未被修改过为 <see langword="null"/>。</param>
/// <remarks>
/// <para><b>刻意不返回口令，也不返回口令密文</b>：密文进了响应就等于口令进了响应——
/// 抓一次包就能拿到一把通用钥匙。界面要改口令时<b>重新输入</b>，
/// 不做「留空表示不修改」那套（那要求服务端能保留旧值，同样得把旧值读出来）。</para>
/// <para><b>只给 <see cref="HasCredentials"/> 而不给明文</b>：界面需要知道
/// 「这条配置是带凭据的匿名访问」还是「有账号」，但不需要知道口令是什么。</para>
/// </remarks>
public sealed record RepositorySummary(
    Guid Id,
    string Name,
    string ServerAddress,
    string Username,
    bool HasCredentials,
    string? Description,
    DateTime CreatedAt,
    DateTime? LastModifiedAt);

/// <summary>
/// 仓库连通性测试的结果。
/// </summary>
/// <param name="Reachable">是否连通。</param>
/// <param name="Message">结果说明（连通时是成功原因，不通时是原因）。</param>
/// <param name="Authenticated">是否通过认证。</param>
/// <remarks>
/// <para><b>为什么把「连通」与「认证通过」分成两个字段</b>：仓库常见的三种状态是
/// 「网络不通」「连得上但没给凭据」「连得上且凭据有效」。把它们压成一个布尔值，
/// 界面就只能显示「失败」——用户不知道该检查网络还是该重填口令。</para>
/// <para><b>不返回任何凭据相关信息</b>：失败时也不回显用户名或口令片段。</para>
/// </remarks>
public sealed record RepositoryTestResult(
    bool Reachable,
    bool Authenticated,
    string Message);
