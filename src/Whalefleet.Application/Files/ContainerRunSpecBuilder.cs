using System.Globalization;
using System.Text;

using Docker.DotNet.Models;

// 本文件只拼装 ASCII 文本（命令 / YAML），其中参与格式化的数值都已显式指定
// InvariantCulture；CA1305 会在每个插值句柄上报一次，属于噪声。
#pragma warning disable CA1305

namespace Whalefleet.Application.Files;

/// <summary>
/// 把容器的 inspect 结果反向生成可复制的定义（<c>docker run</c> 命令 / compose 片段）。
/// </summary>
/// <remarks>
/// <para><b>必须读原始 inspect，而不是现有的详情投影</b>：<c>ContainerDetail</c> 只为「展示」服务，
/// 它刻意不含 <c>capAdd</c> / <c>sysctls</c> / <c>devices</c> / <c>ulimits</c> / <c>logConfig</c> /
/// <c>healthcheck</c> / 端口绑定的宿主 IP 等字段。用投影生成命令会**静默丢掉**这些配置，
/// 而生成出来的命令仍然「看起来对」——那比生成失败糟糕得多。</para>
/// <para><b>目标：粘进终端就能跑</b>。因此每个参数都要正确引号转义（见 <see cref="ShellQuoting"/>），
/// 且 <c>--name</c> 之类会与现有容器冲突的参数照原样生成（用户自己改名即可）。</para>
/// <para><b>不带 <c>-d</c></b>：是否后台运行是使用者的选择，不是容器配置的一部分。</para>
/// </remarks>
public static class ContainerRunSpecBuilder
{
    /// <summary>
    /// 生成 <c>docker run</c> 命令。
    /// </summary>
    /// <param name="container">容器的 inspect 结果。</param>
    /// <returns>可直接执行的命令（单行）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> 为 <see langword="null"/>。</exception>
    public static string BuildDockerRun(ContainerInspectResponse container)
    {
        ArgumentNullException.ThrowIfNull(container);

        var parts = new List<string> { "docker run" };

        AppendRunOptions(parts, container);

        // 优先用 Config.Image（如 nginx:1.27）；Inspect.Image 是镜像 ID，
        // 写进命令虽能跑，但没人看得懂、也不可移植到别的机器。
        var image = String.IsNullOrWhiteSpace(container.Config?.Image)
            ? container.Image ?? "image"
            : container.Config.Image;

        parts.Add(ShellQuoting.Quote(image));

        AppendCommand(parts, container);

        return String.Join(' ', parts);
    }

    /// <summary>
    /// 生成 compose 服务片段（YAML）。
    /// </summary>
    /// <param name="container">容器的 inspect 结果。</param>
    /// <returns>compose 片段（可直接粘进 <c>services:</c> 之下）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// <b>为什么只生成一个服务</b>：容器与 compose 项目不是一回事——一个容器只是一个服务。
    /// 生成整份文件会让人以为「这是原项目的 YAML」，而那正是我们<b>没有</b>信息去还原的东西。
    /// </remarks>
    public static string BuildComposeFragment(ContainerInspectResponse container)
    {
        ArgumentNullException.ThrowIfNull(container);

        var name = NormalizeServiceName(container.Name);
        var builder = new StringBuilder();

        builder.AppendLine("services:").AppendLine($"  {name}:");
        builder.AppendLine($"    image: {container.Config?.Image ?? container.Image}");
        builder.AppendLine($"    container_name: {name}");

        AppendComposeRestart(builder, container);
        AppendComposePorts(builder, container);
        AppendComposeEnvironment(builder, container);
        AppendComposeVolumes(builder, container);
        AppendComposeNetworks(builder, container);
        AppendComposeResources(builder, container);
        AppendComposeAdvanced(builder, container);

        if (container.Config?.Cmd is { Count: > 0 })
        {
            builder.AppendLine("    command:");

            foreach (var argument in container.Config.Cmd)
            {
                builder.AppendLine($"      - {YamlScalar(argument)}");
            }
        }

        if (container.Config?.Entrypoint is { Count: > 0 })
        {
            builder.AppendLine("    entrypoint:");

            foreach (var argument in container.Config.Entrypoint)
            {
                builder.AppendLine($"      - {YamlScalar(argument)}");
            }
        }

        AppendComposeLabels(builder, container);

        return builder.ToString();
    }

