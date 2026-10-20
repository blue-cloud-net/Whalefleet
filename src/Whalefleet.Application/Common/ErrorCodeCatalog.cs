using System.Collections.Frozen;

namespace Whalefleet.Application.Common;

/// <summary>
/// 错误码目录：为 <see cref="ErrorCodes"/> 中的每个错误码提供语义分类与默认文案。
/// </summary>
/// <remarks>
/// <para>这是错误码的「唯一解释入口」：全局异常处理中间件通过 <see cref="Classify"/> 决定
/// 响应的 HTTP 状态码，通过 <see cref="GetDefaultMessage"/> 取得兜底文案。</para>
/// <para>本类<b>只描述语义分类</b>，不涉及 HTTP 状态码——HTTP 映射属于接口层，
/// 由 <c>Whalefleet.Api.Errors.ErrorHttpStatus</c> 负责。</para>
/// <para>未登记的错误码一律按 <see cref="ErrorCategory.Internal"/> 处理，而<b>不是</b>按数值分段推断。
/// 理由是分段内 HTTP 语义并不统一（同段内既有 404 也有 502、429），推断会把服务端故障
/// 错误地伪装成「客户端请求不合法」。</para>
/// </remarks>
public static class ErrorCodeCatalog
{
    /// <summary>未登记错误码的兜底消息。</summary>
    private const string UnknownMessage = "请求处理失败";

