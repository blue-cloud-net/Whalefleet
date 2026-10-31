using System.Globalization;
using Whalefleet.Application.Common;
using YamlDotNet.RepresentationModel;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// Compose 文件解析器：把 YAML 文本解析成 <see cref="ComposeDocument"/>。
/// </summary>
/// <remarks>
/// <para><b>核心原则：不支持的字段一律报错，绝不静默忽略</b>。
/// 静默忽略会产出「YAML 里写了的没生效」这类最难排查的故障——用户看到服务起来了，
/// 但端口没映射、环境变量没注入，而界面上没有任何提示，
/// 而 Compose 的语义里「没写」与「写了但被忽略」看起来一模一样。</para>
/// <para>因此本解析器是<b>白名单</b>式的：每个映射节点都会逐键核对，
/// 不在白名单里的键一律抛 <see cref="ErrorCodes.ComposeInvalid"/>（1904），
/// 并在消息里带上完整路径（如 <c>services.api.healthcheck</c>）——
/// 用户能据此直接定位到 YAML 里的那几行。</para>
/// <para><b>用 <see cref="YamlStream"/> 而不是反序列化到 POCO</b>：
/// 反序列化到强类型对象时，「这个键我认不认识」的信息在绑定阶段就丢了，
/// 只能拿到「反序列化失败」这种没有路径信息的错误。
/// 保留节点树才能自己走一遍结构、自己判断每个键。</para>
/// <para><b>本版本支持的字段</b>（服务级）：<c>image</c> / <c>environment</c> /
/// <c>command</c> / <c>ports</c> / <c>volumes</c> / <c>networks</c> /
/// <c>depends_on</c> / <c>restart</c> / <c>healthcheck</c>。
/// 顶层：<c>services</c> / <c>networks</c> / <c>volumes</c>，
/// 以及被忽略并给出提示的 <c>version</c> 与 <c>name</c>。</para>
/// <para><b>变量插值（<c>${VAR}</c>）显式拒绝</b>：它需要一套「变量从哪来」的约定
/// （进程环境 / .env 文件 / 平台级变量），那是 0.15.0 的能力。
/// 现在放行会得到「变量被替换成空串」的结果——那比重报一个错糟糕得多。</para>
/// </remarks>
internal static class ComposeParser
{
    /// <summary>顶层允许的键。</summary>
    private static readonly HashSet<string> _topLevelKeys = new(StringComparer.Ordinal)
    {
        "version",
        "name",
        "services",
        "networks",
        "volumes",
    };

    /// <summary>服务级允许的键。</summary>
    private static readonly HashSet<string> _serviceKeys = new(StringComparer.Ordinal)
    {
        "image",
        "environment",
        "command",
        "ports",
        "volumes",
        "networks",
        "depends_on",
        "restart",
        "healthcheck",
    };

    /// <summary>顶层网络允许的键。</summary>
    private static readonly HashSet<string> _networkKeys = new(StringComparer.Ordinal)
    {
        "name",
        "external",
    };

    /// <summary>顶层卷允许的键。</summary>
    private static readonly HashSet<string> _volumeKeys = new(StringComparer.Ordinal)
    {
        "name",
        "external",
    };

    /// <summary>健康检查允许的键。</summary>
    private static readonly HashSet<string> _healthCheckKeys = new(StringComparer.Ordinal)
    {
        "test",
        "interval",
        "timeout",
        "start_period",
        "retries",
        "disable",
    };

    /// <summary>Compose 支持的重启策略取值。</summary>
    private static readonly HashSet<string> _restartPolicies = new(StringComparer.Ordinal)
    {
        "no",
        "always",
        "on-failure",
        "unless-stopped",
    };

