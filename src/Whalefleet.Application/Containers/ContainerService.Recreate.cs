using System.Globalization;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Microsoft.Extensions.Logging;
using SdkState = Docker.DotNet.Models.State;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器基础操作：重命名、热更新、复制、追加挂载（0.3.0）。
/// </summary>
/// <remarks>
/// <para><b>为什么拆成 partial</b>：本类同时承担「查询」（列表 / 详情 / 日志）与「变更」
/// （启停 / 删除 / 重建），后者在 0.3.0 之后又长出重建与回滚两段流程。
/// 放在一起会让单文件超过两千行，而每次只想读「重建那套流程」的人都要翻过整个查询部分。</para>
/// <para><b>主机校验与失败路径同 <see cref="ContainerService"/> 的其余部分</b>：
/// 主机存在（<c>1300</c>）与可见性（<c>1501</c>）在管道里完成，
/// 这里只负责「拿客户端 → 调 Docker → 翻译异常」。</para>
/// <para><b>日志纪律</b>：只记主机标识、容器标识与名称，<b>不</b>记环境变量与命令内容
/// （两者常含密码 / Token）。</para>
/// </remarks>
public sealed partial class ContainerService
{
    /// <summary>复制时追加到原名后的后缀。</summary>
    private const string CopySuffix = "-copy";

    /// <summary>追加挂载时给旧容器用的让位名前缀。</summary>
    /// <remarks>前缀本身带语义：列表上看到一个 <c>whalefleet-replaced-…</c> 的容器，
    /// 用户能猜到它是某次重建的产物，而不是自己起的名字。</remarks>
    private const string ReplacedPrefix = "whalefleet-replaced-";

    /// <summary>让位名重试次数上限。</summary>
    private const int MaxNameRetries = 10;

    /// <summary>
    /// 交换空间相对内存的倍数（2）。
    /// </summary>
    /// <remarks>
    /// 与 <c>docker run --memory</c> / <c>docker update --memory</c> 的默认行为一致：
    /// 只给 <c>--memory</c> 时 Docker 会自动把 swap 定成它的 2 倍。
    /// <b>不能只发 Memory 不发 MemorySwap</b>：守护进程会拿容器原有的 swap 校验并拒绝
    /// （详见 <c>UpdateAsync</c> 里的注释）。
    /// </remarks>
    private const long DefaultSwapRatio = 2;

    // ==================== 重命名 ====================

