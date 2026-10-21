using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Application.HostTags;

/// <summary>
/// 主机标签用例（列表 / 创建 / 更新 / 删除 / 批量打标与清标）。
/// </summary>
/// <remarks>
/// <para><b>标签不参与授权</b>：这里不做任何权限判定——管理端点由 Api 层的权限策略把关，
/// 「谁可见哪台主机」由资源可见性服务负责。让标签参与授权会让「给主机打个标签」
/// 变成一次隐式提权，而界面上完全看不出来。</para>
/// <para><b>错误码</b>：标签名重复 <c>1307</c>、标签不存在 <c>1306</c>、
/// 主机不存在 <c>1300</c>（复用主机管理的码，因为它就是同一件事）。</para>
/// <para><b>批量上限 100</b>：与批量删除同一口径。一个人不会勾超过 100 台主机，
/// 而放行无上限会让一次请求变成几千次库往返。</para>
/// </remarks>
public sealed class HostTagService
{
    /// <summary>
    /// 单次批量打标 / 清标允许的主机数与标签数上限。
    /// </summary>
    public const int MaxBatchSize = 100;

    private readonly IHostTagRepository _tags;
    private readonly IHostRepository _hosts;

    /// <summary>
    /// 初始化用例服务。
    /// </summary>
    /// <param name="tags">标签仓储。</param>
    /// <param name="hosts">主机仓储（把主机名翻译成主键）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public HostTagService(IHostTagRepository tags, IHostRepository hosts)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(hosts);

