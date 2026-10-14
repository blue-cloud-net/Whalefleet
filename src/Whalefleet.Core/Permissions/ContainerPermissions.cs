namespace Whalefleet.Core.Permissions;

/// <summary>
/// 容器粒度权限位（主机粒度授权的<b>细粒度补充</b>）。
/// </summary>
/// <remarks>
/// <para>容器是主机内的资源，因此容器权限记录必须同时带 <c>HostId</c>——
/// 否则不同主机上同名 / 同 ID 的容器会互相串权。</para>
/// <para>本枚举<b>没有</b> <c>Admin</c>：容器的管理性操作（创建 / 删除）属于主机级
/// <see cref="HostPermissions.Write"/>，不在容器粒度重复表达。</para>
/// <para><b>落地阶段</b>：0.3.0（本迭代只定义实体与接口，不建表）。</para>
/// </remarks>
[Flags]
public enum ContainerPermissions : byte
{
    /// <summary>
    /// 无权限。
    /// </summary>
    None = 0,

    /// <summary>
    /// 只读：可查看该容器（含日志）。
    /// </summary>
    Read = 1,

    /// <summary>
    /// 可写：可启动 / 停止 / 重启该容器。
    /// </summary>
    Write = 2,

    /// <summary>
    /// 全部权限位（用于校验传入值未越界）。
    /// </summary>
    All = Read | Write,
}