    /// <summary>
    /// 重命名容器。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">重命名请求（容器标识、新名称）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>容器 ID 与新名称。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识或新名称为空 / 名称非法（<c>1000</c>）、容器或新名称已被占用（<c>1401</c> / <c>1404</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>为什么名称非法要本地判</b>：守护进程对非法名称回 400 且不说明是哪个字符不合法，
    /// 而「改一个字符」正是用户改完就能成功的操作——本地挡下才能给出可操作的提示。
    /// </remarks>
    public async Task<ContainerRenamedResult> RenameAsync(
        string hostId,
        RenameContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var newName = RequireContainerName(request.Name)
            ?? throw BusinessException.Validation("name 不能为空");

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.RenameContainerAsync(
                containerId,
                new ContainerRenameParameters { NewName = newName },
                cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 重命名容器 {ContainerId} 为 {NewName}", hostId, containerId, newName);

        return new ContainerRenamedResult(containerId, newName);
    }

    // ==================== 热更新 ====================

    /// <summary>
    /// 热更新容器的重启策略与资源限制。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">更新请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns><b>更新后</b>实际生效的值。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识为空、字段非法（<c>1000</c>）、容器不存在（<c>1401</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>为什么先 inspect 再 update</b>：Docker 的 update 接口在「未传的字段」上
    /// 行为随 API 版本变化——老版本会把未传的数值字段一并写回 0（等于取消限制）。
    /// 先取当前值再按「请求覆盖当前」的规则组装，就不会出现「只改了重启策略、内存上限被清掉」
    /// 这种用户没要求却发生了的状态丢失。</para>
    /// <para><b>为什么回带结果而不是只返回 200</b>：同上——Docker 的 update 响应体
    /// 在不同版本里可能为空，而调用方需要知道「改完之后到底是什么值」才能刷新界面。</para>
    /// </remarks>
    public async Task<ContainerUpdatedResult> UpdateAsync(
        string hostId,
        UpdateContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);

        // 先校验请求再取客户端：纯本地的格式错误不该触发一次 Docker 往返
        RestartPolicyKind? restartPolicy = request.RestartPolicy is null
            ? null
            : BuildRestartPolicy(request.RestartPolicy).Name;
        var memory = AdvancedCreateOptions.RequireUpdatableMemoryLimit(request.MemoryLimitBytes);
        var nanoCpus = AdvancedCreateOptions.RequireUpdatableNanoCpus(request.NanoCpus);

        if (restartPolicy is null && memory is null && nanoCpus is null)
        {
            throw BusinessException.Validation(
                "restartPolicy / memoryLimitBytes / nanoCpus 至少要给一个（全省略等同于什么都不改）");
        }

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var current = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.InspectContainerAsync(containerId, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        // 「请求给了就用请求的，没给就保持当前值」——这条规则必须在本地拼出来，
        // 不能指望守护进程替我们保持。
        //
        // ⚠️ **MemorySwap 必须与 Memory 一起下发**（实测 Docker 29.x）：
        // 只发 Memory 时守护进程会拿容器**原有**的 MemorySwap 来校验，报
        // 「Memory limit should be smaller than already set memoryswap limit,
        //   update the memoryswap at the same time」。
        // 原值是「不限」时任何新内存上限都通不过这个校验——症状是「第一次改内存上限就失败」，
        // 而报错完全看不出是缺了 swap。
        var effectiveMemory = memory ?? current.HostConfig?.Memory ?? 0;
        var effectiveSwap = memory is null
            ? current.HostConfig?.MemorySwap ?? 0
            : memory > 0
                ? memory.Value * DefaultSwapRatio
                : 0;

        var parameters = new ContainerUpdateParameters
        {
            RestartPolicy = new RestartPolicy
            {
                Name = restartPolicy ?? current.HostConfig?.RestartPolicy?.Name ?? RestartPolicyKind.No,
                MaximumRetryCount = current.HostConfig?.RestartPolicy?.MaximumRetryCount ?? 0,
            },
            Memory = effectiveMemory,
            MemorySwap = effectiveSwap,
            NanoCPUs = nanoCpus ?? current.HostConfig?.NanoCPUs ?? 0,
        };

        // on-failure 的重试次数不在本端点的开放范围内：它属于 HostConfig.RestartPolicy，
        // 而热更新接口对它的处理各版本不一致。保持当前值，不给用户「改了却不生效」的错觉。
        await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.UpdateContainerAsync(containerId, parameters, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        var updated = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.InspectContainerAsync(containerId, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 更新容器 {ContainerId}：重启策略={Policy}，内存上限={Memory} 字节，CPU 配额={NanoCpus}",
            hostId,
            containerId,
            MapRestartPolicy(updated.HostConfig?.RestartPolicy?.Name),
            updated.HostConfig?.Memory.ToString(CultureInfo.InvariantCulture) ?? "未设置",
            updated.HostConfig?.NanoCPUs.ToString(CultureInfo.InvariantCulture) ?? "未设置");

        return new ContainerUpdatedResult(
            containerId,
            MapRestartPolicy(updated.HostConfig?.RestartPolicy?.Name),
            updated.HostConfig is null || updated.HostConfig.Memory == 0 ? null : updated.HostConfig.Memory,
            updated.HostConfig is null || updated.HostConfig.NanoCPUs == 0 ? null : updated.HostConfig.NanoCPUs);
    }

    // ==================== 复制 ====================

    /// <summary>
    /// 复制容器：按原容器的配置新建一个兄弟容器（原容器不受影响）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">复制请求（源容器、新名称、是否启动）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新容器的 ID、名称与提示。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识为空或名称非法（<c>1000</c>）、源容器不存在（<c>1401</c>）、
    /// 目标名称已被占用且无法自动避让（<c>1404</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>为什么不能原地克隆</b>：Docker 没有 clone 接口。能做到「配置一致」的只有
    /// 「inspect 出来 → 用同一份配置 create」这一条路。</para>
    /// <para><b>为什么默认加 <c>-copy</c> 而不是要求用户填名字</b>：
    /// 同一守护进程上不能有两个同名容器，用户若不填名字就只能先看到失败再改一次。</para>
    /// </remarks>
    public async Task<ContainerRecreatedResult> DuplicateAsync(
        string hostId,
        DuplicateContainerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var sourceId = RequireContainerId(request.ContainerId);

        // 先校验本地字段再取客户端：名称非法属于「改一下就能成功」的错误，
        // 不该被「容器不存在」抢在前面报出来——那会让用户以为搞错了容器 ID。
        var requestedName = string.IsNullOrWhiteSpace(request.Name) ? null : RequireContainerName(request.Name);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var source = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.InspectContainerAsync(sourceId, cancellationToken),
            $"容器 {sourceId}",
            cancellationToken).ConfigureAwait(false);

        var sourceName = source.Name?.TrimStart('/') ?? string.Empty;
        var baseName = requestedName ?? sourceName + CopySuffix;

        RequireContainerName(baseName);

        var name = await ResolveFreeNameAsync(client, baseName, cancellationToken).ConfigureAwait(false);
        var parameters = BuildRecreateParameters(source, name);

        var created = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.CreateContainerAsync(parameters, cancellationToken),
            $"容器 {sourceId} 的副本",
            cancellationToken).ConfigureAwait(false);

        var warnings = created.Warnings is null ? [] : (IReadOnlyList<string>)[.. created.Warnings];
        var start = request.Start ?? false;

        if (start)
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken),
                $"容器 {created.ID}",
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "主机 {HostId} 复制容器 {SourceId} 为 {NewId}（名称={Name}，启动={Start}）",
            hostId,
            sourceId,
            created.ID,
            name,
            start);

        // Recreated=false：复制是「新建了一个容器」，原容器毫发无损，
        // 调用方据此区分「我原来的容器还在」与「我原来的容器被换掉了」。
        return new ContainerRecreatedResult(created.ID, name, null, start, warnings, Recreated: false);
    }

    // ==================== 追加挂载（重建） ====================

    /// <summary>
    /// 为已存在容器追加挂载（<b>重建容器</b>，ID 会变）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">追加挂载请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新容器 ID、被让位的旧容器 ID 与实际生效的运行状态。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识或挂载为空（<c>1000</c>）、容器不存在（<c>1401</c>）、
    /// 重建失败且回滚也失败（<c>1402</c>）、主机不可达（<c>1301</c>）。
    /// </exception>
    /// <remarks>
    /// <para>⚠️ <b>这个操作会重建容器</b>。Docker 没有「给已存在容器加挂载」的接口，
    /// 唯一途径是重建，因此容器 ID 一定变。界面必须把这一点写在二次确认里。</para>
    /// <para><b>让位而不是删除</b>：重建要用回原来的名字，而旧容器占着这个名字。
    /// 这里把旧容器<b>改名</b>而不是删掉——删除不可逆，而重建失败时我们需要它还在。
    /// 重建成功后旧容器以 <c>whalefleet-replaced-*</c> 保留在列表里，
    /// 用户确认新容器没问题后自己删。</para>
    /// <para><b>失败回滚</b>：重建失败时把旧容器改回原名并恢复它原本的运行状态，
    /// 然后把原始异常原样抛出——回滚失败也不会掩盖真正的失败原因。</para>
    /// </remarks>
    public async Task<ContainerRecreatedResult> AddVolumesAsync(
        string hostId,
        AddContainerVolumesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var addedBinds = RequireVolumes(request.Volumes)
            ?? throw BusinessException.Validation("volumes 不能为空（要追加的挂载不能为空列表）");

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var source = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.InspectContainerAsync(containerId, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        var name = source.Name?.TrimStart('/')
            ?? throw BusinessException.Validation($"容器 {containerId} 没有名称，无法按原名重建");

        var wasRunning = source.State?.Running ?? false;
        var parameters = BuildRecreateParameters(source, name);

        // 追加而不是覆盖：原有挂载全部保留。同一目标路径重复挂载会让守护进程行为不确定，
        // 因此这里提前挡掉（「同一个容器里挂两次同一个目录」几乎总是配置错了）。
        var existingBinds = parameters.HostConfig?.Binds ?? [];
        var existingDestinations = existingBinds.Select(DestinationOf).ToList();

        foreach (var bind in addedBinds)
        {
            var destination = DestinationOf(bind);

            if (existingDestinations.Contains(destination, StringComparer.Ordinal))
            {
                throw BusinessException.Validation(
                    $"容器 {name} 已经挂载了 {destination}；追加挂载不允许重复同一目标路径（要替换请先删掉原挂载）");
            }

            existingDestinations.Add(destination);
        }

        parameters.HostConfig!.Binds = [.. existingBinds, .. addedBinds];

        // 顺序不能反：先让位（旧容器改名）才能用原名创建新容器。
        // 反过来会得到「名字被占用」，而那时旧容器已经被停了——一次失败的操作留下两个后果。
        var parkedName = await ParkContainerAsync(client, containerId, name, cancellationToken).ConfigureAwait(false);

        if (wasRunning)
        {
            await this.StopForRecreateAsync(client, containerId, cancellationToken).ConfigureAwait(false);
        }

        CreateContainerResponse created;

        try
        {
            created = await DockerErrorTranslator.ExecuteAsync(
                () => client.Containers.CreateContainerAsync(parameters, cancellationToken),
                $"重建容器 {name}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (BusinessException)
        {
            // 回滚：把旧容器改回原名并恢复运行状态。回滚本身失败时**不**掩盖原始异常——
            // 用户需要看到的是「重建为什么失败」，而旧容器改名失败是另一个需要单独排查的问题。
            await this.RestoreParkedContainerAsync(
                client,
                containerId,
                name,
                parkedName,
                wasRunning,
                cancellationToken).ConfigureAwait(false);

            throw;
        }

        // start 为 null 时沿用原状态：对一个本来就没运行的容器擅自启动它是用户没要求的副作用
        var start = request.Start ?? wasRunning;

        if (start)
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken),
                $"容器 {created.ID}",
                cancellationToken).ConfigureAwait(false);
        }

        var warnings = created.Warnings is null ? [] : (IReadOnlyList<string>)[.. created.Warnings];

        _logger.LogInformation(
            "主机 {HostId} 为容器 {OldId} 追加 {Count} 个挂载后重建：新容器 {NewId}，旧容器让位为 {ParkedName}，启动={Start}",
            hostId,
            containerId,
            addedBinds.Count,
            created.ID,
            parkedName,
            start);

        return new ContainerRecreatedResult(created.ID, name, containerId, start, warnings, Recreated: true);
    }

    // ==================== 复制 / 重建共用的内部实现 ====================

    /// <summary>
    /// 把 inspect 结果还原成「能重新创建出等价容器」的参数。
    /// </summary>
    /// <param name="source">原容器的 inspect 结果。</param>
    /// <param name="name">新容器名。</param>
    /// <returns>创建参数。</returns>
    /// <remarks>
    /// <para><b>能还原多少取决于 inspect 给了什么</b>：Docker 只回填配置，不回填「镜像名」——
    /// 它给的是镜像 ID。因此这里用 <c>Config.Image</c>（镜像名）而不是顶层 <c>Image</c>：
    /// 顶层那个是 <c>sha256:…</c>，把它当镜像名传给 create 恰好也能工作，
    /// 但一旦原镜像被删，副本就再也创建不出来（而名字还能拉回同一个镜像）。</para>
    /// <para><b>刻意不还原的字段</b>：<c>AutoRemove</c>、<c>RestartCount</c>、<c>State</c>、
    /// <c>NetworkID</c> 等运行时状态——它们描述的是「这次运行」，不是「这个容器的配置」。
    /// 原样带过去会让副本带着上一世的运行痕迹。</para>
    /// </remarks>
    // SDK 把 Cmd / Env / Labels / NetworkMode / Devices 等字段声明为**非空**，
    // 但它们实际都是可选的：序列化时省略 null（与 Docker 的「未指定」语义一致），
    // 而传空数组 / 空串会被当成「显式清空」——例如把 Cmd 传成 [] 会让容器不带任何启动命令。
    // 因此这里必须传 null，只用 pragma 关掉与 SDK 标注的不一致。