        _tags = tags;
        _hosts = hosts;
    }

    /// <summary>
    /// 列出全部标签（含每个标签挂载的主机数）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按名称升序的标签列表。</returns>
    /// <remarks>不做分页：标签由人工维护，现实里是十几个；加 <c>pageIndex</c> 只会让调用方多一层判断。</remarks>
    public async Task<IReadOnlyList<HostTagSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var tags = await _tags.QueryAllListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await _tags.CountByTagAsync(cancellationToken).ConfigureAwait(false);

        return [.. tags.Select(tag => Map(tag, counts))];
    }

    /// <summary>
    /// 新建标签。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的标签。</returns>
    /// <exception cref="BusinessException">标签名为空（<c>1000</c>）或已被占用（<c>1307</c>）。</exception>
    public async Task<HostTagSummary> CreateAsync(
        CreateHostTagRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = Require(request.Name, "name");

        await this.EnsureNameAvailableAsync(name, excludedId: null, cancellationToken).ConfigureAwait(false);

        var tag = Build(name, request.Description, request.Color);

        await _tags.AddAsync(tag, cancellationToken).ConfigureAwait(false);

        // 新建的标签必定没有主机，计数直接给 0：再查一次库只为了得到一个必然的值
        return Map(tag, EmptyCounts);
    }

    /// <summary>
    /// 更新标签（改名 / 描述 / 颜色）。
    /// </summary>
    /// <param name="tagId">标签主键。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新后的标签。</returns>
    /// <exception cref="BusinessException">标签不存在（<c>1306</c>）或新名称已被占用（<c>1307</c>）。</exception>
    public async Task<HostTagSummary> UpdateAsync(
        Guid tagId,
        UpdateHostTagRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tag = await this.RequireTagAsync(tagId, cancellationToken).ConfigureAwait(false);

        if (request.Name is not null)
        {
            var name = Require(request.Name, "name");

            await this.EnsureNameAvailableAsync(name, tagId, cancellationToken).ConfigureAwait(false);
            Apply(tag, () => tag.Rename(name));
        }

        // 未提供表示不改，空串表示清空（见 UpdateHostTagRequest 的说明）
        if (request.Description is not null)
        {
            Apply(tag, () => tag.Describe(request.Description));
        }

        if (request.Color is not null)
        {
            Apply(tag, () => tag.SetColor(request.Color));
        }

        await _tags.UpdateAsync(tag, cancellationToken).ConfigureAwait(false);

        var counts = await _tags.CountByTagAsync(cancellationToken).ConfigureAwait(false);

        return Map(tag, counts);
    }

    /// <summary>
    /// 删除标签（关联一并清除，**不删主机**）。
    /// </summary>
    /// <param name="tagId">标签主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被解除关联的主机数。</returns>
    /// <exception cref="BusinessException">标签不存在（<c>1306</c>）。</exception>
    /// <remarks>
    /// 与删除分组同因：标签只是标记，级联删主机是灾难性的（配置与凭据一起没了）。
    /// 关联行必须一并清除——外键挡着物理删除，且「同名标签重建」会继承旧的关联。
    /// </remarks>
    public async Task<int> DeleteAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        _ = await this.RequireTagAsync(tagId, cancellationToken).ConfigureAwait(false);

        var counts = await _tags.CountByTagAsync(cancellationToken).ConfigureAwait(false);
        var affected = counts.TryGetValue(tagId, out var count) ? count : 0;

        await _tags.RemoveAssignmentsByTagAsync(tagId, cancellationToken).ConfigureAwait(false);
        await _tags.RemoveByIdAsync(tagId, cancellationToken).ConfigureAwait(false);

        return affected;
    }

    /// <summary>
    /// 给若干台主机打若干标签（幂等）。
    /// </summary>
    /// <param name="request">打标请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>无。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、主机不存在（<c>1300</c>）或标签不存在（<c>1306</c>）。
    /// </exception>
    public async Task AssignAsync(
        HostTagAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (hostIds, tagIds) = await this.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        await _tags.AssignAsync(hostIds, tagIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 撤销若干台主机身上的若干标签（幂等）。
    /// </summary>
    /// <param name="request">清标请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>无。</returns>
    /// <exception cref="BusinessException">
    /// 参数非法（<c>1000</c>）、主机不存在（<c>1300</c>）或标签不存在（<c>1306</c>）。
    /// </exception>
    public async Task RevokeAsync(
        HostTagAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (hostIds, tagIds) = await this.ResolveAsync(request, cancellationToken).ConfigureAwait(false);

        await _tags.RevokeAsync(hostIds, tagIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把标签实体投影成「挂在主机上的标签」（主机列表 / 概览用）。
    /// </summary>
    /// <param name="tag">标签实体。</param>
    /// <returns>标签引用。</returns>
    public static HostTagRef ToRef(HostTag tag) => new(tag.Id, tag.Name, tag.Color);

    private static readonly IReadOnlyDictionary<Guid, int> EmptyCounts = new Dictionary<Guid, int>();

    private async Task<(IReadOnlyList<Guid> HostIds, IReadOnlyList<Guid> TagIds)> ResolveAsync(
        HostTagAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var hostNames = Normalize(request.HostIds, "hostIds");
        var tagTexts = Normalize(request.TagIds, "tagIds");

        var hostIds = new List<Guid>(hostNames.Count);

        foreach (var hostName in hostNames)
        {
            var host = await _hosts.FindByNameAsync(hostName, cancellationToken).ConfigureAwait(false)
                ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机「{hostName}」不存在");

            hostIds.Add(host.Id);
        }

        var tagIds = new List<Guid>(tagTexts.Count);

        foreach (var tagText in tagTexts)
        {
            if (!Guid.TryParse(tagText, out var tagId))
            {
                throw BusinessException.Validation($"标签标识「{tagText}」不是合法的 GUID");
            }

            _ = await this.RequireTagAsync(tagId, cancellationToken).ConfigureAwait(false);
            tagIds.Add(tagId);
        }

        return (hostIds, tagIds);
    }

    private async Task<HostTag> RequireTagAsync(Guid tagId, CancellationToken cancellationToken) =>
        await _tags.QueryByIdAsync(tagId, cancellationToken).ConfigureAwait(false)
        ?? throw new BusinessException(ErrorCodes.HostTagNotFound, "主机标签不存在");

    private async Task EnsureNameAvailableAsync(
        string name,
        Guid? excludedId,
        CancellationToken cancellationToken)
    {
        var existing = await _tags.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);

        // 改名时「查到自己」不算冲突——否则任何一次保存都会被自己的旧名字挡住
        if (existing is not null && existing.Id != excludedId)
        {
            throw new BusinessException(ErrorCodes.HostTagNameAlreadyExists, $"标签名「{name}」已被占用");
        }
    }

    private static HostTagSummary Map(HostTag tag, IReadOnlyDictionary<Guid, int> counts) => new(
        tag.Id,
        tag.Name,
        tag.Description,
        tag.Color,
        counts.TryGetValue(tag.Id, out var count) ? count : 0);

    private static HostTag Build(string name, string? description, string? color)
    {
        try
        {
            return HostTag.Create(Guid.NewGuid(), name, description, color);
        }
        catch (ArgumentException ex)
        {
            // 实体抛的是 BCL 异常，这里翻译成业务错误码，
            // 否则会走「ArgumentException → 1000」的兜底分支，丢失字段定位
            throw BusinessException.Validation(ex.Message);
        }
    }

    private static void Apply(HostTag tag, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException ex)
        {
            throw BusinessException.Validation(ex.Message);
        }
    }

    private static string Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw BusinessException.Validation($"{field} 不能为空");
        }

        return value.Trim();
    }

    /// <summary>
    /// 规范化批量列表：去空白、去重（不区分大小写）、校验非空与上限。
    /// </summary>
    /// <param name="values">原始列表。</param>
    /// <param name="field">字段名（仅供提示）。</param>
    /// <returns>去重后的列表。</returns>
    /// <remarks>
    /// 去重是必须的：同一个标签出现两次会让「打标成功数」这类统计失真，
    /// 也会让清标时对同一行执行两次 DELETE（无害，但没必要）。
    /// </remarks>
    private static List<string> Normalize(IReadOnlyList<string>? values, string field)
    {
        if (values is null || values.Count == 0)
        {
            throw BusinessException.Validation($"{field} 不能为空");
        }

        if (values.Count > MaxBatchSize)
        {
            throw BusinessException.Validation($"{field} 一次最多 {MaxBatchSize} 项");
        }

        var normalized = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw BusinessException.Validation($"{field} 不得包含空白项");
            }

            var trimmed = value.Trim();

            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }
}
