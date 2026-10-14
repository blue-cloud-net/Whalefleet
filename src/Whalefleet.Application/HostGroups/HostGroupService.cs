using Whalefleet.Application.Common;
using Whalefleet.Core.Hosts;

namespace Whalefleet.Application.HostGroups;

/// <summary>
/// 主机分组用例（列表 / 创建 / 更新 / 删除 / 调整主机归属）。
/// </summary>
/// <remarks>
/// <para><b>分组是展示性概念</b>：这里不做任何权限判定——管理端点由 Api 层的
/// <c>[Authorize(Roles = "admin")]</c> 把关，「谁可见哪台主机」由资源可见性服务负责。
/// 分组与授权混在一起会让「把主机挪进某组」变成一次隐式提权。</para>
/// <para><b>错误码</b>：分组名重复 <c>1304</c>、分组不存在 <c>1305</c>、
/// 主机不存在 <c>1300</c>（复用主机管理的码，因为它就是同一件事）。</para>
/// <para><b>删除不删主机</b>：组内主机逐台回到未分组。级联删主机是灾难性的
/// （配置与凭据一起没了），而「主机还在、只是回到未分组」既安全又能被管理员立刻纠正。</para>
/// </remarks>
public sealed class HostGroupService
{
    private readonly IHostGroupRepository _groups;
    private readonly IHostRepository _hosts;

    /// <summary>
    /// 初始化用例服务。
    /// </summary>
    /// <param name="groups">分组仓储。</param>
    /// <param name="hosts">主机仓储（用于归属调整与主机计数）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public HostGroupService(IHostGroupRepository groups, IHostRepository hosts)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(hosts);

