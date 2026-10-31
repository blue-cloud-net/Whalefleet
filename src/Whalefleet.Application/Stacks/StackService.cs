using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Whalefleet.Core.Permissions;
using Whalefleet.Core.Stacks;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排 Stack 用例（列表 / 详情 / 部署 / 更新 / 停止 / 删除 / 任务查询）。
/// </summary>
/// <remarks>
/// <para><b>主机校验不在这里</b>：主机是否存在（<c>1300</c>）与可见性（<c>1501</c>）
/// 由 Api 层的 <c>CurrentHostMiddleware</c> 在进入本服务前完成。</para>
/// <para><b>校验在请求期、执行在后台</b>：语法错、重名、非本平台部署、无权限
/// 这些问题在 HTTP 响应里直接报出来——用户改一下 YAML 就能重来，
/// 不必等一个后台任务跑完才发现「一开始就错了」。</para>
/// <para><b>「发现」与「记忆」并存</b>：列表里的 Stack 有两个来源——
/// 守护进程上的 compose 项目（靠标签发现，<b>managed=false</b> 的那些也包括在内）
/// 与本平台数据库里记录的（<b>managed=true</b>）。
/// 只看其中一个都会给出残缺的视图：只看标签会漏掉「YAML 存了但容器被手工删了」，
/// 只看数据库会漏掉「用户自己在命令行部署的项目」。</para>
/// <para><b>日志纪律</b>：只记 Stack 名与条数，<b>不</b>记 YAML 全文与环境变量的值。</para>
/// </remarks>
public sealed class StackService
{
    /// <summary>列表默认每页条数。</summary>
    private const int DefaultPageSize = 20;

    /// <summary>列表每页条数上限（与容器 / 镜像一致）。</summary>
    private const int MaxPageSize = 100;