    /// <summary>
    /// 解析 Compose 文件。
    /// </summary>
    /// <param name="content">YAML 文本。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="BusinessException">
    /// YAML 语法错误或含暂不支持的字段（<c>1904</c>）。
    /// </exception>
    internal static ComposeDocument Parse(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(content));
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"Compose 文件不是合法的 YAML：{exception.Message}");
        }

        if (stream.Documents.Count == 0)
        {
            throw new BusinessException(ErrorCodes.ComposeInvalid, "Compose 文件是空的");
        }

        var root = stream.Documents[0].RootNode;

        if (root is not YamlMappingNode rootMapping)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                "Compose 文件的顶层必须是一个映射（键值对）");
        }

        var warnings = new List<string>();
        RejectUnknownKeys(rootMapping, string.Empty, _topLevelKeys);

        // version 已被 Compose 规范废弃；name 会被本平台的 Stack 名称取代。
        // 两者都忽略，但**给提示**——静默忽略会让人以为顶层 name 生效了。
        if (TryGetChild(rootMapping, "version", out var versionNode))
        {
            warnings.Add($"「version」字段已被 Compose 规范废弃（当前值：{versionNode}），已忽略");
        }

        if (TryGetChild(rootMapping, "name", out var nameNode))
        {
            warnings.Add($"顶层「name」字段不生效——项目名以 Stack 名称为准（当前值：{nameNode}）");
        }

        var services = ParseServices(rootMapping, warnings);
        var networks = ParseNetworks(rootMapping);
        var volumes = ParseVolumes(rootMapping);

        return new ComposeDocument(services, networks, volumes, warnings);
    }

    /// <summary>
    /// 解析 <c>services</c>。
    /// </summary>
    /// <param name="root">顶层映射。</param>
    /// <param name="warnings">提示收集器。</param>
    /// <returns>服务定义列表。</returns>
    private static List<ComposeService> ParseServices(
        YamlMappingNode root,
        List<string> warnings)
    {
        if (!TryGetChild(root, "services", out var servicesNode))
        {
            throw new BusinessException(ErrorCodes.ComposeInvalid, "Compose 文件缺少「services」段");
        }

        if (servicesNode is not YamlMappingNode servicesMapping)
        {
            throw new BusinessException(ErrorCodes.ComposeInvalid, "「services」必须是一个映射");
        }

        if (servicesMapping.Children.Count == 0)
        {
            throw new BusinessException(ErrorCodes.ComposeInvalid, "「services」里没有任何服务");
        }

        var services = new List<ComposeService>(servicesMapping.Children.Count);

        foreach (var pair in servicesMapping.Children)
        {
            var name = pair.Key.ToString();
            var path = $"services.{name}";

            if (pair.Value is not YamlMappingNode serviceMapping)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}」必须是一个映射（服务定义）");
            }

            services.Add(ParseService(name, serviceMapping, path));
        }

        // depends_on 指向的服务必须存在——这是能在部署前就发现的错误，
        // 等到启动顺序排不动才报错就晚了。
        foreach (var service in services)
        {
            foreach (var dependency in service.DependsOn)
            {
                if (services.All(candidate => !string.Equals(candidate.Name, dependency, StringComparison.Ordinal)))
                {
                    throw new BusinessException(
                        ErrorCodes.ComposeInvalid,
                        $"「services.{service.Name}.depends_on」引用了不存在的服务「{dependency}」");
                }
            }
        }

        return services;
    }

    /// <summary>
    /// 解析单个服务。
    /// </summary>
    /// <param name="name">服务名。</param>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>服务定义。</returns>
    private static ComposeService ParseService(string name, YamlMappingNode mapping, string path)
    {
        RejectUnknownKeys(mapping, path, _serviceKeys);

        if (!TryGetChild(mapping, "image", out var imageNode))
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}」缺少「image」字段（本版本不支持 build，只能显式指定镜像）");
        }

        var image = RequireScalar(imageNode, $"{path}.image").Trim();

        if (image.Length == 0)
        {
            throw new BusinessException(ErrorCodes.ComposeInvalid, $"「{path}.image」不能为空");
        }

        RejectInterpolation(image, $"{path}.image");

        return new ComposeService
        {
            Name = name,
            Image = image,
            Environment = ParseEnvironment(mapping, path),
            Command = ParseCommand(mapping, path),
            Ports = ParsePorts(mapping, path),
            Volumes = ParseVolumes(mapping, path),
            Networks = ParseServiceNetworks(mapping, path),
            DependsOn = ParseDependsOn(mapping, path),
            Restart = ParseRestart(mapping, path),
            HealthCheck = ParseHealthCheck(mapping, path),
        };
    }

    /// <summary>
    /// 解析服务的 <c>environment</c>。
    /// </summary>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns><c>KEY=VALUE</c> 列表。</returns>
    /// <remarks>
    /// <b>同时支持映射写法与列表写法</b>（两种都是 Compose 的标准写法）：
    /// 映射 <c>KEY: VALUE</c>、列表 <c>KEY=VALUE</c>。
    /// </remarks>
    private static List<string> ParseEnvironment(YamlMappingNode mapping, string path)
    {
        if (!TryGetChild(mapping, "environment", out var node))
        {
            return [];
        }

        var environment = new List<string>();

        if (node is YamlMappingNode environmentMapping)
        {
            foreach (var pair in environmentMapping.Children)
            {
                var key = pair.Key.ToString();

                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new BusinessException(
                        ErrorCodes.ComposeInvalid,
                        $"「{path}.environment」里有空的变量名");
                }

                // 值为 null 的写法（`KEY:`）在 Compose 里表示「从宿主环境取值」，
                // 而本版本没有环境变量来源，因此显式拒绝而不是让它变成空串。
                if (pair.Value is YamlScalarNode { Value: null or "" } && pair.Value is not YamlScalarNode)
                {
                    throw new BusinessException(
                        ErrorCodes.ComposeInvalid,
                        $"「{path}.environment.{key}」没有值——本版本暂不支持从宿主环境继承变量");
                }

                var value = pair.Value is YamlScalarNode scalar ? scalar.Value ?? string.Empty : pair.Value.ToString();

                RejectInterpolation(value, $"{path}.environment.{key}");

                environment.Add($"{key}={value}");
            }

            return environment;
        }

        if (node is YamlSequenceNode sequence)
        {
            foreach (var item in sequence.Children)
            {
                var text = RequireScalar(item, $"{path}.environment");
                RejectInterpolation(text, $"{path}.environment");

                if (!text.Contains('=', StringComparison.Ordinal))
                {
                    throw new BusinessException(
                        ErrorCodes.ComposeInvalid,
                        $"「{path}.environment」里的「{text}」没有值——本版本暂不支持从宿主环境继承变量");
                }

                environment.Add(text);
            }

            return environment;
        }

        throw new BusinessException(
            ErrorCodes.ComposeInvalid,
            $"「{path}.environment」必须是映射或列表");
    }

    /// <summary>
    /// 解析服务的 <c>command</c>。
    /// </summary>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>argv 列表；未配置时是空列表。</returns>
    /// <remarks>
    /// <b>字符串写法按 shell 规则拆分成 argv</b>——与 Compose 的行为一致
    /// （Compose 把字符串形式的 command 交给 shell，因此引号要生效）。
    /// </remarks>
    private static List<string> ParseCommand(YamlMappingNode mapping, string path)
    {
        if (!TryGetChild(mapping, "command", out var node))
        {
            return [];
        }

        if (node is YamlSequenceNode sequence)
        {
            var argv = new List<string>(sequence.Children.Count);

            foreach (var item in sequence.Children)
            {
                argv.Add(RequireScalar(item, $"{path}.command"));
            }

            return argv;
        }

        var text = RequireScalar(node, $"{path}.command");
        RejectInterpolation(text, $"{path}.command");

        return Shellwords.Split(text);
    }

    /// <summary>
    /// 解析服务的 <c>ports</c>（只支持短语法）。
    /// </summary>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>端口映射列表。</returns>
    private static List<ComposePortMapping> ParsePorts(YamlMappingNode mapping, string path)
    {
        if (!TryGetChild(mapping, "ports", out var node))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.ports」必须是列表");
        }

        var ports = new List<ComposePortMapping>(sequence.Children.Count);

        foreach (var item in sequence.Children)
        {
            if (item is YamlMappingNode)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}.ports」暂不支持长语法（映射写法），只支持「宿主机端口:容器端口」这样的短语法");
            }

            var text = RequireScalar(item, $"{path}.ports");
            RejectInterpolation(text, $"{path}.ports");

            ports.Add(ParsePort(text, $"{path}.ports"));
        }

        return ports;
    }

    /// <summary>
    /// 解析一条短语法端口。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="path">字段路径。</param>
    /// <returns>端口映射。</returns>
    /// <remarks>
    /// <b>短语法有四种形态</b>：<c>容器端口</c>、<c>宿主机端口:容器端口</c>、
    /// <c>绑定地址:宿主机端口:容器端口</c>，以及带 <c>/协议</c> 后缀的变体。
    /// </remarks>
    private static ComposePortMapping ParsePort(string text, string path)
    {
        var trimmed = text.Trim();
        var protocol = "tcp";

        var slashIndex = trimmed.LastIndexOf('/');

        if (slashIndex >= 0)
        {
            protocol = trimmed[(slashIndex + 1)..].Trim().ToLowerInvariant();
            trimmed = trimmed[..slashIndex].Trim();

            if (protocol is not ("tcp" or "udp"))
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}」里的协议「{protocol}」非法，只能是 tcp 或 udp");
            }
        }

        var parts = trimmed.Split(':', StringSplitOptions.TrimEntries);

        string? hostIp = null;
        int? hostPort = null;
        var containerPortText = parts[^1];

        if (parts.Length == 2)
        {
            hostPort = ParsePortNumber(parts[0], path, "宿主机端口");
        }
        else if (parts.Length == 3)
        {
            hostIp = parts[0].Length == 0 ? null : parts[0];
            hostPort = ParsePortNumber(parts[1], path, "宿主机端口");
        }
        else if (parts.Length > 3)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}」里的「{text}」格式非法（短语法最多三段：绑定地址:宿主机端口:容器端口）");
        }

        var containerPort = ParsePortNumber(containerPortText, path, "容器端口");

        return new ComposePortMapping(containerPort, hostPort, protocol, hostIp);
    }

    /// <summary>
    /// 解析端口号。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="path">字段路径。</param>
    /// <param name="role">角色描述（用于错误消息）。</param>
    /// <returns>端口号。</returns>
    private static int ParsePortNumber(string text, string path, string role)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}」里的{role}「{text}」非法（须在 1-65535 之间）");
        }

        return port;
    }

    /// <summary>
    /// 解析服务的 <c>volumes</c>（只支持短语法）。
    /// </summary>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>挂载列表。</returns>
    private static List<ComposeVolumeMount> ParseVolumes(YamlMappingNode mapping, string path)
    {
        if (!TryGetChild(mapping, "volumes", out var node))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.volumes」必须是列表");
        }

        var mounts = new List<ComposeVolumeMount>(sequence.Children.Count);

        foreach (var item in sequence.Children)
        {
            if (item is YamlMappingNode)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}.volumes」暂不支持长语法（映射写法），只支持「源:目标[:ro]」这样的短语法");
            }

            var text = RequireScalar(item, $"{path}.volumes");
            RejectInterpolation(text, $"{path}.volumes");

            mounts.Add(ParseMount(text, $"{path}.volumes"));
        }

        return mounts;
    }

    /// <summary>
    /// 解析一条短语法挂载。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <param name="path">字段路径。</param>
    /// <returns>挂载定义。</returns>
    /// <remarks>
    /// <b>源是路径还是具名卷，靠前缀判定</b>：以 <c>.</c> / <c>/</c> / <c>~</c> 开头的是路径，
    /// 其余按具名卷处理。Compose 的判定更精细（还会去看路径是否存在），
    /// 但我们面对的可能是<b>远程主机</b>——本机的路径存在性说明不了那台机器的情况，
    /// 因此用「看起来像路径」这个纯文本规则。</remarks>
    private static ComposeVolumeMount ParseMount(string text, string path)
    {
        var trimmed = text.Trim();
        var parts = trimmed.Split(':', StringSplitOptions.TrimEntries);

        if (parts.Length is < 2 or > 3)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}」里的「{text}」格式非法（短语法是「源:目标」或「源:目标:ro」）");
        }

        var source = parts[0];
        var destination = parts[1];
        var readOnly = false;

        if (parts.Length == 3)
        {
            if (!string.Equals(parts[2], "ro", StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}」里的「{text}」的第三段只能是 ro（只读）");
            }

            readOnly = true;
        }

        if (source.Length == 0 || destination.Length == 0)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}」里的「{text}」不完整（源与目标都不能为空）");
        }

        // 相对路径在远程主机上没有可靠的语义（「相对当前目录」指的是谁的当前目录？）。
        // 静默按绝对路径处理会挂到完全错误的位置，因此显式拒绝。
        if (source.StartsWith('.') || source.StartsWith('~'))
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}」里的「{source}」是相对路径——本版本只接受绝对路径或具名卷"
                + "（远程主机上没有可靠的「当前目录」语义）");
        }

        var isBind = source.StartsWith('/') || source.StartsWith('\\');

        return new ComposeVolumeMount(source, destination, readOnly, isBind);
    }

    /// <summary>
    /// 解析服务的 <c>networks</c>（只支持列表写法）。
    /// </summary>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>网络名列表。</returns>
    private static List<string> ParseServiceNetworks(YamlMappingNode mapping, string path)
    {
        if (!TryGetChild(mapping, "networks", out var node))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.networks」必须是列表（本版本暂不支持带 aliases 的映射写法）");
        }

        var networks = new List<string>(sequence.Children.Count);

        foreach (var item in sequence.Children)
        {
            if (item is YamlMappingNode)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}.networks」暂不支持映射写法（带 aliases 的形式），只支持网络名列表");
            }

            var name = RequireScalar(item, $"{path}.networks").Trim();

            if (name.Length == 0)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}.networks」里有空的网络名");
            }

            networks.Add(name);
        }

        return networks;
    }

    /// <summary>
    /// 解析服务的 <c>depends_on</c>（只支持列表写法）。
    /// </summary>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>依赖的服务名列表。</returns>
    private static List<string> ParseDependsOn(YamlMappingNode mapping, string path)
    {
        if (!TryGetChild(mapping, "depends_on", out var node))
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.depends_on」必须是列表（本版本暂不支持带 condition 的映射写法）");
        }

        var dependencies = new List<string>(sequence.Children.Count);

        foreach (var item in sequence.Children)
        {
            if (item is YamlMappingNode)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}.depends_on」暂不支持映射写法（带 condition 的形式），只支持服务名列表");
            }

            var name = RequireScalar(item, $"{path}.depends_on").Trim();

            if (name.Length == 0)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}.depends_on」里有空的服务名");
            }

            dependencies.Add(name);
        }

        return dependencies;
    }

    /// <summary>
    /// 解析服务的 <c>restart</c>。
    /// </summary>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>重启策略；未配置时为 <see langword="null"/>。</returns>
    private static string? ParseRestart(YamlMappingNode mapping, string path)
    {
        if (!TryGetChild(mapping, "restart", out var node))
        {
            return null;
        }

        var text = RequireScalar(node, $"{path}.restart").Trim().ToLowerInvariant();

        // on-failure:5 这类「带最大重试次数」的写法本版本不支持：
        // Docker 的重启策略本身没有「最多重试几次」这个概念，要实现得靠外部监督。
        if (text.StartsWith("on-failure:", StringComparison.Ordinal))
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.restart」的「{text}」暂不支持（带最大重试次数的写法）");
        }

        if (!_restartPolicies.Contains(text))
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.restart」的「{text}」非法，只能是 no / always / on-failure / unless-stopped");
        }

        return text;
    }

    /// <summary>
    /// 解析服务的 <c>healthcheck</c>。
    /// </summary>
    /// <param name="mapping">服务节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>健康检查定义；未配置时为 <see langword="null"/>。</returns>
    private static ComposeHealthCheck? ParseHealthCheck(YamlMappingNode mapping, string path)
    {
        if (!TryGetChild(mapping, "healthcheck", out var node))
        {
            return null;
        }

        if (node is not YamlMappingNode healthCheck)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.healthcheck」必须是一个映射");
        }

        var healthCheckPath = $"{path}.healthcheck";
        RejectUnknownKeys(healthCheck, healthCheckPath, _healthCheckKeys);

        // disable: true 是「关掉镜像里自带的健康检查」，等价于 test: ["NONE"]
        if (TryGetChild(healthCheck, "disable", out var disableNode))
        {
            var disabled = RequireScalar(disableNode, $"{healthCheckPath}.disable").Trim();

            if (string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetChild(healthCheck, "test", out _))
                {
                    throw new BusinessException(
                        ErrorCodes.ComposeInvalid,
                        $"「{healthCheckPath}」的 disable 与 test 不能同时出现");
                }

                return new ComposeHealthCheck(["NONE"], null, null, null, null);
            }

            if (!string.Equals(disabled, "false", StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{healthCheckPath}.disable」只能是 true 或 false");
            }
        }

        if (!TryGetChild(healthCheck, "test", out var testNode))
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{healthCheckPath}」缺少「test」字段（要关闭镜像自带的检查请写 disable: true）");
        }

        var test = ParseHealthCheckTest(testNode, healthCheckPath);
        var interval = ComposeDuration.Parse(
            ReadScalarOrNull(healthCheck, "interval"),
            $"{healthCheckPath}.interval");
        var timeout = ComposeDuration.Parse(
            ReadScalarOrNull(healthCheck, "timeout"),
            $"{healthCheckPath}.timeout");
        var startPeriod = ComposeDuration.Parse(
            ReadScalarOrNull(healthCheck, "start_period"),
            $"{healthCheckPath}.start_period");
        var retries = ParseRetries(healthCheck, healthCheckPath);

        return new ComposeHealthCheck(test, interval, timeout, startPeriod, retries);
    }

    /// <summary>
    /// 解析健康检查的 <c>test</c>。
    /// </summary>
    /// <param name="node">test 节点。</param>
    /// <param name="path">字段路径。</param>
    /// <returns>检查命令。</returns>
    /// <remarks>
    /// <b>字符串写法等价于 <c>CMD-SHELL</c></b>（与 Compose 一致）：
    /// <c>test: curl -f http://localhost/</c> 会交给 shell 执行。
    /// 数组写法则原样保留（<c>["CMD", "curl", ...]</c> / <c>["NONE"]</c>）。
    /// </remarks>
    private static List<string> ParseHealthCheckTest(YamlNode node, string path)
    {
        if (node is YamlSequenceNode sequence)
        {
            var test = new List<string>(sequence.Children.Count);

            foreach (var item in sequence.Children)
            {
                test.Add(RequireScalar(item, $"{path}.test"));
            }

            if (test.Count == 0)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}.test」不能为空（要关闭检查请写 [\"NONE\"]）");
            }

            return test;
        }

        var text = RequireScalar(node, $"{path}.test");
        RejectInterpolation(text, $"{path}.test");

        if (text.Length == 0)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.test」不能为空（要关闭检查请写 [\"NONE\"]）");
        }

        return ["CMD-SHELL", text];
    }

    /// <summary>
    /// 解析健康检查的 <c>retries</c>。
    /// </summary>
    /// <param name="healthCheck">健康检查节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>重试次数；未配置时为 <see langword="null"/>。</returns>
    private static int? ParseRetries(YamlMappingNode healthCheck, string path)
    {
        var text = ReadScalarOrNull(healthCheck, "retries");

        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var retries)
            || retries < 1)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}.retries」的「{text}」非法（须为不小于 1 的整数）");
        }

        return retries;
    }

    /// <summary>
    /// 解析顶层 <c>networks</c> 段。
    /// </summary>
    /// <param name="root">顶层映射。</param>
    /// <returns>网络定义列表。</returns>
    private static List<ComposeNetworkDefinition> ParseNetworks(YamlMappingNode root)
    {
        if (!TryGetChild(root, "networks", out var node))
        {
            return [];
        }

        if (node is not YamlMappingNode networks)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                "顶层「networks」必须是一个映射");
        }

        var definitions = new List<ComposeNetworkDefinition>(networks.Children.Count);

        foreach (var pair in networks.Children)
        {
            var name = pair.Key.ToString();
            var path = $"networks.{name}";

            if (pair.Value is YamlScalarNode { Value: null or "" })
            {
                definitions.Add(new ComposeNetworkDefinition(name, null, false));
                continue;
            }

            if (pair.Value is not YamlMappingNode networkMapping)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}」必须是一个映射或空值");
            }

            RejectUnknownKeys(networkMapping, path, _networkKeys);

            var actualName = ReadScalarOrNull(networkMapping, "name");
            var external = ParseExternal(networkMapping, path);

            definitions.Add(new ComposeNetworkDefinition(name, actualName, external));
        }

        return definitions;
    }

    /// <summary>
    /// 解析顶层 <c>volumes</c> 段。
    /// </summary>
    /// <param name="root">顶层映射。</param>
    /// <returns>卷定义列表。</returns>
    private static List<ComposeVolumeDefinition> ParseVolumes(YamlMappingNode root)
    {
        if (!TryGetChild(root, "volumes", out var node))
        {
            return [];
        }

        if (node is not YamlMappingNode volumes)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                "顶层「volumes」必须是一个映射");
        }

        var definitions = new List<ComposeVolumeDefinition>(volumes.Children.Count);

        foreach (var pair in volumes.Children)
        {
            var name = pair.Key.ToString();
            var path = $"volumes.{name}";

            if (pair.Value is YamlScalarNode { Value: null or "" })
            {
                definitions.Add(new ComposeVolumeDefinition(name, null, false));
                continue;
            }

            if (pair.Value is not YamlMappingNode volumeMapping)
            {
                throw new BusinessException(
                    ErrorCodes.ComposeInvalid,
                    $"「{path}」必须是一个映射或空值");
            }

            RejectUnknownKeys(volumeMapping, path, _volumeKeys);

            var actualName = ReadScalarOrNull(volumeMapping, "name");
            var external = ParseExternal(volumeMapping, path);

            definitions.Add(new ComposeVolumeDefinition(name, actualName, external));
        }

        return definitions;
    }

    /// <summary>
    /// 解析 <c>external</c> 标记。
    /// </summary>
    /// <param name="mapping">节点。</param>
    /// <param name="path">字段路径前缀。</param>
    /// <returns>是否为外部资源。</returns>
    private static bool ParseExternal(YamlMappingNode mapping, string path)
    {
        var text = ReadScalarOrNull(mapping, "external");

        if (text is null)
        {
            return false;
        }

        var trimmed = text.Trim();

        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new BusinessException(
            ErrorCodes.ComposeInvalid,
            $"「{path}.external」只能是 true 或 false");
    }

    /// <summary>
    /// 读取映射里的标量值。
    /// </summary>
    /// <param name="mapping">映射。</param>
    /// <param name="key">键名。</param>
    /// <returns>标量文本；键不存在时为 <see langword="null"/>。</returns>
    private static string? ReadScalarOrNull(YamlMappingNode mapping, string key) =>
        TryGetChild(mapping, key, out var node) ? RequireScalar(node, key) : null;

    /// <summary>
    /// 按标量键在映射里查找子节点。
    /// </summary>
    /// <param name="mapping">映射节点。</param>
    /// <param name="key">键名（区分大小写，与 YAML 一致）。</param>
    /// <param name="value">命中的子节点；未命中时为 <see langword="null"/>。</param>
    /// <returns>命中返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <b>为什么不直接用 <c>YamlMappingNode.TryGetValue</c></b>：那个方法的键类型是
    /// <see cref="YamlNode"/>，用它查找就得先造一个 <see cref="YamlScalarNode"/> 当键，
    /// 而 <see cref="YamlScalarNode"/> 的相等性比较的是<b>值</b>——
    /// 造出来的键与文件里的键能否相等取决于 YamlDotNet 的实现细节。
    /// 直接遍历 <c>Children</c> 并比较标量值是确定的行为。
    /// </remarks>
    private static bool TryGetChild(YamlMappingNode mapping, string key, out YamlNode value)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    /// <summary>
    /// 取出标量节点的文本。
    /// </summary>
    /// <param name="node">节点。</param>
    /// <param name="path">字段路径。</param>
    /// <returns>标量文本。</returns>
    /// <exception cref="BusinessException">节点不是标量（<c>1904</c>）。</exception>
    private static string RequireScalar(YamlNode node, string path)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node is not YamlScalarNode scalar)
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}」必须是一个值");
        }

        return scalar.Value ?? string.Empty;
    }

    /// <summary>
    /// 拒绝映射里的未知键。
    /// </summary>
    /// <param name="mapping">映射节点。</param>
    /// <param name="path">字段路径前缀（顶层为空串）。</param>
    /// <param name="allowed">允许的键集合。</param>
    /// <exception cref="BusinessException">存在未知键（<c>1904</c>）。</exception>
    private static void RejectUnknownKeys(
        YamlMappingNode mapping,
        string path,
        HashSet<string> allowed)
    {
        foreach (var pair in mapping.Children)
        {
            var key = pair.Key.ToString();

            if (allowed.Contains(key))
            {
                continue;
            }

            var fullPath = path.Length == 0 ? key : $"{path}.{key}";

            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{fullPath}」是本版本暂不支持的 Compose 字段——已明确报错而不是忽略，"
                + "以免「YAML 里写了但没生效」变成难以排查的故障");
        }
    }

    /// <summary>
    /// 拒绝含变量插值的文本。
    /// </summary>
    /// <param name="value">文本。</param>
    /// <param name="path">字段路径。</param>
    /// <exception cref="BusinessException">含插值（<c>1904</c>）。</exception>
    /// <remarks>
    /// <b>为什么现在不能支持</b>：变量插值需要一套「变量从哪来」的约定
    /// （进程环境 / .env 文件 / 平台级变量），那是 0.15.0 的能力。
    /// 现在放行会得到「变量被替换成空串」的结果——
    /// 端口映射会变成 <c>:80</c>、环境变量会变成 <c>KEY=</c>，
    /// 症状是「服务起来了但什么都不对」，比直接报错糟糕得多。
    /// </remarks>
    private static void RejectInterpolation(string value, string path)
    {
        if (value.Contains("${", StringComparison.Ordinal) || value.Contains('$', StringComparison.Ordinal))
        {
            throw new BusinessException(
                ErrorCodes.ComposeInvalid,
                $"「{path}」里含变量引用（${{...}}）——变量与密钥的注入排在 0.15.0，"
                + "当前版本会把它替换成空串，因此明确报错");
        }
    }
}
