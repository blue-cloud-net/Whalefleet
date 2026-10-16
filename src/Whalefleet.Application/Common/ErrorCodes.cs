namespace Whalefleet.Application.Common;

/// <summary>
/// 业务错误码常量表。
/// </summary>
/// <remarks>
/// <para>数值与分段规则的唯一事实源是 <c>docs/api-spec.md</c> 的「业务错误码」章节：
/// 新增错误码必须先更新该文档，再在此登记常量，并在 <see cref="ErrorCodeCatalog"/> 中补充
/// 其语义分类与默认文案，否则该错误码会落入「未登记」兜底分支。</para>
/// <para>分段约定（每段预留 100 个代码，便于后续细分）：</para>
/// <list type="table">
///   <item><term>0</term><description>成功</description></item>
///   <item><term>1000-1099</term><description>通用（参数校验 / 资源不存在 / Docker 操作失败 / 超时）</description></item>
///   <item><term>1100-1199</term><description>鉴权与会话</description></item>
///   <item><term>1200-1299</term><description>用户管理</description></item>
///   <item><term>1300-1399</term><description>主机管理</description></item>
///   <item><term>1400-1499</term><description>资源操作（容器 / 镜像 / Volume / Network）</description></item>
///   <item><term>1500-1599</term><description>资源权限</description></item>
///   <item><term>9000-9999</term><description>服务器内部错误</description></item>
/// </list>
/// <para><b>重要</b>：同一分段内的 HTTP 语义并不统一——例如 <see cref="DockerOperationFailed"/>
/// 对应 500，而 <see cref="LoginRateLimited"/> 对应 429，<see cref="HostUnreachable"/> 对应 502。
/// 因此「错误码 → HTTP 状态码」的映射必须<b>逐码显式登记</b>，不能按分段推断，
/// 详见 <c>Whalefleet.Api.Errors.ErrorHttpStatus</c>。</para>
/// </remarks>
public static class ErrorCodes
{
    /// <summary>成功（HTTP 200）。</summary>
    public const int Success = 0;

    // ==================== 1000-1099 通用 ====================

    /// <summary>参数校验失败（HTTP 400）。</summary>
    public const int ValidationFailed = 1000;

    /// <summary>资源不存在（HTTP 404）。</summary>
    public const int NotFound = 1001;

    /// <summary>通用 Docker 操作失败（<c>DockerApiException</c>，HTTP 500）。</summary>
    public const int DockerOperationFailed = 1002;

    /// <summary>操作超时（<c>TaskCanceledException</c>，HTTP 408）。</summary>
    public const int OperationTimeout = 1003;

    // ==================== 1100-1199 鉴权与会话 ====================

    /// <summary>未认证：Token 缺失或无效（HTTP 401）。</summary>
    public const int Unauthenticated = 1100;

    /// <summary>Token 已过期（HTTP 401）。</summary>
    public const int TokenExpired = 1101;

    /// <summary>Refresh token 已撤销或无效（HTTP 401）。</summary>
    public const int RefreshTokenRevoked = 1102;

    /// <summary>登录速率限制（5 次/分钟，HTTP 429）。</summary>
    public const int LoginRateLimited = 1103;

    /// <summary>Terminal token 无效或已过期（HTTP 401）。</summary>
    public const int TerminalTokenInvalid = 1104;

    // ==================== 1200-1299 用户管理 ====================

    /// <summary>用户名已存在（HTTP 400）。</summary>
    public const int UsernameAlreadyExists = 1200;

    /// <summary>用户不存在（HTTP 404）。</summary>
    public const int UserNotFound = 1201;

    /// <summary>密码强度不满足要求（HTTP 400）。</summary>
    public const int WeakPassword = 1202;

    // ==================== 1300-1399 主机管理 ====================

    /// <summary>主机不存在（HTTP 404）。</summary>
    public const int HostNotFound = 1300;

    /// <summary>主机连通性失败（HTTP 502）。</summary>
    public const int HostUnreachable = 1301;

    /// <summary>主机已停用（HTTP 400）。</summary>
    public const int HostDisabled = 1302;

    /// <summary>主机名已被占用（HTTP 400）。</summary>
    public const int HostNameAlreadyExists = 1303;

