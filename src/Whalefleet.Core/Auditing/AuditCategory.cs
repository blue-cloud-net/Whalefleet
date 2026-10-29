namespace Whalefleet.Core.Auditing;

/// <summary>
/// 审计日志的两大类。
/// </summary>
/// <remarks>
/// <para><b>为什么要分类而不是一张平表</b>：界面上它们是<b>两个页面</b>
/// （认证日志 / 活动日志），两者的过滤维度与保留价值都不同——
/// 认证日志关心「谁在什么时候从哪个 IP 登录、失败了多少次」，
/// 活动日志关心「谁对哪个资源做了什么」。</para>
/// <para><b>为什么是枚举</b>：这是<b>封闭集合</b>——只有这两类，不会再长出第三类。
/// 与之相对，<c>action</c>（如 <c>container.start</c>）会随功能增加，
/// 因此那边用字符串常量而不是枚举（见 <see cref="AuditActions"/>）。</para>
/// </remarks>
public enum AuditCategory
{
    /// <summary>认证日志：登录、登出、令牌撤销。</summary>
    Authentication = 0,

    /// <summary>活动日志：对资源与配置的操作。</summary>
    Activity = 1,
}
