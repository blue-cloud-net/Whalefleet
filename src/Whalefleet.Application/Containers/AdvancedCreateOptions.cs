using System.Globalization;
using Docker.DotNet.Models;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器创建请求里「高级参数」的校验与映射。
/// </summary>
/// <remarks>
/// <para><b>为什么单独成文件</b>：<c>ContainerService</c> 已经过千行，而这一块是纯函数
/// （给定请求片段，产出 SDK 片段），既不需要客户端也不需要日志。
/// 混在服务类里会让「哪些字段在本地被校验过」这件事淹在业务编排中间。</para>
/// <para><b>为什么每个字段都要自己校验</b>：守护进程对格式错误的处理是回一句
/// <c>invalid parameter</c>（或更糟：在<b>启动时</b>才失败）。本地校验的价值是给出
/// <b>指向具体字段</b>的中文消息，让界面能把提示落到对应输入框。</para>
/// <para><b>时间单位对外一律秒</b>：Docker 的 healthcheck 用纳秒，那是 SDK 与守护进程之间的
/// 实现细节；对外用秒可读，换算集中在这里。</para>
/// </remarks>
internal static class AdvancedCreateOptions
{
    /// <summary>纳秒换算系数（1 秒 = 10^9 纳秒）。</summary>
    private const long NanosPerSecond = 1_000_000_000L;

    /// <summary>设备权限里允许出现的字符。</summary>
    private const string AllowedDevicePermissions = "rwm";

    /// <summary>日志驱动默认类型。</summary>
    /// <remarks>与守护进程默认一致；显式写出来是为了让 <c>inspect</c> 回读时能对上，
    /// 而不是出现「请求里没这个字段但守护进程填了默认值」的差异。</remarks>
    private const string DefaultLogDriver = "json-file";

    /// <summary>关闭健康检查时 <see cref="HealthcheckConfig.Test"/> 的固定内容。</summary>
    private const string HealthCheckNone = "NONE";

    /// <summary>
    /// 构建 capability 增删列表。
    /// </summary>
    /// <param name="capAdd">新增的 capability。</param>
    /// <param name="capDrop">移除的 capability。</param>
    /// <returns>可直接赋给 <c>HostConfig.CapAdd</c> / <c>CapDrop</c> 的列表（无则 <see langword="null"/>）。</returns>
    /// <exception cref="BusinessException">同一次请求里既增又删同一项（<c>1000</c>）。</exception>
    /// <remarks>
    /// <b>为什么拒绝「既增又删同一项」</b>：Docker 不报错，两个列表都会发过去，
    /// 最终生效的是哪一个取决于 daemon 内部的合并顺序。这类请求几乎总是拼错了
    /// （比如从两份配置合并时重复了），在本地拒掉比让用户猜结果可靠。
    /// </remarks>
    internal static (List<string>? Add, List<string>? Drop) BuildCapabilities(
        IReadOnlyList<string>? capAdd,
        IReadOnlyList<string>? capDrop)
    {
        var add = NormalizeList(capAdd, "capAdd");
        var drop = NormalizeList(capDrop, "capDrop");

        if (add is not null && drop is not null)
        {
            var conflict = add.Intersect(drop, StringComparer.Ordinal).FirstOrDefault();

            if (conflict is not null)
            {
                throw BusinessException.Validation(
                    $"capability「{conflict}」不能同时出现在 capAdd 与 capDrop 里");
            }
        }

        return (add, drop);
    }

