using System.Globalization;
using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Realtime;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Whalefleet.Application.Images;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排操作执行器：把「要做的事」真正落到 Docker 上。
/// </summary>
/// <remarks>
/// <para><b>为什么是后台执行而不是在 HTTP 请求里做</b>：部署可能要拉镜像（几分钟），
/// 而 HTTP 请求扛不住——中间的反代会把连接掐掉，
/// 客户端拿到的是「504 网关超时」而不是「部署失败」。
/// 改成「立刻返回任务标识 + 后台执行 + 实时进度」之后，长耗时不再需要特殊处理。</para>
/// <para><b>标签是唯一的联结</b>：本执行器不维护「哪个容器属于哪个 Stack」的表——
/// 每次都靠 <c>com.docker.compose.project</c> 标签向守护进程问。
/// 这样用户在命令行删掉一个容器后，下次「应用」会把它重新建出来，
/// 而不会出现「库里说它存在、实际上不存在」的错位。</para>
/// <para><b>回滚只回滚本次新建的东西</b>：失败时删掉本次创建的容器与网络，
/// 但<b>绝不删卷</b>——卷里是用户的数据，删它的后果不可逆，
/// 而「多出一个没被用上的卷」可以事后清理。</para>
/// <para><b>日志纪律</b>：只记资源名与条数，不记环境变量的值、不记 YAML 全文
/// （里面常含口令）。</para>
/// </remarks>
public sealed class StackOperationExecutor
{
    /// <summary>标签过滤：只认 label 名，不带值。</summary>
    /// <remarks>
    /// Docker 的 label 过滤语义是「键存在即为真」——
    /// 写成 <c>com.docker.compose.project=demo</c> 才会按值过滤，
    /// 而我们要的是「这个项目下的所有容器」。
    /// </remarks>
    private static readonly IDictionary<string, IDictionary<string, bool>> _labelFilter =
        new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [ComposeLabels.Project] = true,
            },
        };

    private readonly IDockerHostAccessor _accessor;
    private readonly ImageService _images;
    private readonly IOperationProgressPublisher _progress;
    private readonly ILogger<StackOperationExecutor> _logger;

    /// <summary>
    /// 初始化执行器。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="images">镜像用例（复用它的拉取逻辑与进度上报）。</param>
    /// <param name="progress">进度发布器。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public StackOperationExecutor(
        IDockerHostAccessor accessor,
        ImageService images,
        IOperationProgressPublisher progress,
        ILogger<StackOperationExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _images = images;
        _progress = progress;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次编排操作。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果摘要。</returns>
    /// <exception cref="BusinessException">操作失败。</exception>
    public async Task<StackOperationResult> ExecuteAsync(
        StackOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Operation switch
        {
            StackOperations.Stop => await this.StopAsync(request, cancellationToken).ConfigureAwait(false),
            StackOperations.Remove => await this.RemoveAsync(request, cancellationToken).ConfigureAwait(false),
            _ => await this.ApplyAsync(request, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// 部署 / 更新：建资源、起容器、清理孤儿。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果摘要。</returns>
    private async Task<StackOperationResult> ApplyAsync(
        StackOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ComposeContent))
        {
            throw new BusinessException(ErrorCodes.ComposeInvalid, "缺少 Compose 文件内容");
        }

        var client = await _accessor.GetClientAsync(request.HostName, cancellationToken).ConfigureAwait(false);

        var document = ComposeParser.Parse(request.ComposeContent);
        var plan = ComposePlanner.Plan(request.StackName, document);

        var created = 0;
        var recreated = 0;
        var removed = 0;
        var skippedOrphans = 0;
        var started = 0;
        var stopped = 0;
        var networksCreated = 0;
        var volumesCreated = 0;

        var createdContainerIds = new List<string>();
        var createdNetworkIds = new List<string>();

        try
        {
            await this.PublishAsync(request, null, "准备网络", cancellationToken).ConfigureAwait(false);

            foreach (var network in plan.Networks)
            {
                if (network.External)
                {
                    continue;
                }

                if (await EnsureNetworkAsync(client, request.StackName, network.ActualName, cancellationToken).ConfigureAwait(false))
                {
                    networksCreated++;
                    createdNetworkIds.Add(network.ActualName);
                }
            }

            await this.PublishAsync(request, null, "准备数据卷", cancellationToken).ConfigureAwait(false);

            foreach (var volume in plan.Volumes)
            {
                if (volume.External)
                {
                    continue;
                }

                if (await EnsureVolumeAsync(client, request.StackName, volume.ActualName, cancellationToken).ConfigureAwait(false))
                {
                    volumesCreated++;
                }
            }

            var existing = await ListProjectContainersAsync(client, request.StackName, cancellationToken)
                .ConfigureAwait(false);

            var total = Math.Max(plan.Services.Count, 1);
            var index = 0;

            foreach (var serviceName in plan.StartOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var servicePlan = plan.Services.First(plan => plan.Service.Name == serviceName);
                index++;

                await this.PublishAsync(
                    request,
                    (double)index / total * 100,
                    $"服务 {serviceName}",
                    cancellationToken).ConfigureAwait(false);

                var serviceResult = await this.ApplyServiceAsync(
                    client,
                    request,
                    plan,
                    servicePlan,
                    existing,
                    createdContainerIds,
                    cancellationToken).ConfigureAwait(false);

                created += serviceResult.Created;
                recreated += serviceResult.Recreated;
                started += serviceResult.Started;
            }

            await this.PublishAsync(request, null, "清理多余资源", cancellationToken).ConfigureAwait(false);

            var orphanResult = await this.PruneOrphansAsync(
                client,
                request,
                plan,
                existing,
                cancellationToken).ConfigureAwait(false);

            removed += orphanResult.Removed;
            skippedOrphans += orphanResult.Skipped;

            _logger.LogInformation(
                "主机 {HostId} 应用编排 {Stack}：新建 {Created} 个服务、重建 {Recreated} 个、清理 {Removed} 个孤儿",
                request.HostName,
                request.StackName,
                created,
                recreated,
                removed);

            return new StackOperationResult(
                created,
                recreated,
                removed,
                skippedOrphans,
                started,
                stopped,
                networksCreated,
                volumesCreated,
                0);
        }
        catch
        {
            await this.RollbackAsync(client, createdContainerIds, createdNetworkIds, cancellationToken)
                .ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// 应用单个服务（必要时重建）。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="request">操作请求。</param>
    /// <param name="plan">编排计划。</param>
    /// <param name="servicePlan">服务计划。</param>
    /// <param name="existing">项目下已有的容器。</param>
    /// <param name="createdContainerIds">本次新建的容器 ID（收集起来供回滚用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次服务的结果增量。</returns>
    /// <remarks>
    /// <b>「要不要重建」只看配置哈希</b>：与 Compose 同一套语义。
    /// 哈希相同就保留现有容器（只保证它在跑），不同才重建——
    /// 否则每次「应用」都会把全部容器换一遍，用户会看到毫无变化的服务被重启。
    /// </remarks>
    private async Task<(int Created, int Recreated, int Started)> ApplyServiceAsync(
        IDockerClient client,
        StackOperationRequest request,
        ComposePlan plan,
        ComposeServicePlan servicePlan,
        IReadOnlyList<ContainerListResponse> existing,
        List<string> createdContainerIds,
        CancellationToken cancellationToken)
    {
        await this.EnsureImageAsync(client, request, servicePlan.Service.Image, cancellationToken)
            .ConfigureAwait(false);

        var current = existing.FirstOrDefault(container =>
            string.Equals(
                LabelOf(container, ComposeLabels.Service),
                servicePlan.Service.Name,
                StringComparison.Ordinal));

        var hash = servicePlan.ConfigHash;
        var unchanged = current is not null
            && string.Equals(
                LabelOf(current, ComposeLabels.ConfigHash),
                hash,
                StringComparison.Ordinal);

        if (unchanged && current is not null)
        {
            // 配置没变就保留容器，只保证它在运行
            if (!IsRunning(current))
            {
                await DockerErrorTranslator.ExecuteAsync(
                    () => client.Containers.StartContainerAsync(
                        current.ID,
                        new ContainerStartParameters(),
                        cancellationToken),
                    $"容器 {current.Names?.FirstOrDefault() ?? current.ID}",
                    cancellationToken).ConfigureAwait(false);

                return (0, 0, 1);
            }

            return (0, 0, 0);
        }

        // 配置变了（或没有旧容器）：先删掉旧容器再建新的
        if (current is not null)
        {
            await this.RemoveContainerAsync(client, current.ID, cancellationToken).ConfigureAwait(false);
        }

        var parameters = BuildCreateParameters(request, plan, servicePlan);
        var created = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.CreateContainerAsync(parameters, cancellationToken),
            $"服务 {servicePlan.Service.Name}",
            cancellationToken).ConfigureAwait(false);

        createdContainerIds.Add(created.ID);

        foreach (var network in servicePlan.Networks)
        {
            var actualName = plan.Networks
                .FirstOrDefault(candidate => string.Equals(candidate.Name, network, StringComparison.Ordinal))
                ?.ActualName;

            if (actualName is null)
            {
                continue;
            }

            await DockerErrorTranslator.ExecuteAsync(
                () => client.Networks.ConnectNetworkAsync(
                    actualName,
                    new NetworkConnectParameters { Container = created.ID },
                    cancellationToken),
                $"服务 {servicePlan.Service.Name} 接入网络 {actualName}",
                cancellationToken).ConfigureAwait(false);
        }

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken),
            $"服务 {servicePlan.Service.Name}",
            cancellationToken).ConfigureAwait(false);

        return (current is null ? 1 : 0, current is null ? 0 : 1, 1);
    }

    /// <summary>
    /// 清理「已不在 YAML 里的」孤儿服务。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="request">操作请求。</param>
    /// <param name="plan">编排计划。</param>
    /// <param name="existing">项目下已有的容器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除数与保留数。</returns>
    private async Task<(int Removed, int Skipped)> PruneOrphansAsync(
        IDockerClient client,
        StackOperationRequest request,
        ComposePlan plan,
        IReadOnlyList<ContainerListResponse> existing,
        CancellationToken cancellationToken)
    {
        var declared = plan.Services.Select(static plan => plan.Service.Name).ToHashSet(StringComparer.Ordinal);
        var orphans = existing
            .Where(container => !declared.Contains(
                LabelOf(container, ComposeLabels.Service) ?? string.Empty))
            .ToList();

        if (orphans.Count == 0)
        {
            return (0, 0);
        }

        if (!request.PruneOrphans)
        {
            return (0, orphans.Count);
        }

        var removed = 0;

        foreach (var orphan in orphans)
        {
            await this.RemoveContainerAsync(client, orphan.ID, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        return (removed, 0);
    }

    /// <summary>
    /// 停止：把项目下所有容器停掉（容器保留）。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果摘要。</returns>
    private async Task<StackOperationResult> StopAsync(
        StackOperationRequest request,
        CancellationToken cancellationToken)
    {
        var client = await _accessor.GetClientAsync(request.HostName, cancellationToken).ConfigureAwait(false);
        var containers = await ListProjectContainersAsync(client, request.StackName, cancellationToken)
            .ConfigureAwait(false);

        var stopped = 0;

        foreach (var container in containers)
        {
            if (!IsRunning(container))
            {
                continue;
            }

            await DockerErrorTranslator.ExecuteAsync(
                () => client.Containers.StopContainerAsync(
                    container.ID,
                    new ContainerStopParameters(),
                    cancellationToken),
                $"容器 {container.Names?.FirstOrDefault() ?? container.ID}",
                cancellationToken).ConfigureAwait(false);

            stopped++;
        }

        _logger.LogInformation("主机 {HostId} 停止编排 {Stack}：停了 {Stopped} 个容器", request.HostName, request.StackName, stopped);

        return new StackOperationResult(0, 0, 0, 0, 0, stopped, 0, 0, 0);
    }

    /// <summary>
    /// 删除：清掉项目下的容器、网络（卷按需）。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果摘要。</returns>
    private async Task<StackOperationResult> RemoveAsync(
        StackOperationRequest request,
        CancellationToken cancellationToken)
    {
        var client = await _accessor.GetClientAsync(request.HostName, cancellationToken).ConfigureAwait(false);
        var containers = await ListProjectContainersAsync(client, request.StackName, cancellationToken)
            .ConfigureAwait(false);

        var stopped = 0;

        foreach (var container in containers)
        {
            if (IsRunning(container))
            {
                await DockerErrorTranslator.ExecuteAsync(
                    () => client.Containers.StopContainerAsync(
                        container.ID,
                        new ContainerStopParameters(),
                        cancellationToken),
                    $"容器 {container.Names?.FirstOrDefault() ?? container.ID}",
                    cancellationToken).ConfigureAwait(false);

                stopped++;
            }

            await this.RemoveContainerAsync(client, container.ID, cancellationToken).ConfigureAwait(false);
        }

        var networks = await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.ListNetworksAsync(
                new NetworksListParameters { Filters = _labelFilter },
                cancellationToken),
            "网络列表",
            cancellationToken).ConfigureAwait(false);

        var removedNetworks = 0;

        foreach (var network in networks ?? [])
        {
            if (!string.Equals(
                    LabelOf(network, ComposeLabels.Project),
                    request.StackName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                await DockerErrorTranslator.ExecuteAsync(
                    () => client.Networks.DeleteNetworkAsync(network.ID, cancellationToken),
                    $"网络 {network.Name}",
                    cancellationToken).ConfigureAwait(false);

                removedNetworks++;
            }
            catch (BusinessException exception)
            {
                // 网络上还有端点（多半是本项目之外的容器接入了）：留着，不当失败
                _logger.LogWarning(
                    "主机 {HostId} 删除编排 {Stack} 时网络 {Network} 未被删除：{Reason}",
                    request.HostName,
                    request.StackName,
                    network.Name,
                    exception.Message);
            }
        }

        var removedVolumes = 0;

        if (request.RemoveVolumes)
        {
            var volumes = await DockerErrorTranslator.ExecuteAsync(
                () => client.Volumes.ListAsync(
                    new VolumesListParameters { Filters = _labelFilter },
                    cancellationToken),
                "数据卷列表",
                cancellationToken).ConfigureAwait(false);

            foreach (var volume in volumes.Volumes ?? [])
            {
                if (!string.Equals(
                        LabelOf(volume, ComposeLabels.Project),
                        request.StackName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    await DockerErrorTranslator.ExecuteAsync(
                        () => client.Volumes.RemoveAsync(volume.Name, true, cancellationToken),
                        $"数据卷 {volume.Name}",
                        cancellationToken).ConfigureAwait(false);

                    removedVolumes++;
                }
                catch (BusinessException exception)
                {
                    _logger.LogWarning(
                        "主机 {HostId} 删除编排 {Stack} 时数据卷 {Volume} 未被删除：{Reason}",
                        request.HostName,
                        request.StackName,
                        volume.Name,
                        exception.Message);
                }
            }
        }

        _logger.LogInformation(
            "主机 {HostId} 删除编排 {Stack}：移除 {Containers} 个容器、{Networks} 个网络、{Volumes} 个卷",
            request.HostName,
            request.StackName,
            containers.Count,
            removedNetworks,
            removedVolumes);

        return new StackOperationResult(
            0,
            0,
            containers.Count,
            0,
            0,
            stopped,
            0,
            0,
            removedVolumes);
    }

    /// <summary>
    /// 确保镜像在本地存在（必要时拉取）。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="request">操作请求。</param>
    /// <param name="image">镜像引用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>「拉取最新」失败即整任务失败</b>：用户勾了「拉取最新」，
    /// 而我们悄悄回退到本地旧镜像并报告成功——那是最糟的组合：
    /// 用户以为更新了，实际跑的还是旧版本。
    /// </remarks>
    private async Task EnsureImageAsync(
        IDockerClient client,
        StackOperationRequest request,
        string image,
        CancellationToken cancellationToken)
    {
        var exists = await ImageExistsAsync(client, image, cancellationToken).ConfigureAwait(false);

        if (exists && !request.PullAlways)
        {
            return;
        }

        var (repository, tag) = ImageReference.Split(image);

        _logger.LogInformation(
            "主机 {HostId} 编排 {Stack} 拉取镜像 {Image}（策略={Policy}）",
            request.HostName,
            request.StackName,
            image,
            request.PullAlways ? "always" : "missing");

        await _images.PullAsync(
            request.HostName,
            new PullImageRequest { Image = repository, Tag = tag, ProgressToken = request.TaskId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 判断镜像是否在本地存在。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="image">镜像引用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在返回 <see langword="true"/>。</returns>
    private static async Task<bool> ImageExistsAsync(
        IDockerClient client,
        string image,
        CancellationToken cancellationToken)    {
        try
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Images.InspectImageAsync(image, cancellationToken),
                $"镜像 {image}",
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (BusinessException exception) when (exception.Code == ErrorCodes.DockerResourceNotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// 确保网络存在。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="projectName">项目名（写进标签）。</param>
    /// <param name="name">网络名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次是否新建了网络。</returns>
    /// <remarks>
    /// <b>标签值是项目名而不是网络名</b>：网络名本身已经带了项目名前缀
    /// （<c>&lt;项目名&gt;_&lt;网络名&gt;</c>），而标签是「这个资源属于哪个项目」的标记——
    /// 两者混用会让「按项目列网络」失效。
    /// </remarks>
    private static async Task<bool> EnsureNetworkAsync(
        IDockerClient client,
        string projectName,
        string name,
        CancellationToken cancellationToken)
    {
        var networks = await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.ListNetworksAsync(
                new NetworksListParameters { Filters = _labelFilter },
                cancellationToken),
            "网络列表",
            cancellationToken).ConfigureAwait(false);

        var exists = (networks ?? []).Any(network =>
            string.Equals(network.Name, name, StringComparison.Ordinal));

        if (exists)
        {
            return false;
        }

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Networks.CreateNetworkAsync(
                new NetworksCreateParameters
                {
                    Name = name,
                    Driver = string.Empty,
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ComposeLabels.Project] = projectName,
                    },
                },
                cancellationToken),
            $"网络 {name}",
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// 确保具名卷存在。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="projectName">项目名（写进标签）。</param>
    /// <param name="name">卷名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次是否新建了卷。</returns>
    private static async Task<bool> EnsureVolumeAsync(
        IDockerClient client,
        string projectName,
        string name,
        CancellationToken cancellationToken)
    {
        var volumes = await DockerErrorTranslator.ExecuteAsync(
            () => client.Volumes.ListAsync(
                new VolumesListParameters { Filters = _labelFilter },
                cancellationToken),
            "数据卷列表",
            cancellationToken).ConfigureAwait(false);

        var exists = (volumes.Volumes ?? []).Any(volume =>
            string.Equals(volume.Name, name, StringComparison.Ordinal));

        if (exists)
        {
            return false;
        }

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Volumes.CreateAsync(
                new VolumesCreateParameters
                {
                    Name = name,
                    Driver = string.Empty,
                    Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ComposeLabels.Project] = projectName,
                    },
                },
                cancellationToken),
            $"数据卷 {name}",
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// 列出项目下的全部容器（含已停止的）。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="stackName">Stack 名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>容器列表。</returns>
    private static async Task<IReadOnlyList<ContainerListResponse>> ListProjectContainersAsync(
        IDockerClient client,
        string stackName,
        CancellationToken cancellationToken)
    {
        var containers = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true, Filters = _labelFilter },
                cancellationToken),
            "容器列表",
            cancellationToken).ConfigureAwait(false);

        return (containers ?? [])
            .Where(container => string.Equals(
                LabelOf(container, ComposeLabels.Project),
                stackName,
                StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// 构建容器的创建参数。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <param name="plan">编排计划。</param>
    /// <param name="servicePlan">服务计划。</param>
    /// <returns>创建参数。</returns>
    private static CreateContainerParameters BuildCreateParameters(
        StackOperationRequest request,
        ComposePlan plan,
        ComposeServicePlan servicePlan)
    {
        var service = servicePlan.Service;

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ComposeLabels.Project] = request.StackName,
            [ComposeLabels.Service] = service.Name,
            [ComposeLabels.ContainerNumber] = "1",
            [ComposeLabels.ConfigHash] = servicePlan.ConfigHash,
            [ComposeLabels.OneOff] = "False",
        };

        var exposedPorts = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
        var portBindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal);
        var binds = new List<string>();

        foreach (var port in service.Ports)
        {
            var key = $"{port.ContainerPort.ToString(CultureInfo.InvariantCulture)}/{port.Protocol}";
            exposedPorts[key] = default;

            if (port.HostPort is not { } hostPort)
            {
                continue;
            }

            portBindings[key] =
            [
                new PortBinding
                {
                    HostPort = hostPort.ToString(CultureInfo.InvariantCulture),
                    HostIP = port.HostIp ?? string.Empty,
                },
            ];
        }

        foreach (var volume in servicePlan.Volumes)
        {
            binds.Add($"{volume.ActualName}:{volume.Destination}{(volume.ReadOnly ? ":ro" : string.Empty)}");
        }

        foreach (var bind in servicePlan.Binds)
        {
            binds.Add($"{bind.Source}:{bind.Destination}{(bind.ReadOnly ? ":ro" : string.Empty)}");
        }

        // 空集合与 null 对守护进程不等价：传 [] 会被当成「显式清空」（如「不带任何启动命令」），
        // 因此这里只传 null，只用 pragma 关掉与 SDK 标注的不一致（同 ContainerService 的做法）。
#pragma warning disable CS8601
        var parameters = new CreateContainerParameters
        {
            Name = servicePlan.ContainerName,
            Image = service.Image,
            Labels = labels,
            Env = service.Environment.Count == 0 ? null : [.. service.Environment],
            Cmd = service.Command.Count == 0 ? null : [.. service.Command],
            ExposedPorts = exposedPorts.Count == 0 ? null : exposedPorts,
            HostConfig = new HostConfig
            {
                Binds = binds.Count == 0 ? null : binds,
                PortBindings = portBindings.Count == 0 ? null : portBindings,
                RestartPolicy = BuildRestartPolicy(service.Restart),
            },
        };
#pragma warning restore CS8601

        if (service.HealthCheck is { } healthCheck)
        {
            parameters.Healthcheck = new HealthcheckConfig
            {
                Test = [.. healthCheck.Test],
                Interval = ToDuration(healthCheck.IntervalSeconds),
                Timeout = ToDuration(healthCheck.TimeoutSeconds),
                StartPeriod = ToDuration(healthCheck.StartPeriodSeconds),
                Retries = healthCheck.Retries,
            };
        }

        return parameters;
    }

    /// <summary>
    /// 构建重启策略。
    /// </summary>
    /// <param name="policy">策略名；<see langword="null"/> 时用默认（不自动重启）。</param>
    /// <returns>重启策略。</returns>
    private static RestartPolicy BuildRestartPolicy(string? policy) => policy switch
    {
        "always" => new RestartPolicy { Name = RestartPolicyKind.Always },
        "on-failure" => new RestartPolicy { Name = RestartPolicyKind.OnFailure },
        "unless-stopped" => new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
        _ => new RestartPolicy { Name = RestartPolicyKind.No },
    };

    /// <summary>
    /// 读容器上的标签值。
    /// </summary>
    /// <param name="container">容器。</param>
    /// <param name="key">标签名。</param>
    /// <returns>标签值；没有该标签时为 <see langword="null"/>。</returns>
    private static string? LabelOf(ContainerListResponse container, string key) =>
        container.Labels is not null && container.Labels.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 读网络上的标签值。
    /// </summary>
    /// <param name="network">网络。</param>
    /// <param name="key">标签名。</param>
    /// <returns>标签值；没有该标签时为 <see langword="null"/>。</returns>
    private static string? LabelOf(NetworkResponse network, string key) =>
        network.Labels is not null && network.Labels.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 读卷上的标签值。
    /// </summary>
    /// <param name="volume">卷。</param>
    /// <param name="key">标签名。</param>
    /// <returns>标签值；没有该标签时为 <see langword="null"/>。</returns>
    private static string? LabelOf(VolumeResponse volume, string key) =>
        volume.Labels is not null && volume.Labels.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 秒 → <see cref="TimeSpan"/>。
    /// </summary>
    /// <param name="seconds">秒；<see langword="null"/> 时返回 <see langword="null"/>。</param>
    /// <returns>时长。</returns>
    private static TimeSpan? ToDuration(int? seconds) =>
        seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);

    /// <summary>
    /// 判断容器是否在运行。
    /// </summary>
    /// <param name="container">容器。</param>
    /// <returns>在运行返回 <see langword="true"/>。</returns>
    private static bool IsRunning(ContainerListResponse container) =>
        string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 删除容器（先停后删）。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="containerId">容器 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RemoveContainerAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken)
    {
        try
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Containers.RemoveContainerAsync(
                    containerId,
                    new ContainerRemoveParameters { Force = true },
                    cancellationToken),
                $"容器 {containerId}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessException exception)
        {
            _logger.LogWarning("删除容器 {ContainerId} 失败：{Reason}", containerId, exception.Message);
        }
    }

    /// <summary>
    /// 回滚：删掉本次新建的容器与网络（**不动卷**）。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="containerIds">本次新建的容器 ID。</param>
    /// <param name="networkNames">本次新建的网络名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <b>回滚失败不掩盖原始异常</b>：这里吞掉自己的异常，
    /// 让调用方看到真正失败原因——那才是有用的信息。
    /// </remarks>
    private async Task RollbackAsync(
        IDockerClient client,
        IReadOnlyList<string> containerIds,
        IReadOnlyList<string> networkNames,
        CancellationToken cancellationToken)
    {
        foreach (var containerId in containerIds)
        {
            try
            {
                await client.Containers.RemoveContainerAsync(
                    containerId,
                    new ContainerRemoveParameters { Force = true },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning("回滚时删除容器 {ContainerId} 失败：{Reason}", containerId, exception.Message);
            }
        }

        foreach (var networkName in networkNames)
        {
            try
            {
                await client.Networks.DeleteNetworkAsync(networkName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning("回滚时删除网络 {Network} 失败：{Reason}", networkName, exception.Message);
            }
        }
    }

    /// <summary>
    /// 推送一次进度。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <param name="percent">总体进度百分比。</param>
    /// <param name="step">当前步骤。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PublishAsync(
        StackOperationRequest request,
        double? percent,
        string step,
        CancellationToken cancellationToken)
    {
        await _progress.PublishAsync(
            new OperationProgress(
                request.TaskId,
                request.Operation,
                percent,
                step,
                false,
                null),
            cancellationToken).ConfigureAwait(false);
    }
}