        _groups = groups;
        _hosts = hosts;
    }

    /// <summary>
    /// 列出全部分组（含每组的已启用主机数）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按排序权重升序的分组列表。</returns>
    /// <remarks>
    /// 不做分页：分组由人工维护，现实里是个位数。加 <c>pageIndex</c> 只会让调用方
    /// 多一层「要不要翻页」的判断，而真实数据永远在第一页。
    /// </remarks>
    public async Task<IReadOnlyList<HostGroupSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var groups = await _groups.QueryAllListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await _hosts.CountByGroupAsync(cancellationToken).ConfigureAwait(false);

        return [.. groups.Select(group => Map(group, counts))];
    }

    /// <summary>
    /// 新建分组。
    /// </summary>
    /// <param name="request">创建请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的分组。</returns>
    /// <exception cref="BusinessException">分组名为空（<c>1000</c>）或已被占用（<c>1304</c>）。</exception>
    public async Task<HostGroupSummary> CreateAsync(
        CreateHostGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = Require(request.Name, "name");

        await EnsureNameAvailableAsync(name, excludedId: null, cancellationToken).ConfigureAwait(false);

        var group = Build(name, request.Description, request.SortOrder ?? 0);

        await _groups.AddAsync(group, cancellationToken).ConfigureAwait(false);

        // 新建的分组必定是空的，计数直接给 0：再查一次库只为了得到一个必然的值
        return Map(group, EmptyCounts);
    }

    /// <summary>
    /// 更新分组（改名 / 描述 / 排序权重）。
    /// </summary>
    /// <param name="groupId">分组主键。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新后的分组。</returns>
    /// <exception cref="BusinessException">分组不存在（<c>1305</c>）或新名称已被占用（<c>1304</c>）。</exception>
    public async Task<HostGroupSummary> UpdateAsync(
        Guid groupId,
        UpdateHostGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var group = await RequireGroupAsync(groupId, cancellationToken).ConfigureAwait(false);

        if (request.Name is not null)
        {
            var name = Require(request.Name, "name");

            await EnsureNameAvailableAsync(name, groupId, cancellationToken).ConfigureAwait(false);
            ApplyName(group, name);
        }

        // 描述：未提供表示不改，空串表示清空（见 UpdateHostGroupRequest 的说明）
        if (request.Description is not null)
        {
            ApplyDescription(group, request.Description);
        }

        if (request.SortOrder is not null)
        {
            group.Reorder(request.SortOrder.Value);
        }

        await _groups.UpdateAsync(group, cancellationToken).ConfigureAwait(false);

        var counts = await _hosts.CountByGroupAsync(cancellationToken).ConfigureAwait(false);

        return Map(group, counts);
    }

    /// <summary>
    /// 删除分组（软删除），组内主机回到未分组。
    /// </summary>
    /// <param name="groupId">分组主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被移出分组的主机数量（供调用方提示「已把 N 台主机移出该组」）。</returns>
    /// <exception cref="BusinessException">分组不存在（<c>1305</c>）。</exception>
    /// <remarks>
    /// 顺序很重要：**先把主机移出，再删分组**。反过来的话，外键会把主机的
    /// <c>group_id</c> 指向一个已删除的行——它不会报错（软删除的行还在），
    /// 但界面上会表现为「主机莫名出现在未分组」，且再也找不到原因。
    /// </remarks>
    public async Task<int> DeleteAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var group = await RequireGroupAsync(groupId, cancellationToken).ConfigureAwait(false);

        var members = await _hosts.QueryByGroupAsync(groupId, cancellationToken).ConfigureAwait(false);

        foreach (var host in members)
        {
            host.AssignGroup(null);

            await _hosts.UpdateAsync(host, cancellationToken).ConfigureAwait(false);
        }

        await _groups.RemoveByIdAsync(groupId, cancellationToken).ConfigureAwait(false);

        return members.Count;
    }

    /// <summary>
    /// 调整某台主机的分组归属。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">归属请求；<c>groupId</c> 为 <see langword="null"/> 表示移出分组。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>无。</returns>
    /// <exception cref="BusinessException">主机不存在（<c>1300</c>）或目标分组不存在（<c>1305</c>）。</exception>
    /// <remarks>
    /// 入参是**主机名**而不是主键：它是对外标识（也是路径里的 <c>hostId</c>），
    /// 前端手里只有这个名字。主键只在服务端内部流转。
    /// </remarks>
    public async Task AssignHostAsync(
        string hostId,
        AssignHostGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

        var host = await _hosts.FindByNameAsync(hostId, cancellationToken).ConfigureAwait(false)
            ?? throw new BusinessException(ErrorCodes.HostNotFound, $"主机「{hostId}」不存在");

        if (request.GroupId is { } groupId)
        {
            // 必须确认目标分组存在且未删除：外键拦不住"指向已软删除的分组"
            _ = await RequireGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        }

        host.AssignGroup(request.GroupId);

        await _hosts.UpdateAsync(host, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 内置分组的固定主键，与迁移 <c>0005_host_groups.sql</c> 的种子一致。
    /// </summary>
    /// <remarks>
    /// 供前端与文档引用（列表里靠 <see cref="HostGroupSummary.Name"/> 判断展示名，
    /// 这里只记录「哪些名字是内置的」供需要固定引用的场景使用）。
    /// </remarks>
    public static IReadOnlyList<string> BuiltInNames => ["production", "staging", "local"];

    private static readonly IReadOnlyDictionary<Guid, int> EmptyCounts =
        new Dictionary<Guid, int>();

    private async Task<HostGroup> RequireGroupAsync(Guid groupId, CancellationToken cancellationToken) =>
        await _groups.QueryByIdAsync(groupId, cancellationToken).ConfigureAwait(false)
        ?? throw new BusinessException(ErrorCodes.GroupNotFound, "分组不存在");

    private async Task EnsureNameAvailableAsync(
        string name,
        Guid? excludedId,
        CancellationToken cancellationToken)
    {
        var existing = await _groups.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);

        // 改名时「查到自己」不算冲突——否则任何一次保存都会被自己的旧名字挡住
        if (existing is not null && existing.Id != excludedId)
        {
            throw new BusinessException(ErrorCodes.GroupNameAlreadyExists, $"分组名「{name}」已被占用");
        }
    }

    private static HostGroupSummary Map(HostGroup group, IReadOnlyDictionary<Guid, int> counts) => new(
        group.Id,
        group.Name,
        group.Description,
        group.SortOrder,
        counts.TryGetValue(group.Id, out var count) ? count : 0);

    private static HostGroup Build(string name, string? description, int sortOrder)
    {
        try
        {
            return HostGroup.Create(Guid.NewGuid(), name, description, sortOrder);
        }
        catch (ArgumentException ex)
        {
            // 实体抛的是 BCL 异常，这里翻译成业务错误码，
            // 否则会走「ArgumentException → 1000」的兜底分支，丢失字段定位
            throw BusinessException.Validation(ex.Message);
        }
    }

    private static void ApplyName(HostGroup group, string name)
    {
        try
        {
            group.Rename(name);
        }
        catch (ArgumentException ex)
        {
            throw BusinessException.Validation(ex.Message);
        }
    }

    private static void ApplyDescription(HostGroup group, string description)
    {
        try
        {
            group.Describe(description);
        }
        catch (ArgumentException ex)
        {
            throw BusinessException.Validation(ex.Message);
        }
    }

    private static string Require(string? value, string field)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            throw BusinessException.Validation($"{field} 不能为空");
        }

        return value.Trim();
    }
}