    /// <summary>
    /// 追加 <c>docker run</c> 的可选参数。
    /// </summary>
    /// <param name="parts">参数片段。</param>
    /// <param name="container">容器 inspect 结果。</param>
    private static void AppendRunOptions(List<string> parts, ContainerInspectResponse container)
    {
        var config = container.Config;
        var hostConfig = container.HostConfig;

        AppendRunOption(parts, "--name", NormalizeServiceName(container.Name));

        if (!String.IsNullOrWhiteSpace(config?.Hostname))
        {
            AppendRunOption(parts, "--hostname", config.Hostname);
        }

        if (!String.IsNullOrWhiteSpace(config?.User))
        {
            AppendRunOption(parts, "--user", config.User);
        }

        if (!String.IsNullOrWhiteSpace(config?.WorkingDir))
        {
            AppendRunOption(parts, "--workdir", config.WorkingDir);
        }

        if (config?.Tty == true)
        {
            parts.Add("--tty");
        }

        if (config?.OpenStdin == true)
        {
            parts.Add("--interactive");
        }

        AppendPorts(parts, hostConfig);
        AppendVolumes(parts, container);
        AppendNetworks(parts, hostConfig);

        if (hostConfig?.RestartPolicy is { } restart)
        {
            var restartValue = RestartValue(restart);

            // --restart no 是默认值，写上去只是噪声
            if (restartValue != "no")
            {
                AppendRunOption(parts, "--restart", restartValue);
            }
        }

        if (hostConfig?.AutoRemove == true)
        {
            parts.Add("--rm");
        }

        if (hostConfig?.Privileged == true)
        {
            parts.Add("--privileged");
        }

        if (hostConfig?.ReadonlyRootfs == true)
        {
            parts.Add("--read-only");
        }

        if (hostConfig?.Memory is > 0 and var memory)
        {
            AppendRunOption(parts, "--memory", memory.ToString(CultureInfo.InvariantCulture));
        }

        AppendCpus(parts, hostConfig);

        foreach (var capability in hostConfig?.CapAdd ?? [])
        {
            AppendRunOption(parts, "--cap-add", capability);
        }

        foreach (var entry in hostConfig?.Sysctls ?? new Dictionary<string, string>())
        {
            AppendRunOption(parts, "--sysctl", $"{entry.Key}={entry.Value}");
        }

        foreach (var device in hostConfig?.Devices ?? [])
        {
            var mapping = $"{device.PathOnHost}:{device.PathInContainer}";

            if (!String.IsNullOrWhiteSpace(device.CgroupPermissions))
            {
                mapping += ":" + device.CgroupPermissions;
            }

            AppendRunOption(parts, "--device", mapping);
        }

        foreach (var limit in hostConfig?.Ulimits ?? [])
        {
            AppendRunOption(parts, "--ulimit", $"{limit.Name}={limit.Soft}:{limit.Hard}");
        }

        if (hostConfig?.LogConfig is { } logConfig && !String.IsNullOrWhiteSpace(logConfig.Type))
        {
            AppendRunOption(parts, "--log-driver", logConfig.Type);

            foreach (var option in logConfig.Config ?? new Dictionary<string, string>())
            {
                AppendRunOption(parts, "--log-opt", $"{option.Key}={option.Value}");
            }
        }

        if (!String.IsNullOrWhiteSpace(hostConfig?.PidMode))
        {
            AppendRunOption(parts, "--pid", hostConfig.PidMode);
        }

        if (!String.IsNullOrWhiteSpace(hostConfig?.IpcMode))
        {
            AppendRunOption(parts, "--ipc", hostConfig.IpcMode);
        }

        foreach (var host in hostConfig?.ExtraHosts ?? [])
        {
            AppendRunOption(parts, "--add-host", host);
        }

        foreach (var option in hostConfig?.SecurityOpt ?? [])
        {
            AppendRunOption(parts, "--security-opt", option);
        }

        AppendHealthCheck(parts, config?.Healthcheck);

        if (!String.IsNullOrWhiteSpace(config?.StopSignal))
        {
            AppendRunOption(parts, "--stop-signal", config.StopSignal);
        }

        foreach (var variable in config?.Env ?? [])
        {
            AppendRunOption(parts, "--env", variable);
        }

        foreach (var label in config?.Labels ?? new Dictionary<string, string>())
        {
            AppendRunOption(parts, "--label", $"{label.Key}={label.Value}");
        }
    }

