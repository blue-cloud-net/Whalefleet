namespace Whalefleet.Core.Auditing;

/// <summary>
/// 一次操作的结果。
/// </summary>
/// <remarks>
/// <para><b>三档而不是「成功 / 失败」两档</b>：<see cref="Denied"/> 与
/// <see cref="Failure"/> 在现场处置上完全不同——</para>
/// <list type="bullet">
///   <item><description><see cref="Denied"/>：请求被<b>规则</b>挡住（权限不足、登录限流）。
///   资源没被碰过，需要的是「该不该给他权限」这个问题的答案。</description></item>
///   <item><description><see cref="Failure"/>：规则放行了，但<b>执行</b>出错（守护进程不可达、容器已不存在）。
///   需要的是排障。</description></item>
/// </list>
/// <para>合成一档之后，这两类问题会混在同一条筛选结果里，而它们的处置人往往都不是同一个。</para>
/// </remarks>
public enum AuditOutcome
{
    /// <summary>成功。</summary>
    Success = 0,

    /// <summary>被规则拒绝（权限不足、限流等）。</summary>
    Denied = 1,

    /// <summary>执行失败（外部资源不可用、状态冲突等）。</summary>
    Failure = 2,
}