    /// <summary>
    /// 构建健康检查配置。
    /// </summary>
    /// <param name="check">请求里的健康检查。</param>
    /// <returns>SDK 的健康检查配置；<paramref name="check"/> 为 <see langword="null"/> 时返回 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">命令为空、时间越界或重试次数越界（<c>1000</c>）。</exception>
    /// <remarks>
    /// <b>为什么 <c>NONE</c> 不带其他字段</b>：Docker 见到 <c>["NONE"]</c> 就关闭检查，
    /// 此时再给 interval / retries 是没有意义的组合。放行会让用户以为自己配了宽限期，
    /// 实际上一条都不会执行。
    /// </remarks>
    internal static HealthcheckConfig? BuildHealthCheck(ContainerHealthCheck? check)
    {
        if (check is null)
        {
            return null;
        }

        var test = NormalizeList(check.Test, "healthCheck.test");

        if (test is null || test.Count == 0)
        {
            throw BusinessException.Validation("healthCheck.test 不能为空（关闭检查请传 [\"NONE\"]）");
        }

        var interval = RequirePositiveSeconds(check.IntervalSeconds, "healthCheck.intervalSeconds");
        var timeout = RequirePositiveSeconds(check.TimeoutSeconds, "healthCheck.timeoutSeconds");
        var startPeriod = RequirePositiveSeconds(check.StartPeriodSeconds, "healthCheck.startPeriodSeconds");

        // 守护进程对 retries=0 的解释是「永不判定失败」（一直 starting）。
        // 那是「不检查」而不是「很严格地检查」，因此 0 按非法处理。
        // 显式给类型：switch 的三个分支分别是 null / throw / long，
        // 不写出来编译器推不出 best common type（null 无类型信息）
        long? retries = check.Retries switch
        {
            null => null,
            // 用 Value 而不是直接 ToString：Nullable<int>.ToString(IFormatProvider) 不存在，
            // 而在 < 1 分支里已经把「非 null」这件事证明过了
            < 1 => throw BusinessException.Validation(
                $"healthCheck.retries 至少为 1（当前 {check.Retries.Value.ToString(CultureInfo.InvariantCulture)}）；0 的含义是「永不判定失败」"),
            _ => check.Retries.Value,
        };

        var disabled = test.Count == 1
            && string.Equals(test[0], HealthCheckNone, StringComparison.OrdinalIgnoreCase);

        if (disabled && (interval is not null || timeout is not null || startPeriod is not null || retries is not null))
        {
            throw BusinessException.Validation(
                "healthCheck.test 为 [\"NONE\"] 时不能再带 intervalSeconds / timeoutSeconds / startPeriodSeconds / retries");
        }

        // 顺序不能变：Docker 按 Test[0] 区分 CMD / CMD-SHELL / NONE，
        // 而 Test[0] 是 NONE 时其余元素会被忽略（因此上面先拒掉了「NONE + 其他字段」）。
        var command = new List<string> { test[0] };
        command.AddRange(test.Skip(1));

        return new HealthcheckConfig
        {
            Test = command,
            Interval = ToNanos(interval),
            Timeout = ToNanos(timeout),
            StartPeriod = ToNanos(startPeriod),
            Retries = retries,
        };
    }