    /// <summary>标签过滤：只认 label 名，不带值。</summary>
    private static readonly IDictionary<string, IDictionary<string, bool>> _labelFilter =
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ComposeLabels.Project] = true,
            },
        };

    private readonly IDockerHostAccessor _accessor;
    private readonly IStackRepository _repository;
    private readonly StackTaskRegistry _tasks;
    private readonly StackOperationQueue _queue;
    private readonly IPermissionChecker _permissions;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentClientIp _clientIp;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StackService> _logger;

    /// <summary>
    /// 初始化编排用例。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="repository">Stack 仓储。</param>
    /// <param name="tasks">任务登记表。</param>
    /// <param name="queue">任务队列。</param>
    /// <param name="permissions">权限判定。</param>
    /// <param name="currentUser">当前用户。</param>
    /// <param name="clientIp">来源 IP。</param>
    /// <param name="timeProvider">时间源。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public StackService(
        IDockerHostAccessor accessor,
        IStackRepository repository,
        StackTaskRegistry tasks,
        StackOperationQueue queue,
        IPermissionChecker permissions,
        ICurrentUser currentUser,
        ICurrentClientIp clientIp,
        TimeProvider timeProvider,
        ILogger<StackService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(clientIp);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _repository = repository;
        _tasks = tasks;
        _queue = queue;
        _permissions = permissions;
        _currentUser = currentUser;
        _clientIp = clientIp;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// 列出主机上的编排 Stack。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（搜索与分页）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前页数据与总数。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<StackListResult> ListAsync(
        string hostId,
        ListStacksRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var discovered = await DiscoverAsync(client, cancellationToken).ConfigureAwait(false);
        var recorded = await _repository.ListByHostAsync(host.Id, cancellationToken).ConfigureAwait(false);

        var summaries = Merge(discovered, recorded);

        var filtered = summaries
            .Where(summary => MatchesSearch(summary.Name, request.Search))
            .Where(summary => MatchesStatus(summary.Status, request.Status))
            .ToList();

        var pageIndex = Math.Max(request.PageIndex ?? 0, 0);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        _logger.LogDebug(
            "主机 {HostId} 编排列表：发现 {Discovered} 个、已记录 {Recorded} 个（搜索={Search}），返回第 {PageIndex} 页（每页 {PageSize}）",
            hostId,
            discovered.Count,
            recorded.Count,
            string.IsNullOrWhiteSpace(request.Search) ? "无" : request.Search.Trim(),
            pageIndex,
            pageSize);

        return new StackListResult(
            [.. filtered.Skip(pageIndex * pageSize).Take(pageSize)],
            filtered.Count,
            pageIndex,
            pageSize);
    }

    /// <summary>
    /// 查询编排 Stack 详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">详情请求（名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>详情。</returns>
    /// <exception cref="BusinessException">
    /// 名称为空（<c>1000</c>）、Stack 不存在（<c>1900</c>）或主机不可达（<c>1301</c>）。
    /// </exception>
    public async Task<StackDetail> InspectAsync(
        string hostId,
        InspectStackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireName(request.Name);
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var recorded = await _repository
            .FindByHostAndNameAsync(host.Id, name, cancellationToken)
            .ConfigureAwait(false);

        var discovered = await DiscoverAsync(client, cancellationToken).ConfigureAwait(false);
        var recordedList = new List<Stack>();

        if (recorded is not null)
        {
            recordedList.Add(recorded);
        }

        var summaries = Merge(discovered, recordedList);
        var summary = summaries.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));

        if (summary is null)
        {
            throw new BusinessException(ErrorCodes.StackNotFound, $"编排 Stack「{name}」不存在");
        }

        var services = await ListServicesAsync(client, name, recorded, cancellationToken)
            .ConfigureAwait(false);

        var warnings = new List<string>();

        if (recorded is not null)
        {
            try
            {
                warnings.AddRange(ComposeParser.Parse(recorded.ComposeContent).Warnings);
            }
            catch (BusinessException)
            {
                // 库里存的 YAML 是在写入时校验过的，解析不了属于异常状态；
                // 这里不阻断详情（服务列表仍能按容器给出），只让警告为空
            }
        }

        return new StackDetail(summary, recorded?.ComposeContent, services, warnings);
    }

    /// <summary>
    /// 部署编排 Stack（新建）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">部署请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务受理结果。</returns>
    /// <exception cref="BusinessException">
    /// 名称非法（<c>1000</c>）、YAML 非法（<c>1904</c>）、同名已存在（<c>1902</c>）、
    /// 缺 <c>stack.write</c>（<c>1502</c>）或已有进行中的操作（<c>1905</c>）。
    /// </exception>
    public async Task<StackTaskAccepted> DeployAsync(
        string hostId,
        ApplyStackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireName(request.Name);
        var content = RequireComposeContent(request.ComposeContent);
        var taskId = RequireTaskId(request.TaskId);

        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await this.EnsureWritePermissionAsync(host.Id, cancellationToken).ConfigureAwait(false);

        // 解析在请求期做：语法错、含不支持的字段都直接回给用户
        var document = ComposeParser.Parse(content);

        var existing = await _repository
            .FindByHostAndNameAsync(host.Id, name, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            throw new BusinessException(
                ErrorCodes.StackNameAlreadyExists,
                $"编排 Stack「{name}」已存在——请改用「更新」而不是「部署」");
        }

        await this.EnsureNotBusyAsync(host.Name, name, cancellationToken).ConfigureAwait(false);

        _tasks.TryRegister(taskId, host.Name, name, StackOperations.Deploy, _timeProvider.GetUtcNow().UtcDateTime);

        // 先落库再入队：后台任务可能立刻开始，而它需要读到这份 YAML。
        // 写库失败必须撤销登记——否则这个 Stack 会被「占住」到 TTL 结束，
        // 用户看到的却是「已有进行中的操作」，而实际上什么任务都没跑起来。
        var entity = Stack.Create(Guid.NewGuid(), host.Id, name, content);

        try
        {
            await _repository.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _tasks.Abort(taskId);
            throw;
        }

        this.Enqueue(
            taskId,
            host.Name,
            name,
            StackOperations.Deploy,
            content,
            request.PullAlways ?? false,
            request.PruneOrphans ?? false,
            false);

        _logger.LogInformation(
            "主机 {HostId} 受理编排部署 {StackName}（任务 {TaskId}，服务 {ServiceCount} 个）",
            hostId,
            name,
            taskId,
            document.Services.Count);

        return new StackTaskAccepted(taskId, StackTaskStatusText.Queued, 1);
    }

    /// <summary>
    /// 更新编排 Stack（重新应用 YAML）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务受理结果。</returns>
    /// <exception cref="BusinessException">
    /// 名称非法（<c>1000</c>）、YAML 非法（<c>1904</c>）、Stack 不存在（<c>1900</c>）、
    /// 非本平台部署（<c>1903</c>）、缺 <c>stack.write</c>（<c>1502</c>）
    /// 或已有进行中的操作（<c>1905</c>）。
    /// </exception>
    public async Task<StackTaskAccepted> UpdateAsync(
        string hostId,
        ApplyStackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireName(request.Name);
        var content = RequireComposeContent(request.ComposeContent);
        var taskId = RequireTaskId(request.TaskId);

        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await this.EnsureWritePermissionAsync(host.Id, cancellationToken).ConfigureAwait(false);

        var document = ComposeParser.Parse(content);

        var existing = await _repository
            .FindByHostAndNameAsync(host.Id, name, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            throw new BusinessException(
                ErrorCodes.StackNotFound,
                $"编排 Stack「{name}」不存在——本平台没有它的 YAML，无法更新");
        }

        await this.EnsureNotBusyAsync(host.Name, name, cancellationToken).ConfigureAwait(false);

        _tasks.TryRegister(taskId, host.Name, name, StackOperations.Update, _timeProvider.GetUtcNow().UtcDateTime);

        existing.ReplaceContent(content);

        try
        {
            await _repository.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 同部署：写库失败要撤销登记，否则 Stack 会被「占住」到 TTL 结束
            _tasks.Abort(taskId);
            throw;
        }

        this.Enqueue(
            taskId,
            host.Name,
            name,
            StackOperations.Update,
            content,
            request.PullAlways ?? false,
            request.PruneOrphans ?? true,
            false);

        _logger.LogInformation(
            "主机 {HostId} 受理编排更新 {StackName}（任务 {TaskId}，服务 {ServiceCount} 个）",
            hostId,
            name,
            taskId,
            document.Services.Count);

        return new StackTaskAccepted(taskId, StackTaskStatusText.Queued, 1);
    }

    /// <summary>
    /// 停止编排 Stack（停掉全部服务，容器保留）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">停止请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务受理结果。</returns>
    public async Task<StackTaskAccepted> StopAsync(
        string hostId,
        StopStackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireName(request.Name);
        var taskId = RequireTaskId(request.TaskId);

        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await this.EnsureWritePermissionAsync(host.Id, cancellationToken).ConfigureAwait(false);

        await this.EnsureNotBusyAsync(host.Name, name, cancellationToken).ConfigureAwait(false);

        _tasks.TryRegister(taskId, host.Name, name, StackOperations.Stop, _timeProvider.GetUtcNow().UtcDateTime);

        this.Enqueue(taskId, host.Name, name, StackOperations.Stop, null, false, false, false);

        return new StackTaskAccepted(taskId, StackTaskStatusText.Queued, 1);
    }

    /// <summary>
    /// 删除编排 Stack（容器与网络一并清掉；卷按需）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务受理结果。</returns>
    public async Task<StackTaskAccepted> RemoveAsync(
        string hostId,
        RemoveStackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireName(request.Name);
        var taskId = RequireTaskId(request.TaskId);

        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await this.EnsureWritePermissionAsync(host.Id, cancellationToken).ConfigureAwait(false);

        await this.EnsureNotBusyAsync(host.Name, name, cancellationToken).ConfigureAwait(false);

        _tasks.TryRegister(taskId, host.Name, name, StackOperations.Remove, _timeProvider.GetUtcNow().UtcDateTime);

        this.Enqueue(
            taskId,
            host.Name,
            name,
            StackOperations.Remove,
            null,
            false,
            false,
            request.RemoveVolumes ?? false);

        return new StackTaskAccepted(taskId, StackTaskStatusText.Queued, 1);
    }

    /// <summary>
    /// 查询任务快照。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（任务标识）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务快照；任务不存在时为 <see langword="null"/>。</returns>
    public async Task<StackTaskSnapshot?> GetTaskAsync(
        string hostId,
        GetStackTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var taskId = request.TaskId?.Trim();

        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw BusinessException.Validation("taskId 不能为空");
        }

        var snapshot = _tasks.Get(taskId);

        if (snapshot is null)
        {
            return null;
        }

        // 任务属于哪台主机要核对：别的机器上的任务不该被这里读到
        if (!string.Equals(snapshot.HostName, hostId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        return snapshot;
    }

    /// <summary>
    /// 发现主机上的 compose 项目（靠标签）。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>项目名 → 项目信息。</returns>
    private static async Task<IReadOnlyDictionary<string, DiscoveredProject>> DiscoverAsync(
        IDockerClient client,
        CancellationToken cancellationToken)
    {
        var containers = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true, Filters = _labelFilter },
                cancellationToken),
            "容器列表",
            cancellationToken).ConfigureAwait(false);

        var projects = new Dictionary<string, DiscoveredProject>(StringComparer.Ordinal);

        foreach (var container in containers ?? [])
        {
            var project = container.Labels is not null
                && container.Labels.TryGetValue(ComposeLabels.Project, out var value)
                    ? value
                    : null;

            if (string.IsNullOrWhiteSpace(project))
            {
                continue;
            }

            // 一次性容器（docker compose run 产生的）不算服务的常态表现
            if (string.Equals(
                    LabelOf(container.Labels, ComposeLabels.OneOff),
                    "True",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!projects.TryGetValue(project, out var existing))
            {
                existing = new DiscoveredProject();
                projects[project] = existing;
            }

            existing.Containers.Add(container);
        }

        var networks = await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.ListNetworksAsync(
                new NetworksListParameters { Filters = _labelFilter },
                cancellationToken),
            "网络列表",
            cancellationToken).ConfigureAwait(false);

        foreach (var network in networks ?? [])
        {
            if (network.Labels is not null
                && network.Labels.TryGetValue(ComposeLabels.Project, out var project)
                && !string.IsNullOrWhiteSpace(project))
            {
                if (!projects.TryGetValue(project, out var existing))
                {
                    existing = new DiscoveredProject();
                    projects[project] = existing;
                }

                existing.Networks++;
            }
        }

        var volumes = await DockerErrorTranslator.ExecuteAsync(
            () => client.Volumes.ListAsync(
                new VolumesListParameters { Filters = _labelFilter },
                cancellationToken),
            "数据卷列表",
            cancellationToken).ConfigureAwait(false);

        foreach (var volume in volumes.Volumes ?? [])
        {
            if (volume.Labels is not null
                && volume.Labels.TryGetValue(ComposeLabels.Project, out var project)
                && !string.IsNullOrWhiteSpace(project))
            {
                if (!projects.TryGetValue(project, out var existing))
                {
                    existing = new DiscoveredProject();
                    projects[project] = existing;
                }

                existing.Volumes++;
            }
        }

        return projects;
    }

    /// <summary>
    /// 把「守护进程上发现的」与「数据库里记录的」合并成列表项。
    /// </summary>
    /// <param name="discovered">发现的项目。</param>
    /// <param name="recorded">数据库里记录的 Stack。</param>
    /// <returns>列表项。</returns>
    private static List<StackSummary> Merge(
        IReadOnlyDictionary<string, DiscoveredProject> discovered,
        IReadOnlyList<Stack> recorded)
    {
        var summaries = new List<StackSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stack in recorded)
        {
            discovered.TryGetValue(stack.Name, out var project);

            summaries.Add(BuildSummary(stack.Name, project, stack));
            seen.Add(stack.Name);
        }

        foreach (var (name, project) in discovered)
        {
            if (seen.Contains(name))
            {
                continue;
            }

            summaries.Add(BuildSummary(name, project, null));
        }

        return [.. summaries.OrderBy(summary => summary.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 构建单个列表项。
    /// </summary>
    /// <param name="name">Stack 名称。</param>
    /// <param name="project">发现到的项目信息（可能为 <see langword="null"/>）。</param>
    /// <param name="stack">数据库记录（可能为 <see langword="null"/>）。</param>
    /// <returns>列表项。</returns>
    private static StackSummary BuildSummary(
        string name,
        DiscoveredProject? project,
        Stack? stack)
    {
        var containers = project?.Containers ?? [];
        var running = containers.Count(container =>
            string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase));

        var services = containers
            .Select(container => LabelOf(container.Labels, ComposeLabels.Service))
            .Where(service => !string.IsNullOrWhiteSpace(service))
            .Distinct(StringComparer.Ordinal)
            .Count();

        var status = ResolveStatus(containers.Count, running);

        return new StackSummary(
            name,
            stack is not null,
            status,
            services,
            containers.Count,
            running,
            project?.Networks ?? 0,
            project?.Volumes ?? 0,
            stack is not null,
            stack?.WebhookEnabled ?? false,
            stack?.WebhookTokenHint,
            stack?.WebhookLastUsedAt,
            stack?.LastModificationTime);
    }

    /// <summary>
    /// 解析整体状态。
    /// </summary>
    /// <param name="containerCount">容器数。</param>
    /// <param name="runningCount">在运行的容器数。</param>
    /// <returns>状态文本。</returns>
    private static string ResolveStatus(int containerCount, int runningCount)
    {
        if (containerCount == 0)
        {
            return "unknown";
        }

        if (runningCount == containerCount)
        {
            return "running";
        }

        return runningCount == 0 ? "stopped" : "partial";
    }

    /// <summary>
    /// 列出编排项目的服务。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="stackName">Stack 名称。</param>
    /// <param name="stack">数据库记录（可能为 <see langword="null"/>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务列表。</returns>
    private static async Task<IReadOnlyList<StackServiceSummary>> ListServicesAsync(
        IDockerClient client,
        string stackName,
        Stack? stack,
        CancellationToken cancellationToken)
    {
        var containers = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true, Filters = _labelFilter },
                cancellationToken),
            "容器列表",
            cancellationToken).ConfigureAwait(false);

        var byService = new Dictionary<string, ContainerListResponse>(StringComparer.Ordinal);

        foreach (var container in containers ?? [])
        {
            if (!string.Equals(
                    LabelOf(container.Labels, ComposeLabels.Project),
                    stackName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var service = LabelOf(container.Labels, ComposeLabels.Service);

            if (string.IsNullOrWhiteSpace(service))
            {
                continue;
            }

            byService[service] = container;
        }

        var services = new List<StackServiceSummary>();

        // YAML 里声明的服务是权威（它决定了镜像与配置）；容器只是「当前跑成了什么样」
        if (stack is not null)
        {
            try
            {
                var document = ComposeParser.Parse(stack.ComposeContent);

                foreach (var service in document.Services)
                {
                    byService.TryGetValue(service.Name, out var container);

                    services.Add(MapService(service, container));
                }
            }
            catch (BusinessException)
            {
                // YAML 解析不了（可能是在平台外被改过）：退回按容器列
            }
        }

        foreach (var (serviceName, container) in byService)
        {
            if (services.Any(service => string.Equals(service.Name, serviceName, StringComparison.Ordinal)))
            {
                continue;
            }

            services.Add(new StackServiceSummary(
                serviceName,
                container.Image,
                container.ID,
                container.Names?.FirstOrDefault(),
                container.State,
                container.Status,
                container.Health?.Status,
                [],
                null));
        }

        return services;
    }

    /// <summary>
    /// 把服务定义与容器映射成服务项。
    /// </summary>
    /// <param name="service">服务定义。</param>
    /// <param name="container">对应容器（可能为 <see langword="null"/>）。</param>
    /// <returns>服务项。</returns>
    private static StackServiceSummary MapService(ComposeService service, ContainerListResponse? container)
    {
        var ports = service.Ports
            .Select(port => new StackServicePort(
                port.ContainerPort,
                port.Protocol,
                port.HostPort,
                port.HostIp))
            .ToList();

        return new StackServiceSummary(
            service.Name,
            service.Image,
            container?.ID,
            container?.Names?.FirstOrDefault(),
            container?.State,
            container?.Status,
            container?.Health?.Status,
            ports,
            service.Restart);
    }

    /// <summary>
    /// 校验当前用户有编排写权限。
    /// </summary>
    /// <param name="hostId">主机主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task EnsureWritePermissionAsync(Guid hostId, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId
            ?? throw BusinessException.Unauthenticated("编排操作需要已登录用户");

        await _permissions
            .EnsurePermissionAsync(userId, hostId, Permission.StackWrite, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 校验该 Stack 没有进行中的操作。
    /// </summary>
    /// <param name="hostName">主机名。</param>
    /// <param name="stackName">Stack 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">已有进行中的操作（<c>1905</c>）。</exception>
    private Task EnsureNotBusyAsync(string hostName, string stackName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackName);

        // 登记时的原子检查已经会挡住「同一 Stack 并发」；这里提前给出更友好的提示
        var busy = _tasks
            .GetSnapshot()
            .Any(snapshot =>
                !IsTerminal(snapshot.Status)
                && string.Equals(snapshot.HostName, hostName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(snapshot.StackName, stackName, StringComparison.Ordinal));

        if (busy)
        {
            throw new BusinessException(
                ErrorCodes.StackOperationInProgress,
                $"编排 Stack「{stackName}」已有进行中的操作，请等它结束");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 把请求入队。
    /// </summary>
    /// <param name="taskId">任务标识。</param>
    /// <param name="hostName">主机名。</param>
    /// <param name="stackName">Stack 名称。</param>
    /// <param name="operation">操作类型。</param>
    /// <param name="content">Compose 文件原文。</param>
    /// <param name="pullAlways">是否总是拉取最新镜像。</param>
    /// <param name="pruneOrphans">是否清理孤儿。</param>
    /// <param name="removeVolumes">是否连卷一起删。</param>
    private void Enqueue(
        string taskId,
        string hostName,
        string stackName,
        string operation,
        string? content,
        bool pullAlways,
        bool pruneOrphans,
        bool removeVolumes) =>
        _queue.TryEnqueue(new StackOperationRequest(
            taskId,
            hostName,
            stackName,
            operation,
            content,
            pullAlways,
            pruneOrphans,
            removeVolumes,
            _currentUser.UserId,
            _currentUser.Username,
            _clientIp.Ip,
            null));

    /// <summary>
    /// 校验 Stack 名称。
    /// </summary>
    /// <param name="name">原始名称。</param>
    /// <returns>校验后的名称。</returns>
    /// <exception cref="BusinessException">名称为空或非法（<c>1000</c>）。</exception>
    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw BusinessException.Validation("Stack 名称不能为空");
        }

        var trimmed = name.Trim();

        // 复用实体的校验规则（Compose 项目名规则），但错误码换成 1000（参数校验）
        try
        {
            return Stack.Create(Guid.NewGuid(), Guid.Empty, trimmed, "x").Name;
        }
        catch (ArgumentException exception)
        {
            throw BusinessException.Validation(exception.Message);
        }
    }

    /// <summary>
    /// 校验 Compose 文件内容。
    /// </summary>
    /// <param name="content">原始内容。</param>
    /// <returns>校验后的内容。</returns>
    /// <exception cref="BusinessException">内容为空（<c>1000</c>）。</exception>
    private static string RequireComposeContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw BusinessException.Validation("Compose 文件内容不能为空");
        }

        return content.Trim();
    }

    /// <summary>
    /// 校验任务标识。
    /// </summary>
    /// <param name="taskId">原始标识。</param>
    /// <returns>校验后的标识。</returns>
    /// <exception cref="BusinessException">标识非法（<c>1000</c>）。</exception>
    private static string RequireTaskId(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw BusinessException.Validation("taskId 不能为空");
        }

        var trimmed = taskId.Trim();

        if (trimmed.Length < 32 || trimmed.Length > 128)
        {
            throw BusinessException.Validation("taskId 至少 32 个字符");
        }

        foreach (var character in trimmed)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character == '-';

            if (!allowed)
            {
                throw BusinessException.Validation("taskId 只能包含字母、数字与短横线");
            }
        }

        return trimmed;
    }

    /// <summary>
    /// 取标签值。
    /// </summary>
    /// <param name="labels">标签字典（可能为 <see langword="null"/>）。</param>
    /// <param name="key">标签名。</param>
    /// <returns>标签值；不存在时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>不用 <c>GetValueOrDefault</c></b>：它只在 <see cref="IReadOnlyDictionary{TKey, TValue}"/>
    /// 上有重载，而 Docker SDK 的 <c>Labels</c> 声明为 <see cref="IDictionary{TKey, TValue}"/>。
    /// </remarks>
    private static string? LabelOf(IDictionary<string, string>? labels, string key)
    {
        if (labels is null)
        {
            return null;
        }

        return labels.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// 关键字匹配。
    /// </summary>
    /// <param name="haystack">被检查的文本。</param>
    /// <param name="needle">关键字。</param>
    /// <returns>命中返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>没有关键字时一律命中</b>：写成「关键字为空 → 不匹配」会让空搜索把整个列表清空，
    /// 而症状是「接口返回成功、列表却是空的」——极难联想到是筛选条件写反了。
    /// </remarks>
    private static bool MatchesSearch(string? haystack, string? needle) =>
        string.IsNullOrWhiteSpace(needle)
        || haystack?.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 状态匹配。
    /// </summary>
    /// <param name="status">实际状态。</param>
    /// <param name="filter">筛选值；为空表示不过滤。</param>
    /// <returns>命中返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>筛选值无效时不过滤</b>（而不是返回空列表）：
    /// 界面上的下拉值来自后端定义的封闭集合，
    /// 而手写请求可能带上别的值——那时报「一个都没有」比报「全部」更让人困惑。
    /// </remarks>
    private static bool MatchesStatus(string status, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return string.Equals(status, filter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断任务状态是否已结束。
    /// </summary>
    /// <param name="status">状态文本。</param>
    /// <returns>已结束返回 <see langword="true"/>。</returns>
    private static bool IsTerminal(string status) =>
        status is StackTaskStatusText.Succeeded or StackTaskStatusText.Failed;

    /// <summary>
    /// 守护进程上发现的一个 compose 项目。
    /// </summary>
    internal sealed class DiscoveredProject
    {
        /// <summary>项目下的容器。</summary>
        internal List<ContainerListResponse> Containers { get; } = [];

        /// <summary>项目下的网络数。</summary>
        internal int Networks { get; set; }

        /// <summary>项目下的具名卷数。</summary>
        internal int Volumes { get; set; }
    }
}