#pragma warning disable CS8601
    private static CreateContainerParameters BuildRecreateParameters(ContainerInspectResponse source, string name)
    {
        var config = source.Config;
        var hostConfig = source.HostConfig;

        var (exposedPorts, portBindings) = BuildPortsFromInspect(hostConfig?.PortBindings, config?.ExposedPorts);

        return new CreateContainerParameters
        {
            // 用镜像名而不是镜像 ID：ID 在原镜像被删后就失效了，而名字还能拉回同一个镜像
            Image = config?.Image ?? source.Image,
            Name = name,
            Cmd = config?.Cmd is null or [] ? null : [.. config.Cmd],
            Entrypoint = config?.Entrypoint is null or [] ? null : [.. config.Entrypoint],
            Env = config?.Env is null or [] ? null : [.. config.Env],
            Labels = config?.Labels is null or { Count: 0 }
                ? null
                : new Dictionary<string, string>(config.Labels, StringComparer.Ordinal),
            WorkingDir = string.IsNullOrEmpty(config?.WorkingDir) ? null : config.WorkingDir,
            Hostname = string.IsNullOrEmpty(config?.Hostname) ? null : config.Hostname,
            User = string.IsNullOrEmpty(config?.User) ? null : config.User,
            Tty = config?.Tty ?? false,
            OpenStdin = config?.OpenStdin ?? false,
            StopSignal = string.IsNullOrEmpty(config?.StopSignal) ? null : config.StopSignal,
            StopTimeout = config?.StopTimeout,
            ExposedPorts = exposedPorts,
            Healthcheck = MapHealthCheck(config?.Healthcheck),

            // ⚠️ 网络**不能**按 inspect 原样还原：NetworkMode 若是用户自定义网络名，
            // 必须同时给出 NetworkingConfig.EndpointsConfig，否则守护进程会拒绝创建
            // （"network-scoped alias is supported only for containers in user defined networks"）。
            // 这里改成「NetworkMode=default + 逐个挂到原网络」，这是 Docker 自己重建容器时的做法。
            NetworkingConfig = BuildNetworkingConfig(source.NetworkSettings?.Networks),
            HostConfig = new HostConfig
            {
                NetworkMode = "default",
                Binds = BuildBindsFromMounts(source.Mounts),
                PortBindings = portBindings,
                RestartPolicy = BuildRestartPolicy(MapRestartPolicy(hostConfig?.RestartPolicy?.Name)),
                CapAdd = hostConfig?.CapAdd is null or [] ? null : [.. hostConfig.CapAdd],
                CapDrop = hostConfig?.CapDrop is null or [] ? null : [.. hostConfig.CapDrop],
                Devices = hostConfig?.Devices is null or [] ? null : [.. hostConfig.Devices],
                Sysctls = hostConfig?.Sysctls is null or { Count: 0 }
                    ? null
                    : new Dictionary<string, string>(hostConfig.Sysctls, StringComparer.Ordinal),
                Ulimits = hostConfig?.Ulimits is null or [] ? null : [.. hostConfig.Ulimits],
                LogConfig = hostConfig?.LogConfig is null
                    ? null
                    : new LogConfig
                    {
                        Type = hostConfig.LogConfig.Type,
                        Config = hostConfig.LogConfig.Config is null or { Count: 0 }
                            ? null
                            : new Dictionary<string, string>(hostConfig.LogConfig.Config, StringComparer.Ordinal),
                    },
                Memory = hostConfig?.Memory ?? 0,
                NanoCPUs = hostConfig?.NanoCPUs ?? 0,
            },
        };
    }