    /// <summary>分组名已被占用（HTTP 400）。</summary>
    /// <remarks>
    /// 与 <see cref="HostNameAlreadyExists"/> 分开而非复用：两者都是「名字冲突」，
    /// 但界面需要把提示精确落在对应字段上，错误码一旦合并就再也分不开了。
    /// </remarks>
    public const int GroupNameAlreadyExists = 1304;

    /// <summary>分组不存在（HTTP 404）。</summary>
    public const int GroupNotFound = 1305;

    // ==================== 1400-1499 资源操作 ====================

    /// <summary>Docker 资源（容器 / 镜像 / Volume / Network）不存在（HTTP 404）。</summary>
    public const int DockerResourceNotFound = 1401;

    /// <summary>Docker API 调用失败（HTTP 500）。</summary>
    public const int DockerApiFailed = 1402;

    /// <summary>Docker 操作超时（HTTP 408）。</summary>
    public const int DockerTimeout = 1403;

    /// <summary>资源当前状态不允许该操作（HTTP 409）。</summary>
    /// <remarks>
    /// 典型场景：不带 <c>force</c> 删除运行中的容器、删除仍被容器占用的镜像。
    /// 守护进程用 409 表达这些情况，归成 <c>1402</c>/500 会让「先停容器再重试」变成「服务端炸了」。
    /// </remarks>
    public const int DockerResourceConflict = 1404;

    // ==================== 1500-1599 资源权限 ====================

    /// <summary>无权限访问此资源（HTTP 403）。</summary>
    public const int ResourceForbidden = 1500;

    /// <summary>无权限访问此主机（HTTP 403）。</summary>
    public const int HostForbidden = 1501;

    /// <summary>无权限执行此操作（缺少 write 权限，HTTP 403）。</summary>
    public const int OperationForbidden = 1502;

    /// <summary>授权记录不存在（HTTP 404）。</summary>
    /// <remarks>
    /// 与 <see cref="ResourceForbidden"/>（1500）的区别：1500 是「你没有权限」，
    /// 本码是「你问的那条授权根本不存在」。两者都是 404 / 403 的分界，
    /// 界面需要据此区分「换个用户试试」和「重新授权」。
    /// </remarks>
    public const int PermissionNotFound = 1503;

    /// <summary>角色不存在（HTTP 404）。</summary>
    public const int RoleNotFound = 1510;

    /// <summary>角色标识已被占用（HTTP 400）。</summary>
    /// <remarks>
    /// 与 <see cref="UsernameAlreadyExists"/>（1200）分开而非复用：两者都是「名字冲突」，
    /// 但界面需要把提示精确落在对应字段上，错误码一旦合并就再也分不开了。
    /// </remarks>
    public const int RoleNameAlreadyExists = 1511;

    /// <summary>角色仍有有效分配，拒绝删除（HTTP 400）。</summary>
    /// <remarks>
    /// <b>为什么不级联撤销</b>：级联会悄悄收回没人要求收回的访问权。
    /// 报这个错是把决定权交还管理员——先看谁在用，再逐个撤销，最后才删角色。
    /// 只数<b>有效</b>分配；已撤销的行不该让角色永远删不掉。
    /// </remarks>
    public const int RoleInUse = 1512;

    /// <summary>内置角色受保护，不可修改或删除（HTTP 400）。</summary>
    /// <remarks>
    /// 内置角色的语义随迁移固定。一个能被清空权限集的 <c>operator</c> 已经不是它的名字所声称的那个角色；
    /// 更现实的风险是管理员把 <c>admin</c> 改窄，把自己锁在门外且只能改库恢复。
    /// </remarks>
    public const int RoleProtected = 1513;

    /// <summary>未知权限标识（HTTP 400）。</summary>
    /// <remarks>
    /// 单列而不是复用 <see cref="ValidationFailed"/>：客户端最容易犯的错就是拼错权限名
    /// （<c>"read"</c> 而不是 <c>"host.read"</c>、<c>"ALL"</c>、<c>"7"</c>），
    /// 给它独立码，界面才能提示「请从权限目录里选」。
    /// </remarks>
    public const int PermissionUnknown = 1514;

    /// <summary>角色分配不存在（HTTP 404）。</summary>
    public const int RoleAssignmentNotFound = 1515;

    // ==================== 9000-9999 服务器内部错误 ====================

    /// <summary>服务器内部错误（未捕获异常，HTTP 500）。</summary>
    public const int InternalError = 9999;
}
