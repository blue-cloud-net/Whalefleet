using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Whalefleet.Application.Common;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排计划：把解析后的 Compose 文档变成「要建哪些网络、哪些卷、哪些容器」。
/// </summary>
/// <remarks>
/// <para><b>规划与执行分开</b>：规划是纯函数（输入 YAML，输出资源清单），
/// 执行才碰守护进程。这样「哪些资源会被建出来」「启动顺序是什么」
/// 可以在不接触 Docker 的情况下被测试与推演。</para>
/// <para><b>命名规则照抄 Compose</b>：网络叫 <c>&lt;项目名&gt;_&lt;网络名&gt;</c>、
/// 具名卷叫 <c>&lt;项目名&gt;_&lt;卷名&gt;</c>、容器叫 <c>&lt;项目名&gt;-&lt;服务名&gt;-1</c>。
/// 与 Compose 保持一致意味着两套工具能认出彼此的项目——
/// 用户在命令行 <c>docker compose ls</c> 里也能看到本平台部署的 Stack。</para>
/// <para><b>配置哈希决定「要不要重建」</b>：与 Compose 同一套语义——
/// 哈希变了才重建，没变就保留。哈希取「服务的完整定义」的 SHA-256，
/// 因此改了端口、改了环境变量、改了健康检查都会导致重建，而改��无关服务不会。</para>
/// </remarks>
internal static class ComposePlanner
{
    /// <summary>
    /// 项目默认网络的逻辑名（Compose 的约定名）。
    /// </summary>
    public const string DefaultNetworkName = "default";

    /// <summary>
    /// 规划一个编排项目。
    /// </summary>
    /// <param name="projectName">项目名（= Stack 名称）。</param>
    /// <param name="document">解析后的 Compose 文档。</param>
    /// <returns>编排计划。</returns>
    /// <exception cref="BusinessException">
    /// <c>depends_on</c> 存在环（<c>1904</c>）。
    /// </exception>
    public static ComposePlan Plan(string projectName, ComposeDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentNullException.ThrowIfNull(document);

        var networks = PlanNetworks(projectName, document);
        var volumes = PlanVolumes(projectName, document);
        var services = PlanServices(projectName, document, networks, volumes);
        var order = ResolveStartOrder(services);

        return new ComposePlan(projectName, networks, volumes, services, order);
    }

    /// <summary>
    /// 规划项目需要的网络。
    /// </summary>
    /// <param name="projectName">项目名。</param>
    /// <param name="document">Compose 文档。</param>
    /// <returns>网络计划列表。</returns>
    /// <remarks>
    /// <b>默认网络永远存在</b>：即使 YAML 里没写 <c>networks</c>，
    /// Compose 也会建一个 <c>&lt;项目名&gt;_default</c>，服务默认接入它。
    /// 不建的话服务就没有网络（容器只能用 <c>bridge</c>，与 Compose 行为不符）。
    /// </remarks>
    private static List<ComposeNetworkPlan> PlanNetworks(string projectName, ComposeDocument document)
    {
        var plans = new List<ComposeNetworkPlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in document.Networks)
        {
            if (!seen.Add(definition.Name))
            {
                continue;
            }

            plans.Add(new ComposeNetworkPlan(
                definition.Name,
                ResolveResourceName(projectName, definition.Name, definition.ActualName),
                definition.External));
        }

        // 仅补建默认网络；服务引用的其他网络必须在顶层显式声明。
        if (seen.Add(DefaultNetworkName))
        {
            plans.Add(new ComposeNetworkPlan(
                DefaultNetworkName,
                $"{projectName}_{DefaultNetworkName}",
                false));
        }