    /// <summary>
    /// 错误码 → （语义分类，默认文案）映射表。
    /// 使用 <see cref="FrozenDictionary{TKey,TValue}"/> 以获得最优读取性能。
    /// </summary>
    private static readonly FrozenDictionary<int, (ErrorCategory Category, string Message)> Entries =
        new Dictionary<int, (ErrorCategory Category, string Message)>
        {
            [ErrorCodes.Success] = (ErrorCategory.Success, "ok"),

            [ErrorCodes.ValidationFailed] = (ErrorCategory.Validation, "参数校验失败"),
            [ErrorCodes.NotFound] = (ErrorCategory.NotFound, "资源不存在"),
            [ErrorCodes.DockerOperationFailed] = (ErrorCategory.Internal, "Docker 操作失败"),
            [ErrorCodes.OperationTimeout] = (ErrorCategory.Timeout, "操作超时"),

            [ErrorCodes.Unauthenticated] = (ErrorCategory.Unauthorized, "未认证"),
            [ErrorCodes.TokenExpired] = (ErrorCategory.Unauthorized, "Token 已过期"),
            [ErrorCodes.RefreshTokenRevoked] = (ErrorCategory.Unauthorized, "Refresh token 已撤销或无效"),
            [ErrorCodes.LoginRateLimited] = (ErrorCategory.RateLimited, "登录尝试过于频繁"),
            [ErrorCodes.TerminalTokenInvalid] = (ErrorCategory.Unauthorized, "Terminal token 无效或已过期"),

            [ErrorCodes.UsernameAlreadyExists] = (ErrorCategory.Validation, "用户名已存在"),
            [ErrorCodes.UserNotFound] = (ErrorCategory.NotFound, "用户不存在"),
            [ErrorCodes.WeakPassword] = (ErrorCategory.Validation, "密码强度不满足要求"),

            [ErrorCodes.HostNotFound] = (ErrorCategory.NotFound, "主机不存在"),
            [ErrorCodes.HostUnreachable] = (ErrorCategory.UpstreamFailure, "主机连通性失败"),
            [ErrorCodes.HostDisabled] = (ErrorCategory.Validation, "主机已停用"),
            [ErrorCodes.HostNameAlreadyExists] = (ErrorCategory.Validation, "主机名已被占用"),
            [ErrorCodes.GroupNameAlreadyExists] = (ErrorCategory.Validation, "分组名已被占用"),
            [ErrorCodes.GroupNotFound] = (ErrorCategory.NotFound, "分组不存在"),

            [ErrorCodes.DockerResourceNotFound] = (ErrorCategory.NotFound, "容器 / 镜像 / Volume / Network 不存在"),
            [ErrorCodes.DockerApiFailed] = (ErrorCategory.Internal, "Docker API 调用失败"),
            [ErrorCodes.DockerTimeout] = (ErrorCategory.Timeout, "Docker 操作超时"),
            [ErrorCodes.DockerResourceConflict] = (ErrorCategory.Conflict, "资源当前状态不允许该操作"),

            [ErrorCodes.ResourceForbidden] = (ErrorCategory.Forbidden, "无权限访问此资源"),
            [ErrorCodes.HostForbidden] = (ErrorCategory.Forbidden, "无权限访问此主机"),
            [ErrorCodes.OperationForbidden] = (ErrorCategory.Forbidden, "无权限执行此操作"),
            [ErrorCodes.PermissionNotFound] = (ErrorCategory.NotFound, "授权不存在"),

            [ErrorCodes.RoleNotFound] = (ErrorCategory.NotFound, "角色不存在"),
            [ErrorCodes.RoleNameAlreadyExists] = (ErrorCategory.Validation, "角色标识已存在"),
            [ErrorCodes.RoleInUse] = (ErrorCategory.Validation, "该角色仍有有效分配，请先撤销分配"),
            [ErrorCodes.RoleProtected] = (ErrorCategory.Validation, "内置角色不可修改或删除"),
            [ErrorCodes.PermissionUnknown] = (ErrorCategory.Validation, "未知的权限标识"),
            [ErrorCodes.RoleAssignmentNotFound] = (ErrorCategory.NotFound, "角色分配不存在"),

            [ErrorCodes.RepositoryNotFound] = (ErrorCategory.NotFound, "仓库配置不存在"),
            [ErrorCodes.RepositoryNameAlreadyExists] = (ErrorCategory.Validation, "仓库别名已被占用"),
            [ErrorCodes.RepositoryAddressAlreadyExists] = (ErrorCategory.Validation, "仓库地址已被占用"),
            [ErrorCodes.RepositoryCredentialMissing] = (ErrorCategory.Validation, "未登记该地址的仓库凭据"),
            [ErrorCodes.RepositoryAuthFailed] = (ErrorCategory.Validation, "仓库拒绝了该凭据"),
            [ErrorCodes.RepositoryUnreachable] = (ErrorCategory.UpstreamFailure, "仓库不可达"),
            [ErrorCodes.ImageBuildFailed] = (ErrorCategory.Internal, "镜像构建失败"),
            [ErrorCodes.ImagePushFailed] = (ErrorCategory.Internal, "镜像推送失败"),

            [ErrorCodes.InternalError] = (ErrorCategory.Internal, "服务器内部错误"),
        }.ToFrozenDictionary();

    /// <summary>
    /// 获取已登记的错误码集合（不含未登记时的兜底值），供测试与文档生成使用。
    /// </summary>
    public static IEnumerable<int> KnownCodes => Entries.Keys;

    /// <summary>
    /// 判断指定错误码是否已在目录中登记。
    /// </summary>
    /// <param name="code">业务错误码。</param>
    /// <returns>已登记返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    public static bool IsKnown(int code) => Entries.ContainsKey(code);

    /// <summary>
    /// 获取错误码的语义分类。
    /// </summary>
    /// <param name="code">业务错误码。</param>
    /// <returns>已登记时返回其分类；未登记时返回 <see cref="ErrorCategory.Internal"/>。</returns>
    public static ErrorCategory Classify(int code) =>
        Entries.TryGetValue(code, out var entry) ? entry.Category : ErrorCategory.Internal;

    /// <summary>
    /// 获取错误码的默认文案。
    /// </summary>
    /// <param name="code">业务错误码。</param>
    /// <returns>已登记时返回其默认文案；未登记时返回兜底文案。</returns>
    public static string GetDefaultMessage(int code) =>
        Entries.TryGetValue(code, out var entry) ? entry.Message : UnknownMessage;
}