    /// <summary>
    /// 构建设备映射。
    /// </summary>
    /// <param name="devices">请求里的设备列表。</param>
    /// <returns>SDK 的设备映射列表；无则 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">路径为空或权限字符非法（<c>1000</c>）。</exception>
    internal static IList<DeviceMapping>? BuildDevices(IReadOnlyList<ContainerDeviceMapping>? devices)
    {
        if (devices is not { Count: > 0 })
        {
            return null;
        }

        var mappings = new List<DeviceMapping>(devices.Count);

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.HostPath))
            {
                throw BusinessException.Validation("devices[].hostPath 不能为空");
            }

            var hostPath = device.HostPath.Trim();
            var containerPath = string.IsNullOrWhiteSpace(device.ContainerPath)
                ? hostPath
                : device.ContainerPath.Trim();

            mappings.Add(new DeviceMapping
            {
                PathOnHost = hostPath,
                PathInContainer = containerPath,
                // 空字符串会被当成「显式清空」而不是「用默认值」，因此这里显式补上 rwm。
                CgroupPermissions = NormalizeDevicePermissions(device.Permissions),
            });
        }

        return mappings;
    }

    /// <summary>
    /// 归一化设备权限。
    /// </summary>
    /// <param name="permissions">请求里的权限串。</param>
    /// <returns><c>r</c> / <c>w</c> / <c>m</c> 的去重组合；省略时为 <c>rwm</c>。</returns>
    /// <exception cref="BusinessException">含非法字符（<c>1000</c>）。</exception>
    internal static string NormalizeDevicePermissions(string? permissions)
    {
        if (string.IsNullOrWhiteSpace(permissions))
        {
            return AllowedDevicePermissions;
        }

        var trimmed = permissions.Trim();
        var seen = new HashSet<char>();

        foreach (var character in trimmed)
        {
            if (!AllowedDevicePermissions.Contains(character, StringComparison.Ordinal))
            {
                throw BusinessException.Validation(
                    $"devices[].permissions「{trimmed}」含非法字符「{character}」，只能是 r（读）/ w（写）/ m（mknod）的组合");
            }

            seen.Add(character);
        }

        if (seen.Count == 0)
        {
            throw BusinessException.Validation("devices[].permissions 不能是空串");
        }

        // 按固定顺序输出：rwm 顺序固定后，同一份配置重建两次得到的是同一个字符串，
        // 界面上不会出现「看起来不一样但其实等价」的差异。
        return string.Concat(AllowedDevicePermissions.Where(seen.Contains));
    }

    /// <summary>
    /// 构建内核参数字典。
    /// </summary>
    /// <param name="sysctls">请求里的 sysctl。</param>
    /// <returns>SDK 字典；无则 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">键或值为空（<c>1000</c>）。</exception>
    internal static IDictionary<string, string>? BuildSysctls(IReadOnlyDictionary<string, string>? sysctls)
    {
        if (sysctls is not { Count: > 0 })
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in sysctls)
        {
            var key = pair.Key?.Trim();

            if (string.IsNullOrEmpty(key))
            {
                throw BusinessException.Validation("sysctls 的键不能为空");
            }

            // 值允许为空串：net.ipv4.ip_forward= 是合法写法（置空等同于置默认值）
            if (pair.Value is null)
            {
                throw BusinessException.Validation($"sysctls[\"{key}\"] 的值不能为 null");
            }

            result[key] = pair.Value.Trim();
        }

        return result;
    }

    /// <summary>
    /// 构建 ulimit 列表。
    /// </summary>
    /// <param name="ulimits">请求里的 ulimit 列表。</param>
    /// <returns>SDK 的 ulimit 列表；无则 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">名称为空或软硬限制为负（<c>1000</c>）。</exception>
    /// <remarks><b>软硬缺一时互相补齐</b>：<c>docker run --ulimit nofile=1024</c> 会把它
    /// 同时设为软硬两个值，只设一个在这里会得到与命令行不同的结果。</remarks>
    internal static IList<Ulimit>? BuildUlimits(IReadOnlyList<ContainerUlimit>? ulimits)
    {
        if (ulimits is not { Count: > 0 })
        {
            return null;
        }

        var result = new List<Ulimit>(ulimits.Count);

        foreach (var ulimit in ulimits)
        {
            if (string.IsNullOrWhiteSpace(ulimit.Name))
            {
                throw BusinessException.Validation("ulimits[].name 不能为空");
            }

            RequireNonNegative(ulimit.Soft, "ulimits[].soft");
            RequireNonNegative(ulimit.Hard, "ulimits[].hard");

            var soft = ulimit.Soft ?? ulimit.Hard ?? 0;
            var hard = ulimit.Hard ?? ulimit.Soft ?? 0;

            result.Add(new Ulimit
            {
                Name = ulimit.Name.Trim(),
                Soft = soft,
                Hard = hard,
            });
        }

        return result;
    }

    /// <summary>
    /// 构建日志驱动配置。
    /// </summary>
    /// <param name="logDriver">请求里的日志驱动。</param>
    /// <returns>SDK 的日志配置；省略时返回类型为 <c>json-file</c>、无选项的配置。</returns>
    /// <exception cref="BusinessException">类型为空或选项键值为空（<c>1000</c>）。</exception>
    internal static LogConfig BuildLogDriver(ContainerLogDriver? logDriver)
    {
        if (logDriver is null)
        {
            // SDK 把 Config 声明成非空但它确实是可选的：传 null 会被序列化成「无选项」，
            // 而传空字典会被当成「显式配置了零个选项」——两者在 JSON 上不等价。
#pragma warning disable CS8625
            return new LogConfig { Type = DefaultLogDriver, Config = null };
#pragma warning restore CS8625
        }

        if (string.IsNullOrWhiteSpace(logDriver.Type))
        {
            throw BusinessException.Validation("logDriver.type 不能为空");
        }

        IDictionary<string, string>? options = null;

        if (logDriver.Options is { Count: > 0 })
        {
            var built = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var pair in logDriver.Options)
            {
                var key = pair.Key?.Trim();

                if (string.IsNullOrEmpty(key))
                {
                    throw BusinessException.Validation("logDriver.options 的键不能为空");
                }

                if (pair.Value is null)
                {
                    throw BusinessException.Validation($"logDriver.options[\"{key}\"] 的值不能为 null");
                }

                built[key] = pair.Value.Trim();
            }

            options = built;
        }

        // ⚠️ 不校验驱动名是否存在：那需要读宿主守护进程的插件注册表，
        // 而「本机没有 journald」这件事只有 daemon 知道。传了本机没有的驱动时，
        // 守护进程会在**启动容器时**才失败——这个坑无法在本层消掉，
        // 因此这里只做非空校验，其余如实交给守护进程。
#pragma warning disable CS8601
        return new LogConfig { Type = logDriver.Type.Trim(), Config = options };