        return plans;
    }

    /// <summary>
    /// 规划项目需要的具名卷。
    /// </summary>
    /// <param name="projectName">项目名。</param>
    /// <param name="document">Compose 文档。</param>
    /// <returns>卷计划列表。</returns>
    /// <remarks>
    /// <b>服务引用了但顶层没声明的卷会被隐式创建</b>（与 Compose 一致）：
    /// YAML 里写 <c>volumes: [data:/var/lib/data]</c> 而没写顶层 <c>volumes:</c> 段时，
    /// Compose 会建一个名为 <c>&lt;项目名&gt;_data</c> 的卷。
    /// 漏掉这一步的症状是「服务创建失败：卷不存在」——而那正是用户最常踩的写法。
    /// </remarks>
    private static List<ComposeVolumePlan> PlanVolumes(string projectName, ComposeDocument document)
    {
        var plans = new List<ComposeVolumePlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in document.Volumes)
        {
            if (!seen.Add(definition.Name))
            {
                continue;
            }

            plans.Add(new ComposeVolumePlan(
                definition.Name,
                ResolveResourceName(projectName, definition.Name, definition.ActualName),
                definition.External));
        }

        foreach (var service in document.Services)
        {
            foreach (var mount in service.Volumes)
            {
                if (mount.IsBind || !seen.Add(mount.Source))
                {
                    continue;
                }

                plans.Add(new ComposeVolumePlan(
                    mount.Source,
                    ResolveResourceName(projectName, mount.Source, null),
                    false));
            }
        }

        return plans;
    }

    /// <summary>
    /// 规划服务对应的容器。
    /// </summary>
    /// <param name="projectName">项目名。</param>
    /// <param name="document">Compose 文档。</param>
    /// <param name="networks">网络计划。</param>
    /// <param name="volumes">卷计划。</param>
    /// <returns>服务计划列表。</returns>
    private static List<ComposeServicePlan> PlanServices(
        string projectName,
        ComposeDocument document,
        List<ComposeNetworkPlan> networks,
        List<ComposeVolumePlan> volumes)
    {
        var plans = new List<ComposeServicePlan>(document.Services.Count);

        foreach (var service in document.Services)
        {
            // 服务没显式写 networks 时接入默认网络（与 Compose 一致）
            var serviceNetworks = service.Networks.Count == 0
                ? [DefaultNetworkName]
                : service.Networks;

            foreach (var network in serviceNetworks)
            {
                if (networks.All(plan => !String.Equals(plan.Name, network, StringComparison.Ordinal)))
                {
                    throw new BusinessException(
                        ErrorCodes.ComposeInvalid,
                        $"「services.{service.Name}.networks」引用了不存在的网络「{network}」");
                }
            }

            var mounts = service.Volumes
                .Where(static mount => !mount.IsBind)
                .Select(mount => new ComposeServiceVolume(
                    mount.Source,
                    ResolveVolumeName(volumes, mount.Source),
                    mount.Destination,
                    mount.ReadOnly))
                .ToList();

            var binds = service.Volumes
                .Where(static mount => mount.IsBind)
                .Select(static mount => new ComposeServiceBind(
                    mount.Source,
                    mount.Destination,
                    mount.ReadOnly))
                .ToList();

            plans.Add(new ComposeServicePlan(
                service,
                $"{projectName}-{service.Name}-1",
                ComputeConfigHash(service),
                serviceNetworks,
                mounts,
                binds));
        }

        return plans;
    }

    /// <summary>
    /// 解析资源的实际名字。
    /// </summary>
    /// <param name="projectName">项目名。</param>
    /// <param name="logicalName">逻辑名（YAML 里的键）。</param>
    /// <param name="actualName">显式指定的名字；为 <see langword="null"/> 时按 Compose 规则拼接。</param>
    /// <returns>实际名字。</returns>
    /// <remarks>
    /// <b>Compose 的规则</b>：顶层声明的名字若不以 <c>&lt;项目名&gt;_</c> 开头，
    /// 实际名字就是 <c>&lt;项目名&gt;_&lt;逻辑名&gt;</c>；已带前缀的保持原样。
    /// 这条规则让「多个项目共用一个具名卷」成为可能（写全名即可）。
    /// </remarks>
    private static string ResolveResourceName(string projectName, string logicalName, string? actualName)
    {
        if (!String.IsNullOrWhiteSpace(actualName))
        {
            return actualName.Trim();
        }

        var prefix = $"{projectName}_";

        return logicalName.StartsWith(prefix, StringComparison.Ordinal)
            ? logicalName
            : prefix + logicalName;
    }

    /// <summary>
    /// 解析具名卷的实际名字。
    /// </summary>
    /// <param name="volumes">卷计划。</param>
    /// <param name="source">YAML 里的卷名。</param>
    /// <returns>实际名字。</returns>
    /// <exception cref="BusinessException">卷不在计划里（<c>1904</c>）。</exception>
    private static string ResolveVolumeName(List<ComposeVolumePlan> volumes, string source)
    {
        foreach (var plan in volumes)
        {
            if (String.Equals(plan.Name, source, StringComparison.Ordinal))
            {
                return plan.ActualName;
            }
        }

        throw new BusinessException(
            ErrorCodes.ComposeInvalid,
            $"卷「{source}」既没有在顶层 volumes 里声明，也没有被隐式收录");
    }

    /// <summary>
    /// 计算服务的配置哈希。
    /// </summary>
    /// <param name="service">服务定义。</param>
    /// <returns>哈希的十六进制文本（64 位）。</returns>
    /// <remarks>
    /// <b>哈希的输入是「服务的完整定义」</b>：镜像、命令、环境变量、端口、挂载、
    /// 网络、依赖、重启策略、健康检查全部算进去。
    /// 因此改任何一个会影响容器行为的字段都会导致重建，
    /// 而改动其它服务不会影响本服务——这正是「滚动重启」的粒度依据。
    /// </remarks>
    public static string ComputeConfigHash(ComposeService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        var builder = new StringBuilder();

        builder.Append("image=").Append(service.Image).Append('\n');
        builder.Append("command=").Append(String.Join(' ', service.Command)).Append('\n');
        builder.Append("env=").Append(String.Join('\u001F', service.Environment)).Append('\n');
        builder.Append("ports=").Append(String.Join('\u001F', service.Ports.Select(static port =>
            $"{port.HostIp ?? String.Empty}:{port.HostPort?.ToString(CultureInfo.InvariantCulture) ?? String.Empty}:"
            + $"{port.ContainerPort.ToString(CultureInfo.InvariantCulture)}/{port.Protocol}"))).Append('\n');
        builder.Append("volumes=").Append(String.Join('\u001F', service.Volumes.Select(static mount =>
            $"{mount.Source}:{mount.Destination}:{mount.ReadOnly}"))).Append('\n');
        builder.Append("networks=").Append(String.Join('\u001F', service.Networks)).Append('\n');
        builder.Append("depends_on=").Append(String.Join('\u001F', service.DependsOn)).Append('\n');
        builder.Append("restart=").Append(service.Restart ?? String.Empty).Append('\n');

        if (service.HealthCheck is { } healthCheck)
        {
            builder.Append("healthcheck=")
                .Append(String.Join('\u001F', healthCheck.Test))
                .Append('|')
                .Append(healthCheck.IntervalSeconds?.ToString(CultureInfo.InvariantCulture) ?? String.Empty)
                .Append('|')
                .Append(healthCheck.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? String.Empty)
                .Append('|')
                .Append(healthCheck.StartPeriodSeconds?.ToString(CultureInfo.InvariantCulture) ?? String.Empty)
                .Append('|')
                .Append(healthCheck.Retries?.ToString(CultureInfo.InvariantCulture) ?? String.Empty)
                .Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// 解析服务的启动顺序。
    /// </summary>
    /// <param name="services">服务计划。</param>
    /// <returns>服务名列表（依赖在前）。</returns>
    /// <exception cref="BusinessException">
    /// <c>depends_on</c> 存在环（<c>1904</c>）。
    /// </exception>
    /// <remarks>
    /// <b>用拓扑排序而不是「按 YAML 顺序建」</b>：Compose 的
    /// <c>depends_on</c> 表达的是「必须先起来」，而不是「写在前面」。
    /// 按 YAML 顺序建会让声明顺序变成隐含契约——用户调整一下文件顺序，
    /// 启动顺序就变了，而且不会有任何提示。</remarks>
    public static IReadOnlyList<string> ResolveStartOrder(List<ComposeServicePlan> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var byName = services.ToDictionary(static plan => plan.Service.Name, StringComparer.Ordinal);
        var visited = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>(services.Count);

        foreach (var plan in services)
        {
            Visit(plan.Service.Name, byName, visited, order, []);
        }

        return order;
    }

    /// <summary>
    /// 深度优先遍历（拓扑排序）。
    /// </summary>
    /// <param name="name">当前服务名。</param>
    /// <param name="byName">服务名 → 计划。</param>
    /// <param name="visited">访问状态（0 = 未访问，1 = 在栈中，2 = 已完成）。</param>
    /// <param name="order">输出顺序。</param>
    /// <param name="stack">当前路径（用于报告环）。</param>
    /// <exception cref="BusinessException">存在环（<c>1904</c>）。</exception>
    private static void Visit(
        string name,
        Dictionary<string, ComposeServicePlan> byName,
        Dictionary<string, int> visited,
        List<string> order,
        List<string> stack)
    {
        if (visited.TryGetValue(name, out var state))
        {
            if (state == 2)
            {
                return;
            }

            var cycleStart = stack.IndexOf(name);
            var cycle = String.Join(" → ", stack.Skip(cycleStart < 0 ? 0 : cycleStart).Append(name));

            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「services.{name}」的 depends_on 存在循环依赖：{cycle}");
        }

        visited[name] = 1;
        stack.Add(name);

        if (byName.TryGetValue(name, out var plan))
        {
            foreach (var dependency in plan.Service.DependsOn)
            {
                Visit(dependency, byName, visited, order, stack);
            }
        }

        stack.RemoveAt(stack.Count - 1);
        visited[name] = 2;
        order.Add(name);
    }
}
