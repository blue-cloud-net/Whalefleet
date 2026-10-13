namespace Docker.Manager.Application.Common;

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
/// 详见 <c>Docker.Manager.Api.Errors.ErrorHttpStatus</c>。</para>
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

    // ==================== 1400-1499 资源操作 ====================

    /// <summary>Docker 资源（容器 / 镜像 / Volume / Network）不存在（HTTP 404）。</summary>
    public const int DockerResourceNotFound = 1401;

    /// <summary>Docker API 调用失败（HTTP 500）。</summary>
    public const int DockerApiFailed = 1402;

    /// <summary>Docker 操作超时（HTTP 408）。</summary>
    public const int DockerTimeout = 1403;

    // ==================== 1500-1599 资源权限 ====================

    /// <summary>无权限访问此资源（HTTP 403）。</summary>
    public const int ResourceForbidden = 1500;

    /// <summary>无权限访问此主机（HTTP 403）。</summary>
    public const int HostForbidden = 1501;

    /// <summary>无权限执行此操作（缺少 write 权限，HTTP 403）。</summary>
    public const int OperationForbidden = 1502;

    // ==================== 9000-9999 服务器内部错误 ====================

    /// <summary>服务器内部错误（未捕获异常，HTTP 500）。</summary>
    public const int InternalError = 9999;
}
