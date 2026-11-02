namespace Whalefleet.Api.Security;

/// <summary>
/// 标记由全局管理权限保护、无需目标主机可见性校验的端点。
/// 主机是否存在由管理用例校验；本标记不授予任何权限。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class GlobalHostManagementAttribute : Attribute;
