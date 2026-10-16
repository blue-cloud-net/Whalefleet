namespace Whalefleet.Application.Common;

/// <summary>
/// 批量端点的入参归一化。
/// </summary>
/// <remarks>
/// <para><b>为什么容器与镜像共用一份</b>：批量删除的入参校验规则完全一样
/// （非空、去空白、去重、上限），差异只在字段名。复制两份的代价是——
/// 哪天把上限从 100 改成 50，只会改掉其中一个，而那个不一致没人能一眼看出来。</para>
/// <para><b>上限为什么存在</b>：批量端点会为每个条目发一次 Docker 请求。
/// 不设上限时，一个传了 10000 个 ID 的请求能让守护进程持续忙上几分钟，
/// 而这对调用方毫无价值（界面上的多选从来不会有几百项）。</para>
/// </remarks>
internal static class BatchRequest
{
    /// <summary>单次批量请求的条目数上限。</summary>
    internal const int MaxBatchSize = 100;

    /// <summary>
    /// 校验并归一化一批资源标识。
    /// </summary>
    /// <param name="ids">原始标识数组。</param>
    /// <param name="field">字段名（用于拼错误消息）。</param>
    /// <returns>去空白、去空项、去重后的标识，**保持首次出现的顺序**。</returns>
    /// <exception cref="BusinessException">
    /// 数组为空（<c>1000</c>）、超过上限（<c>1000</c>）、全是空白（<c>1000</c>）。
    /// </exception>
    /// <remarks>
    /// <b>为什么去重而不是照单全收</b>：同一个容器 ID 出现两次时，第二次必然是
    /// <c>1401</c>（已经删掉了）。用户看到「成功 1 条、失败 1 条，失败的还是同一个容器」
    /// 只会觉得系统在胡说；而去重之后「删 1 个，成功 1 个」正是他期望的语义。
    /// </remarks>
    internal static IReadOnlyList<string> NormalizeIds(IReadOnlyList<string>? ids, string field)
    {
        if (ids is not { Count: > 0 })
        {
            throw BusinessException.Validation($"{field} 不能为空");
        }

        if (ids.Count > MaxBatchSize)
        {
            throw BusinessException.Validation($"{field} 最多 {MaxBatchSize} 个，当前 {ids.Count} 个");
        }

        var normalized = new List<string>(ids.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            var trimmed = id?.Trim();

            if (String.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized.Count > 0
            ? normalized
            : throw BusinessException.Validation($"{field} 不能全为空值");
    }
}