#pragma warning restore CS8601

    /// <summary>
    /// 从 inspect 的挂载列表还原 <c>Binds</c>。
    /// </summary>
    /// <param name="mounts">inspect 返回的挂载列表。</param>
    /// <returns><c>source:destination[:ro]</c> 列表；无挂载时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>tmpfs 必须跳过</b>：它在 inspect 里没有 <c>Source</c>，而 <c>Binds</c> 的语法
    /// 表达不了 tmpfs（要写进 <c>HostConfig.Tmpfs</c>）。硬拼会得到 <c>:/dev/shm</c>
    /// 这种把「临时内存盘」变成「宿主根目录挂进容器」的请求——那是一次静默的权限放大。
    /// </remarks>
    private static List<string>? BuildBindsFromMounts(IList<MountPoint>? mounts)
    {
        if (mounts is not { Count: > 0 })
        {
            return null;
        }

        var binds = new List<string>(mounts.Count);

        foreach (var mount in mounts)
        {
            if (string.IsNullOrWhiteSpace(mount.Source) || string.IsNullOrWhiteSpace(mount.Destination))
            {
                continue;
            }

            // tmpfs 与 npipe 没有宿主来源，硬拼等于凭空造出一个宿主路径
            if (string.Equals(mount.Type, "tmpfs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mount.Type, "npipe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            binds.Add(mount.RW
                ? $"{mount.Source}:{mount.Destination}"
                : $"{mount.Source}:{mount.Destination}:ro");
        }

        return binds.Count > 0 ? binds : null;
    }

    /// <summary>
    /// 从 inspect 的端口绑定还原「暴露端口 + 端口绑定」两半参数。
    /// </summary>
    /// <param name="portBindings">inspect 的端口绑定（键为 <c>"容器端口/协议"</c>）。</param>
    /// <param name="declaredExposedPorts">镜像声明的暴露端口。</param>
    /// <returns>暴露端口字典与绑定字典；都没有时两个都是 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>为什么两边都要</b>：只给 <c>PortBindings</c> 的话，<c>docker ps</c> 上看不到
    /// EXPOSE 那一列（它读的是 <c>ExposedPorts</c>）；而镜像声明了 EXPOSE 但没绑定的端口
    /// 是真实存在的配置，丢掉它等于副本与原容器在端口声明上不一致。
    /// </remarks>
    private static (IDictionary<string, EmptyStruct>? Exposed, IDictionary<string, IList<PortBinding>>? Bindings)
        BuildPortsFromInspect(
            IDictionary<string, IList<PortBinding>>? portBindings,
            IDictionary<string, EmptyStruct>? declaredExposedPorts)
    {
        Dictionary<string, EmptyStruct>? exposed = null;
        Dictionary<string, IList<PortBinding>>? bindings = null;

        if (declaredExposedPorts is { Count: > 0 })
        {
            exposed = new Dictionary<string, EmptyStruct>(declaredExposedPorts, StringComparer.Ordinal);
        }

        if (portBindings is not { Count: > 0 })
        {
            return (exposed, null);
        }

        bindings = new Dictionary<string, IList<PortBinding>>(portBindings, StringComparer.Ordinal);

        if (exposed is not null)
        {
            foreach (var key in bindings.Keys)
            {
                exposed.TryAdd(key, default);
            }
        }
        else
        {
            // 显式给 TElement：`_ => default` 在这个位置无法推断出类型
            // （default 既可能是值类型也可能是引用类型，编译器没有依据）
            exposed = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);

            foreach (var key in bindings.Keys)
            {
                exposed[key] = default;
            }
        }

        return (exposed, bindings);
    }

    /// <summary>
    /// 从 inspect 的网络列表构造 <see cref="NetworkingConfig"/>。
    /// </summary>
    /// <param name="networks">inspect 的网络字典（键为网络名）。</param>
    /// <returns>网络配置；未接入任何网络时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>跳过三个内置网络</b>：<c>bridge</c> / <c>host</c> / <c>none</c> 不能作为
    /// <c>EndpointsConfig</c> 的键——把它们写进去守护进程会拒绝创建。
    /// <c>host</c> 与 <c>none</c> 还是<b>互斥</b>的：容器要么共享宿主网络栈，要么完全没有网络。
    /// </remarks>
    private static NetworkingConfig? BuildNetworkingConfig(IDictionary<string, EndpointSettings>? networks)
    {
        if (networks is not { Count: > 0 })
        {
            return null;
        }

        Dictionary<string, EndpointSettings>? endpoints = null;

        foreach (var pair in networks)
        {
            if (pair.Value is null)
            {
                continue;
            }

            // 内置网络只能作为 NetworkMode，不能作为 EndpointsConfig 的键
            if (IsBuiltInNetwork(pair.Key))
            {
                continue;
            }

            endpoints ??= new Dictionary<string, EndpointSettings>(StringComparer.Ordinal);
            endpoints[pair.Key] = pair.Value;
        }

        if (endpoints is not { Count: > 0 })
        {
            return null;
        }

        return new NetworkingConfig { EndpointsConfig = endpoints };
    }

    /// <summary>
    /// 判断网络名是否是内置网络。
    /// </summary>
    /// <param name="name">网络名。</param>
    /// <returns>内置返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>必须同时收 <c>null</c> 与 <c>none</c></b>：Docker 26 把内置的 <c>null</c> 网络
    /// 改名成 <c>none</c>，老守护进程上 inspect 回来的仍然是 <c>null</c>。
    /// 只收其中一个会在另一种守护进程上让重建失败。
    /// </remarks>
    private static bool IsBuiltInNetwork(string? name) =>
        string.IsNullOrEmpty(name)
        || string.Equals(name, "bridge", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "host", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把 inspect 的健康检查配置还原成创建用的配置。
    /// </summary>
    /// <param name="source">inspect 的健康检查。</param>
    /// <returns>创建参数里的健康检查；无检查时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b><c>["NONE"]</c> 要还原成 <see langword="null"/></b>：那是「镜像里有 HEALTHCHECK，
    /// 但这个容器关掉了它」的状态。原样回传会让副本继承镜像的检查——
    /// 而用户当初显式关掉的正是它。
    /// </remarks>
    private static HealthcheckConfig? MapHealthCheck(HealthcheckConfig? source)
    {
        if (source?.Test is not { Count: > 0 } test)
        {
            return null;
        }

        if (test.Count == 1 && string.Equals(test[0], "NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new HealthcheckConfig
        {
            Test = [.. test],
            Interval = source.Interval,
            Timeout = source.Timeout,
            StartPeriod = source.StartPeriod,
            Retries = source.Retries,
        };
    }

    /// <summary>
    /// 找一个没被占用的容器名。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="baseName">期望的名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可用的名字；<paramref name="baseName"/> 空闲时原样返回。</returns>
    /// <exception cref="BusinessException">连试 <see cref="MaxNameRetries"/> 次都冲突（<c>1404</c>）。</exception>
    /// <remarks>
    /// <b>为什么不直接用请求里的名字</b>：复制时最容易撞的就是「<c>-copy</c> 已经存在」
    /// （用户连点两次）。这里按 <c>-2</c>、<c>-3</c> 递增避让，而不是让第二次点击失败。
    /// </remarks>
    private static async Task<string> ResolveFreeNameAsync(
        IDockerClient client,
        string baseName,
        CancellationToken cancellationToken)
    {
        if (!await NameTakenAsync(client, baseName, cancellationToken).ConfigureAwait(false))
        {
            return baseName;
        }

        for (var attempt = 2; attempt <= MaxNameRetries; attempt++)
        {
            var candidate = $"{baseName}-{attempt.ToString(CultureInfo.InvariantCulture)}";

            if (!await NameTakenAsync(client, candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw BusinessException.Conflict($"容器名 {baseName} 及其派生名都被占用了，请换一个名字");
    }

    /// <summary>
    /// 判断容器名是否已被占用。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="name">候选名字。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>被占用返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>为什么用 inspect 而不是 create 试错</b>：create 失败会留下难看的副作用
    /// （部分网络配置已生效），而 inspect 是纯读。
    /// </remarks>
    private static async Task<bool> NameTakenAsync(
        IDockerClient client,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.Containers.InspectContainerAsync(name, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (
            exception is DockerContainerNotFoundException
            or DockerApiException { StatusCode: HttpStatusCode.NotFound })
        {
            return false;
        }
    }

    /// <summary>
    /// 把旧容器改名让位。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="containerId">旧容器 ID。</param>
    /// <param name="currentName">旧容器当前的名字（用于拼让位名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>让位后的名字。</returns>
    /// <remarks>
    /// <b>为什么让位名里带容器 ID</b>：连续重建同一个容器时，每次的旧容器都是不同的一代，
    /// 名字带 ID 才能让用户从列表上分辨出「哪一代被换掉了」。
    /// </remarks>
    private static async Task<string> ParkContainerAsync(
        IDockerClient client,
        string containerId,
        string currentName,
        CancellationToken cancellationToken)
    {
        var shortId = containerId.Length <= 12 ? containerId : containerId[..12];
        var baseName = $"{ReplacedPrefix}{shortId}";

        for (var attempt = 0; attempt < MaxNameRetries; attempt++)
        {
            var candidate = attempt == 0 ? baseName : $"{baseName}-{attempt.ToString(CultureInfo.InvariantCulture)}";

            if (await NameTakenAsync(client, candidate, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await DockerErrorTranslator.ExecuteAsync(
                () => client.Containers.RenameContainerAsync(
                    containerId,
                    new ContainerRenameParameters { NewName = candidate },
                    cancellationToken),
                $"容器 {currentName}",
                cancellationToken).ConfigureAwait(false);

            return candidate;
        }

        throw BusinessException.Conflict($"无法为容器 {currentName} 找到空闲的让位名，请稍后重试");
    }

    /// <summary>
    /// 为重建而停止容器（已停止的直接跳过）。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="containerId">容器标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// <b>为什么要停</b>：旧容器停着但仍占着已映射的宿主端口，新容器起来时会撞
    /// <c>port is already allocated</c>——而那时的报错完全指不出「是被自己刚停掉的旧容器占的」。
    /// </remarks>
    private async Task StopForRecreateAsync(
        IDockerClient client,
        string containerId,
        CancellationToken cancellationToken)
    {
        await this.ExecuteStateChangeAsync(
            () => client.Containers.StopContainerAsync(
                containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 10 },
                cancellationToken),
            containerId,
            "停止（准备重建）",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 重建失败后把让位的旧容器改回原名并恢复运行状态。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="containerId">旧容器 ID。</param>
    /// <param name="originalName">原名。</param>
    /// <param name="parkedName">让位后的名字。</param>
    /// <param name="wasRunning">重建前是否在运行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// ⚠️ <b>回滚失败只记日志、不抛</b>：调用方拿到的异常必须是「重建为什么失败」，
    /// 而不是「回滚也失败了」——后者会让真正的失败原因消失。
    /// 旧容器改不回原名时它仍在列表里（名字是 <c>whalefleet-replaced-*</c>），
    /// 用户看得见、也删得掉。
    /// </remarks>
    private async Task RestoreParkedContainerAsync(
        IDockerClient client,
        string containerId,
        string originalName,
        string parkedName,
        bool wasRunning,
        CancellationToken cancellationToken)
    {
        try
        {
            await DockerErrorTranslator.ExecuteAsync(
                () => client.Containers.RenameContainerAsync(
                    containerId,
                    new ContainerRenameParameters { NewName = originalName },
                    cancellationToken),
                $"让位容器 {parkedName}",
                cancellationToken).ConfigureAwait(false);

            if (wasRunning)
            {
                await DockerErrorTranslator.ExecuteAsync(
                    () => client.Containers.StartContainerAsync(
                        containerId,
                        new ContainerStartParameters(),
                        cancellationToken),
                    $"容器 {containerId}",
                    cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "重建失败后已回滚：容器 {ContainerId} 改回原名 {Name}（运行={Running}）",
                containerId,
                originalName,
                wasRunning);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "重建失败且回滚也失败：容器 {ContainerId} 仍以让位名 {ParkedName} 存在，需人工改名回 {Name}",
                containerId,
                parkedName,
                originalName);
        }
    }

    /// <summary>
    /// 取 <c>Binds</c> 条目的容器内目标路径。
    /// </summary>
    /// <param name="bind"><c>source:destination[:ro]</c> 形式的条目。</param>
    /// <returns>容器内路径。</returns>
    /// <remarks>
    /// <b>Windows 风格的盘符要防一手</b>：<c>C:\data:/data</c> 里第一个冒号后面还是盘符，
    /// 直接按第一个冒号切会得到 <c>C</c>。因此从<b>最后一个</b>冒号往前找目标段。
    /// 本项目按 Linux 部署，但解析规则与 Docker 一致才不会被将来移植时咬一口。
    /// </remarks>
    private static string DestinationOf(string bind)
    {
        var segments = bind.Split(':');
        var destination = segments.Length switch
        {
            2 => segments[1],
            3 => segments[1],
            _ => segments[^1],
        };

        return destination;
    }
}
