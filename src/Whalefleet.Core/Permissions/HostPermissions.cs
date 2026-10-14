namespace Whalefleet.Core.Permissions;

/// <summary>
/// 主机粒度权限位。
/// </summary>
/// <remarks>
/// <para>以<b>标志位</b>表达，一个用户可以同时拥有多种权限；组合结果按位判断，
/// 不引入「角色等级」这种隐式包含关系（<see cref="Admin"/> 不自动蕴含
/// <see cref="Write"/>，授权时由应用服务显式展开）。</para>
/// <para>对外 API 以小写字符串数组出现（<c>["read", "write", "admin"]</c>），
/// 转换在 DTO 映射层完成。</para>
/// <para><b>落地阶段</b>：0.3.0（本迭代只定义实体与接口，不建表）。</para>
/// </remarks>
[Flags]
public enum HostPermissions : byte
{
    /// <summary>
    /// 无权限。
    /// </summary>
    None = 0,

    /// <summary>
    /// 只读：可查看该主机的资源。
    /// </summary>
    Read = 1,

    /// <summary>
    /// 可写：可启动 / 停止 / 修改该主机的资源。
    /// </summary>
    Write = 2,

    /// <summary>
    /// 管理：可改动该主机自身的配置。
    /// </summary>
    Admin = 4,

    /// <summary>
    /// 全部权限位（用于校验传入值未越界）。
    /// </summary>
    All = Read | Write | Admin,
}