#pragma warning restore CS8601
    }

    /// <summary>
    /// 归一化一个字符串列表（去空白、去空项、去重，保留首次出现的顺序）。
    /// </summary>
    /// <param name="values">原始列表。</param>
    /// <param name="field">字段名（用于拼错误消息）。</param>
    /// <returns>归一化后的列表；全为空项时返回 <see langword="null"/>。</returns>
    /// <exception cref="BusinessException">含有空白项（<c>1000</c>）。</exception>
    /// <remarks>
    /// <b>空白项为什么报错而不是跳过</b>：<c>capAdd: ["", "NET_ADMIN"]</c> 里那个空串几乎总是
    /// 前端把一个空输入框也提交了。静默跳过会让用户以为「我勾的那项生效了」，
    /// 而真正的问题（多了一个空输入框）永远不会被发现。
    /// </remarks>
    private static List<string>? NormalizeList(IReadOnlyList<string>? values, string field)
    {
        if (values is not { Count: > 0 })
        {
            return null;
        }

        var result = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            var trimmed = value?.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                throw BusinessException.Validation($"{field} 不能含有空项");
            }

            // 去重：重复项对 daemon 而言是同一项，保留一个即可，
            // 而重复通常来自调用方的拼装逻辑出错，静默容忍会让那个 bug 一直藏着。
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// 秒 → 纳秒。
    /// </summary>
    /// <param name="seconds">秒数。</param>
    /// <returns>纳秒数；<paramref name="seconds"/> 为 <see langword="null"/> 时返回 <see langword="null"/>。</returns>
    private static TimeSpan? ToNanos(long? seconds) =>
        seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null;

    /// <summary>
    /// 正整数秒校验。
    /// </summary>
    /// <param name="seconds">秒数。</param>
    /// <param name="field">字段名（线上写法）。</param>
    /// <returns>去空白后的秒数；<see langword="null"/> 表示未指定。</returns>
    /// <exception cref="BusinessException">小于 1 时抛 <c>1000</c>。</exception>
    /// <remarks>
    /// 0 与负数都按非法处理：<c>Interval=0</c> 在 Docker 里是「不等待，立即重复」，
    /// 那会把 CPU 跑满，而这不是任何人想要的结果。
    /// </remarks>
    private static long? RequirePositiveSeconds(int? seconds, string field) => seconds switch
    {
        null => null,
        < 1 => throw BusinessException.Validation(
            $"{field} 至少为 1 秒（当前 {seconds.Value.ToString(CultureInfo.InvariantCulture)}）"),
        _ => seconds.Value,
    };

    /// <summary>
    /// 非负整数校验。
    /// </summary>
    /// <param name="value">值。</param>
    /// <param name="field">字段名（线上写法）。</param>
    /// <exception cref="BusinessException">为负时抛 <c>1000</c>。</exception>
    private static void RequireNonNegative(long? value, string field)
    {
        if (value is < 0)
        {
            throw BusinessException.Validation(
                $"{field} 不能为负数（当前 {value.Value.ToString(CultureInfo.InvariantCulture)}）");
        }
    }

    /// <summary>
    /// 内存上限校验。
    /// </summary>
    /// <param name="memoryLimitBytes">字节数。</param>
    /// <returns>原值；<see langword="null"/> 表示不限制。</returns>
    /// <exception cref="BusinessException">为负或低于守护进程下限时抛 <c>1000</c>。</exception>
    /// <remarks>下界见 <see cref="MinMemoryLimitBytes"/>：低于它守护进程在<b>创建时</b>就回 400，
    /// 而 400 经翻译器落到 1402/500——「改一个数字就能成功」的事不该看起来像服务端故障。</remarks>
    internal static long? RequireMemoryLimit(long? memoryLimitBytes) => memoryLimitBytes switch
    {
        null => null,
        < 0 => throw BusinessException.Validation(
            $"memoryLimitBytes 不能为负数（当前 {memoryLimitBytes.Value.ToString(CultureInfo.InvariantCulture)}）；取消限制请传 0 或省略"),

        // 0 = 取消限制。必须排在最小值检查**之前**：否则「传 0 表示取消」会被当成
        // 「低于下限的非法值」而报错——而 0 在 Docker 那边恰恰是合法的「不限」。
        0 => 0,

        < MinMemoryLimitBytes => throw BusinessException.Validation(
            $"memoryLimitBytes 至少为 {MinMemoryLimitBytes} 字节（6 MiB，Docker 守护进程的硬性下限），当前 {memoryLimitBytes.Value.ToString(CultureInfo.InvariantCulture)}"),
        _ => memoryLimitBytes.Value,
    };

    /// <summary>内存下限：6 MiB。</summary>
    /// <remarks>
    /// ⚠️ <b>Docker 把这个值从 4 MiB 提到过 6 MiB</b>（实测 Docker 29.x：
    /// <c>6291455</c> 报「Minimum memory limit allowed is 6MB」，<c>6291456</c> 才过）。
    /// 这里取**两者中更严的 6 MiB</b>：取 4 MiB 会在新守护进程上被拒，
    /// 而取 6 MiB 在老守护进程上仍然合法（它只要求「不低于 4 MiB」）。
    /// 换句话说，取严的那个值对两种守护进程都成立。
    /// </remarks>
    private const long MinMemoryLimitBytes = 6L * 1024 * 1024;

    /// <summary>
    /// CPU 配额校验。
    /// </summary>
    /// <param name="nanoCpus">十亿分之一核。</param>
    /// <returns>原值；<see langword="null"/> 表示不限制。</returns>
    /// <exception cref="BusinessException">为负或超过上限时抛 <c>1000</c>。</exception>
    /// <remarks>上限 1024 核：再大也没有意义（宿主机物理核数就那么多），
    /// 而一个拼错的数字（比如把「毫核」当成「十亿分之一核」传了 2_000_000_000）会静默生效。</remarks>
    internal static long? RequireNanoCpus(long? nanoCpus) => nanoCpus switch
    {
        null => null,
        < 0 => throw BusinessException.Validation(
            $"nanoCpus 不能为负数（当前 {nanoCpus.Value.ToString(CultureInfo.InvariantCulture)}）；取消限制请传 0 或省略"),
        > MaxNanoCpus => throw BusinessException.Validation(
            $"nanoCpus 最多 {MaxNanoCpus}（1024 核 × 10^9），当前 {nanoCpus.Value.ToString(CultureInfo.InvariantCulture)}"),
        _ => nanoCpus.Value,
    };

    /// <summary>CPU 配额上限：1024 核（十亿分之一核为单位）。</summary>
    private const long MaxNanoCpus = 1024L * NanosPerSecond;

    /// <summary>
    /// 热更新时的内存上限校验（<b>与创建时不同：0 是非法的</b>）。
    /// </summary>
    /// <param name="memoryLimitBytes">字节数。</param>
    /// <returns>去空后的字节数；<see langword="null"/> 表示未指定（保持不变）。</returns>
    /// <exception cref="BusinessException">
    /// 为 0、为负或低于下限时抛 <c>1000</c>。
    /// </exception>
    /// <remarks>
    /// ⚠️ <b>为什么 0 在这里必须报错而不是当「取消限制」</b>（实测 Docker 29.x）：
    /// <c>docker update --memory=0</c> <b>返回 200，而容器上��限制根本没变</b>。
    /// 原因在守护进程侧——update 的处理逻辑是「字段非 0 才应用」，
    /// 于是 0 与「不传这个字段」走同一条路。
    /// <para>如果放行 0，用户会看到「保存成功」，而容器仍然跑在旧限制下——
    /// 而那正是这类限制本该避免的后果（以为已经放开，实际没有）。</para>
    /// <para>要真的取消限制只能重建容器，这里把这一点写在错误消息里而不是让用户去猜。</para>
    /// </remarks>
    internal static long? RequireUpdatableMemoryLimit(long? memoryLimitBytes) => memoryLimitBytes switch
    {
        null => null,
        0 => throw BusinessException.Validation(
            "memoryLimitBytes 不能为 0：Docker 的热更新把 0 当作「未指定」（实测会返回成功但不生效）。要取消内存上限必须重建容器"),
        _ => RequireMemoryLimit(memoryLimitBytes),
    };

    /// <summary>
    /// 热更新时的 CPU 配额校验（<b>与创建时不同：0 是非法的</b>）。
    /// </summary>
    /// <param name="nanoCpus">十亿分之一核。</param>
    /// <returns>原值；<see langword="null"/> 表示未指定（保持不变）。</returns>
    /// <exception cref="BusinessException">为 0、为负或超限时抛 <c>1000</c>。</exception>
    /// <remarks>与 <see cref="RequireUpdatableMemoryLimit"/> 同理：Docker 的 update 对
    /// <c>NanoCPUs=0</c> 也是「返回成功但不变」，因此 0 必须报错。</remarks>
    internal static long? RequireUpdatableNanoCpus(long? nanoCpus) => nanoCpus switch
    {
        null => null,
        0 => throw BusinessException.Validation(
            "nanoCpus 不能为 0：Docker 的热更新把 0 当作「未指定」（实测会返回成功但不生效）。要取消 CPU 限制必须重建容器"),
        _ => RequireNanoCpus(nanoCpus),
    };
}