    /// <summary>
    /// 追加端口映射参数。
    /// </summary>
    /// <param name="parts">参数片段。</param>
    /// <param name="hostConfig">宿主侧配置。</param>
    private static void AppendPorts(List<string> parts, HostConfig? hostConfig)
    {
        foreach (var binding in hostConfig?.PortBindings ?? new Dictionary<string, IList<PortBinding>>())
        {
            // 键形如 "80/tcp"
            var slash = binding.Key.LastIndexOf('/');
            var containerPort = slash >= 0 ? binding.Key[..slash] : binding.Key;
            var protocol = slash >= 0 ? binding.Key[(slash + 1)..] : "tcp";

            foreach (var target in binding.Value ?? [])
            {
                var hostPort = target.HostPort;
                var hostIp = target.HostIP;

                var mapping = String.IsNullOrWhiteSpace(hostPort)
                    ? containerPort
                    : $"{hostPort}:{containerPort}";

                if (!String.IsNullOrWhiteSpace(hostIp))
                {
                    mapping = $"{hostIp}:{mapping}";
                }

                if (protocol != "tcp")
                {
                    mapping += "/" + protocol;
                }

                AppendRunOption(parts, "--publish", mapping);
            }
        }
    }

    /// <summary>
    /// 追加挂载参数。
    /// </summary>
    /// <param name="parts">参数片段。</param>
    /// <param name="container">容器 inspect 结果。</param>
    /// <remarks>
    /// <b>读 <c>inspect.Mounts</c>（<c>MountPoint</c>）而不是 <c>HostConfig.Mounts</c></b>：
    /// 用 <c>-v</c> / <c>--mount</c> 起的容器，<c>HostConfig.Mounts</c> 通常是<b>空的</b>
    /// （绑定落在 <c>Binds</c> 里），而 <c>inspect.Mounts</c> 是守护进程算出来的最终结果——
    /// 两者取错会生成一条「一个卷都没有」的命令，但它看起来完全正常。
    /// </remarks>
    private static void AppendVolumes(List<string> parts, ContainerInspectResponse container)
    {
        var mounts = container.Mounts;

        if (mounts is not { Count: > 0 })
        {
            // 回退到 Binds（老守护进程上某些形态不会填 Mounts）。
            foreach (var bind in container.HostConfig?.Binds ?? [])
            {
                AppendRunOption(parts, "--volume", bind);
            }

            return;
        }

        foreach (var mount in mounts)
        {
            if (mount.Type == "tmpfs")
            {
                AppendRunOption(parts, "--tmpfs", mount.Destination);

                continue;
            }

            var mode = mount.RW ? String.Empty : ":ro";

            AppendRunOption(parts, "--volume", $"{mount.Source}:{mount.Destination}{mode}");
        }
    }

