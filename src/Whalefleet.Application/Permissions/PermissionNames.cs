namespace Whalefleet.Application.Permissions;

/// <summary>
/// 权限标识数组的对外校验与规范化。
/// </summary>
/// <remarks>
/// <para><b>为什么不把校验放在实体里</b>：<c>Core</c> 的
/// <see cref="Core.Permissions.Role.SetPermissions"/> 抛 <see cref="ArgumentException"/>，
/// 那是给调用方看的编程错误。这里要做的是把客户端发来的任意字符串翻译成
/// <see cref="Common.BusinessException"/>（<c>1000</c> / 400），两者不是同一层。</para>
/// <para><b>为什么是白名单而不是宽松解析</b>：<c>"7"</c>、<c>"read, write"</c>、<c>"ALL"</c> 这类
/// 客户端绝不该发的东西必须报错。把未知输入猜成某个权限组合，猜错的方向永远是「权限更多」。</para>
/// <para><b>校验必须发生在写库之前</b>：非法权限名不允许在库里留下「待修正」的记录——
/// 那等于让客户端可以往库里塞任何它想要的垃圾。</para>
/// </remarks>
public static class PermissionNames
{
    /// <summary>
    /// 校验并规范化客户端给出的权限数组。
    /// </summary>
    /// <param name="permissions">客户端给出的权限标识数组。</param>
    /// <param name="field">出错时用于拼消息的字段名。</param>
    /// <param name="allowEmpty">
    /// 是否允许空数组。<c>false</c> 时空数组报 <c>1000</c>（用于「提交权限集」这类必须有内容的场景）。
    /// </param>
    /// <returns>规范化后的权限集：转小写、去重、<b>按目录顺序排列</b>。</returns>
    /// <exception cref="Common.BusinessException">
    /// 数组为空（且不允许）、含空串，或含目录之外的权限名（<c>1000</c>）。
    /// </exception>
    public static IReadOnlyList<string> Normalize(
        IReadOnlyList<string>? permissions,
        string field,
        bool allowEmpty = false)
    {
        if (permissions is null || permissions.Count == 0)
        {
            if (allowEmpty)
            {
                return [];
            }

            throw Common.BusinessException.Validation($"{field} 不能为空");
        }

        var normalized = new HashSet<string>(StringComparer.Ordinal);

        foreach (var permission in permissions)
        {
            if (String.IsNullOrWhiteSpace(permission))
            {
                throw Common.BusinessException.Validation($"{field} 含空的权限标识");
            }

            if (!Core.Permissions.Permission.Exists(permission))
            {
                throw Common.BusinessException.Validation(
                    $"{field} 含未知的权限名「{permission}」；权限目录见 GET /api/v1/admin/roles/permissions");
            }

            normalized.Add(permission.Trim().ToLowerInvariant());
        }

        return [.. Core.Permissions.Permission.All.Where(normalized.Contains)];
    }
}