    /// <summary>
    /// 追加网络参数。
    /// </summary>
    /// <param name="parts">参数片段。</param>
    /// <param name="hostConfig">宿主侧配置。</param>
    private static void AppendNetworks(List<string> parts, HostConfig? hostConfig)
    {
        var mode = hostConfig?.NetworkMode;

        if (String.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        // bridge / host / none 是内置网络模式，而其它值是「用户自建网络的名字」。
        // 两者对应的参数不同：前者用 --network 直接给模式名，后者也是 --network，
        // 但语义是「接进这个网络」——命令行上写法一样，因此这里不必区分。
        if (mode is "default")
        {
            return;
        }

        AppendRunOption(parts, "--network", mode);
    }

    /// <summary>
    /// 追加 CPU 限制参数。
    /// </summary>
    /// <param name="parts">参数片段。</param>
    /// <param name="hostConfig">宿主侧配置。</param>
    private static void AppendCpus(List<string> parts, HostConfig? hostConfig)
    {
        if (hostConfig is not { NanoCPUs: > 0 } || hostConfig.NanoCPUs <= 0)
        {
            return;
        }

        var cpus = (hostConfig.NanoCPUs / 1_000_000_000d).ToString("0.####", CultureInfo.InvariantCulture);

        AppendRunOption(parts, "--cpus", cpus);
    }

    /// <summary>
    /// 追加健康检查参数。
    /// </summary>
    /// <param name="parts">参数片段。</param>
    /// <param name="healthCheck">健康检查配置。</param>
    private static void AppendHealthCheck(List<string> parts, HealthcheckConfig? healthCheck)
    {
        if (healthCheck?.Test is not { Count: > 0 } test)
        {
            return;
        }

        // Docker 的 Test 首元素是 CMD / CMD-SHELL / NONE
        var kind = test[0];

        if (String.Equals(kind, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("--no-healthcheck");

            return;
        }

        var command = String.Equals(kind, "CMD-SHELL", StringComparison.OrdinalIgnoreCase)
            ? test.Count > 1 ? test[1] : String.Empty
            : String.Join(' ', test.Skip(1));

        AppendRunOption(parts, "--health-cmd", command);

        if (healthCheck.Interval is { } interval)
        {
            AppendRunOption(parts, "--health-interval", FormatDuration(interval));
        }

        if (healthCheck.Timeout is { } timeout)
        {
            AppendRunOption(parts, "--health-timeout", FormatDuration(timeout));
        }

        if (healthCheck.StartPeriod is { } startPeriod)
        {
            AppendRunOption(parts, "--health-start-period", FormatDuration(startPeriod));
        }

        if (healthCheck.Retries is { } retries and > 0)
        {
            AppendRunOption(parts, "--health-retries", retries.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// 追加容器启动命令。
    /// </summary>
    /// <param name="parts">参数片段。</param>
    /// <param name="container">容器 inspect 结果。</param>
    /// <remarks>
    /// <b>入口点必须显式覆盖</b>：镜像里可能自带 ENTRYPOINT，而 <c>docker run image cmd</c>
    /// 的 <c>cmd</c> 只是「追加到入口点后面」。若容器当初是<b>覆盖</b>过入口点的，
    /// 不写 <c>--entrypoint</c> 就会得到一条行为不同的命令。
    /// </remarks>
    private static void AppendCommand(List<string> parts, ContainerInspectResponse container)
    {
        var config = container.Config;

        if (config?.Entrypoint is { Count: > 0 } entrypoint)
        {
            var imageEntrypoint = container.Path;

            var overridden = String.IsNullOrWhiteSpace(imageEntrypoint) ||
                !String.Equals(entrypoint[0], imageEntrypoint, StringComparison.Ordinal);

            if (overridden)
            {
                AppendRunOption(parts, "--entrypoint", entrypoint[0]);

                // 入口点的其余参数要作为命令的开头部分给出
                foreach (var argument in entrypoint.Skip(1))
                {
                    parts.Add(ShellQuoting.Quote(argument));
                }
            }
        }

        foreach (var argument in config?.Cmd ?? [])
        {
            parts.Add(ShellQuoting.Quote(argument));
        }
    }

    /// <summary>
    /// 追加一个「参数 + 值」片段。
    /// </summary>
    /// <param name="parts">参数片段。</param>
    /// <param name="option">参数名。</param>
    /// <param name="value">参数值。</param>
    private static void AppendRunOption(List<string> parts, string option, string value)
    {
        parts.Add(option);
        parts.Add(ShellQuoting.Quote(value));
    }

    /// <summary>
    /// 追加 compose 的重启策略。
    /// </summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="container">容器 inspect 结果。</param>
    private static void AppendComposeRestart(StringBuilder builder, ContainerInspectResponse container)
    {
        if (container.HostConfig?.RestartPolicy is not { } policy)
        {
            return;
        }

        var value = RestartValue(policy);

        // restart: no 是默认值，写出来只是噪声
        if (value != "no")
        {
            builder.AppendLine($"    restart: {value}");
        }
    }

    /// <summary>
    /// 把重启策略枚举转成 Docker 的命令行 / compose 取值。
    /// </summary>
    /// <param name="policy">重启策略。</param>
    /// <returns>取值文本。</returns>
    /// <remarks>
    /// <b>不能直接把枚举名转小写</b>：<c>OnFailure</c> 转小写得 <c>onfailure</c>，
    /// 而 Docker 要的是 <c>on-failure</c>；<c>UnlessStopped</c> 同理。
    /// </remarks>
    private static string RestartValue(RestartPolicy policy)
    {
        var name = policy.Name switch
        {
            RestartPolicyKind.Always => "always",
            RestartPolicyKind.OnFailure => "on-failure",
            RestartPolicyKind.UnlessStopped => "unless-stopped",
            _ => "no",
        };

        if (name == "on-failure" && policy.MaximumRetryCount > 0)
        {
            return $"on-failure:{policy.MaximumRetryCount.ToString(CultureInfo.InvariantCulture)}";
        }

        return name;
    }

    /// <summary>
    /// 把时长格式化成 Docker 接受的时长文本。
    /// </summary>
    /// <param name="duration">时长。</param>
    /// <returns>如 <c>30s</c> / <c>1m30s</c> / <c>500ms</c>。</returns>
    /// <remarks>
    /// 直接 <c>ToString()</c> 会给 <c>00:00:30</c>——Docker 对健康检查时长只接受带单位的形式，
    /// 而它<b>不报错就默默当成 0</b>（于是健康检查每毫秒跑一次）。
    /// </remarks>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return "0s";
        }

        if (duration.TotalSeconds < 1)
        {
            return $"{duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms";
        }

        var total = (long)Math.Floor(duration.TotalSeconds);
        var minutes = total / 60;
        var seconds = total % 60;

        return minutes > 0
            ? $"{minutes.ToString(CultureInfo.InvariantCulture)}m{seconds.ToString(CultureInfo.InvariantCulture)}s"
            : $"{seconds.ToString(CultureInfo.InvariantCulture)}s";
    }

    /// <summary>
    /// 追加 compose 的端口映射。
    /// </summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="container">容器 inspect 结果。</param>
    private static void AppendComposePorts(StringBuilder builder, ContainerInspectResponse container)
    {
        var bindings = container.HostConfig?.PortBindings;

        if (bindings is not { Count: > 0 })
        {
            return;
        }

        builder.AppendLine("    ports:");

        foreach (var binding in bindings)
        {
            var slash = binding.Key.LastIndexOf('/');
            var containerPort = slash >= 0 ? binding.Key[..slash] : binding.Key;
            var protocol = slash >= 0 ? binding.Key[(slash + 1)..] : "tcp";

            foreach (var target in binding.Value ?? [])
            {
                var mapping = String.IsNullOrWhiteSpace(target.HostPort)
                    ? containerPort
                    : $"{target.HostPort}:{containerPort}";

                if (!String.IsNullOrWhiteSpace(target.HostIP))
                {
                    mapping = $"{target.HostIP}:{mapping}";
                }

                if (protocol != "tcp")
                {
                    mapping += "/" + protocol;
                }

                builder.AppendLine($"      - {YamlScalar(mapping)}");
            }
        }
    }

    /// <summary>
    /// 追加 compose 的环境变量。
    /// </summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="container">容器 inspect 结果。</param>
    /// <remarks>
    /// <b>值会原样出现在生成的 YAML 里</b>：这是用户自己要的「可复制定义」，
    /// 但也意味着 env 里的密码会显示在屏幕上。界面必须提示这一点——
    /// 与 0.18.0 的 MCP 脱敏是同一个关切。
    /// </remarks>
    private static void AppendComposeEnvironment(StringBuilder builder, ContainerInspectResponse container)
    {
        if (container.Config?.Env is not { Count: > 0 } environment)
        {
            return;
        }

        builder.AppendLine("    environment:");

        foreach (var variable in environment)
        {
            var separator = variable.IndexOf('=', StringComparison.Ordinal);
            var key = separator >= 0 ? variable[..separator] : variable;
            var value = separator >= 0 ? variable[(separator + 1)..] : String.Empty;

            builder.AppendLine($"      {YamlKey(key)}: {YamlScalar(value)}");
        }
    }

    /// <summary>
    /// 追加 compose 的挂载。
    /// </summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="container">容器 inspect 结果。</param>
    private static void AppendComposeVolumes(StringBuilder builder, ContainerInspectResponse container)
    {
        if (container.Mounts is not { Count: > 0 } mounts)
        {
            return;
        }

        builder.AppendLine("    volumes:");

        foreach (var mount in mounts)
        {
            var mode = mount.RW ? String.Empty : ":ro";

            builder.AppendLine($"      - {YamlScalar(mount.Source + ":" + mount.Destination + mode)}");
        }
    }

    /// <summary>
    /// 追加 compose 的网络。
    /// </summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="container">容器 inspect 结果。</param>
    private static void AppendComposeNetworks(StringBuilder builder, ContainerInspectResponse container)
    {
        var mode = container.HostConfig?.NetworkMode;

        if (String.IsNullOrWhiteSpace(mode) || mode is "default" or "bridge")
        {
            return;
        }

        // host / none 是模式，不能写进 services.X.networks（那是「自建网络列表」）。
        if (mode is "host" or "none")
        {
            builder.AppendLine($"    network_mode: {mode}");

            return;
        }

        builder.AppendLine("    networks:");
        builder.AppendLine($"      - {YamlScalar(mode)}");
    }

    /// <summary>
    /// 追加 compose 的资源限制。
    /// </summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="container">容器 inspect 结果。</param>
    private static void AppendComposeResources(StringBuilder builder, ContainerInspectResponse container)
    {
        var hostConfig = container.HostConfig;

        if (hostConfig?.Memory is > 0 and var memory)
        {
            builder.AppendLine($"    mem_limit: {memory.ToString(CultureInfo.InvariantCulture)}");
        }

        if (hostConfig?.ShmSize is > 0 and var shm)
        {
            builder.AppendLine($"    shm_size: {shm.ToString(CultureInfo.InvariantCulture)}");
        }

        if (hostConfig?.Privileged == true)
        {
            builder.AppendLine("    privileged: true");
        }

        if (hostConfig?.ReadonlyRootfs == true)
        {
            builder.AppendLine("    read_only: true");
        }
    }

    /// <summary>
    /// 追加 compose 的高级参数。
    /// </summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="container">容器 inspect 结果。</param>
    private static void AppendComposeAdvanced(StringBuilder builder, ContainerInspectResponse container)
    {
        var config = container.Config;
        var hostConfig = container.HostConfig;

        if (hostConfig?.CapAdd is { Count: > 0 } capabilities)
        {
            builder.AppendLine("    cap_add:");

            foreach (var capability in capabilities)
            {
                builder.AppendLine($"      - {YamlScalar(capability)}");
            }
        }

        if (hostConfig?.Sysctls is { Count: > 0 } sysctls)
        {
            builder.AppendLine("    sysctls:");

            foreach (var entry in sysctls)
            {
                builder.AppendLine($"      {YamlKey(entry.Key)}: {YamlScalar(entry.Value)}");
            }
        }

        if (hostConfig?.Ulimits is { Count: > 0 } ulimits)
        {
            builder.AppendLine("    ulimits:");

            foreach (var limit in ulimits)
            {
                builder.AppendLine(
                    $"      {YamlKey(limit.Name)}: {{ soft: {limit.Soft.ToString(CultureInfo.InvariantCulture)}, hard: {limit.Hard.ToString(CultureInfo.InvariantCulture)} }}");
            }
        }

        if (!String.IsNullOrWhiteSpace(config?.User))
        {
            builder.AppendLine($"    user: {YamlScalar(config.User)}");
        }

        if (!String.IsNullOrWhiteSpace(config?.WorkingDir))
        {
            builder.AppendLine($"    working_dir: {YamlScalar(config.WorkingDir)}");
        }

        if (!String.IsNullOrWhiteSpace(config?.Hostname))
        {
            builder.AppendLine($"    hostname: {YamlScalar(config.Hostname)}");
        }

        if (!String.IsNullOrWhiteSpace(config?.StopSignal))
        {
            builder.AppendLine($"    stop_signal: {YamlScalar(config.StopSignal)}");
        }

        if (config?.Tty == true)
        {
            builder.AppendLine("    tty: true");
        }

        if (config?.OpenStdin == true)
        {
            builder.AppendLine("    stdin_open: true");
        }

        if (!String.IsNullOrWhiteSpace(hostConfig?.LogConfig?.Type) && hostConfig.LogConfig.Type != "json-file")
        {
            builder.AppendLine("    logging:");
            builder.AppendLine($"      driver: {YamlScalar(hostConfig.LogConfig.Type)}");

            if (hostConfig.LogConfig.Config is { Count: > 0 } options)
            {
                builder.AppendLine("      options:");

                foreach (var option in options)
                {
                    builder.AppendLine($"        {YamlKey(option.Key)}: {YamlScalar(option.Value)}");
                }
            }
        }

        if (config?.Healthcheck?.Test is { Count: > 0 } test)
        {
            builder.AppendLine("    healthcheck:");

            var kind = test[0];

            if (String.Equals(kind, "NONE", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("      disable: true");
            }
            else
            {
                var command = String.Equals(kind, "CMD-SHELL", StringComparison.OrdinalIgnoreCase)
                    ? test.Count > 1 ? test[1] : String.Empty
                    : String.Join(' ', test.Skip(1));

                builder.AppendLine("      test: [\"CMD-SHELL\", " + YamlScalar(command) + "]");
            }

            if (config.Healthcheck.Interval is { } interval)
            {
                builder.AppendLine($"      interval: {FormatDuration(interval)}");
            }

            if (config.Healthcheck.Timeout is { } timeout)
            {
                builder.AppendLine($"      timeout: {FormatDuration(timeout)}");
            }

            if (config.Healthcheck.StartPeriod is { } startPeriod)
            {
                builder.AppendLine($"      start_period: {FormatDuration(startPeriod)}");
            }

            if (config.Healthcheck.Retries is { } retries and > 0)
            {
                builder.AppendLine($"      retries: {retries.ToString(CultureInfo.InvariantCulture)}");
            }
        }
    }

    /// <summary>
    /// 追加 compose 的标签。
    /// </summary>
    /// <param name="builder">输出缓冲。</param>
    /// <param name="container">容器 inspect 结果。</param>
    /// <remarks>
    /// <b>过滤掉 compose 自己的标签</b>：<c>com.docker.compose.*</c> 是编排引擎写进去的
    /// 记账信息（项目名、服务名、配置哈希）。把它们带进新文件会让「这个服务属于哪个项目」
    /// 出现两套互相矛盾的说法。
    /// </remarks>
    private static void AppendComposeLabels(StringBuilder builder, ContainerInspectResponse container)
    {
        var labels = container.Config?.Labels?
            .Where(label => !label.Key.StartsWith("com.docker.compose.", StringComparison.Ordinal))
            .ToArray();

        if (labels is not { Length: > 0 })
        {
            return;
        }

        builder.AppendLine("    labels:");

        foreach (var label in labels)
        {
            builder.AppendLine($"      {YamlKey(label.Key)}: {YamlScalar(label.Value)}");
        }
    }

    /// <summary>
    /// 把容器名规范成 compose 的服务名。
    /// </summary>
    /// <param name="name">容器名（可能带前导斜杠）。</param>
    /// <returns>服务名。</returns>
    /// <remarks>
    /// Docker 的容器名允许 <c>_</c> 与大写，而 compose 服务名要求
    /// <c>^[a-z0-9][a-z0-9_-]*$</c>——不规范化会得到一个「粘进去就报错」的片段。
    /// </remarks>
    private static string NormalizeServiceName(string? name)
    {
        if (String.IsNullOrWhiteSpace(name))
        {
            return "service";
        }

        var trimmed = name.TrimStart('/');
        var lowered = trimmed.ToLowerInvariant();
        var sanitized = new string([.. lowered.Select(character =>
            Char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-')]);

        return sanitized.Length == 0 ? "service" : sanitized.TrimStart('-');
    }

    /// <summary>
    /// 把值转成 YAML 标量（必要时加引号）。
    /// </summary>
    /// <param name="value">原始值。</param>
    /// <returns>YAML 标量。</returns>
    private static string YamlScalar(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return "\"\"";
        }

        // 只在需要时加引号：纯字母数字与常见符号直接裸写，读起来更像人写的 YAML。
        var safe = value.All(character =>
            Char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':' or '=' or '@' or ',');

        if (safe)
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// 把键转成 YAML 键（必要时加引号）。
    /// </summary>
    /// <param name="key">原始键。</param>
    /// <returns>YAML 键。</returns>
    private static string YamlKey(string key) => YamlScalar(key);
}
