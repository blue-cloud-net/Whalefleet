using System.Globalization;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Microsoft.Extensions.Logging;

namespace Whalefleet.Application.Volumes;

/// <summary>
/// 数据卷用例（列表 / 详情 / 创建 / 删除），对应 <c>/api/v1/hosts/{hostId}/volumes/*</c>。
/// </summary>
/// <remarks>
/// <para><b>主机校验不在这里</b>：主机是否存在（<c>1300</c>）与可见性（<c>1501</c>）由
/// Api 层的 <c>CurrentHostMiddleware</c> 在进入本服务前完成。</para>
/// <para><b>外部响应按可空处理</b>：SDK 的 <c>VolumeResponse</c> 把字段声明为非空，
/// 但它们的值来自另一个进程的 JSON。<c>CreatedAt</c> 可能为空串，用量则要从
/// <c>GET /system/df</c> 另取（<c>GET /volumes</c> 不返回它）。所有映射都按可空写，
/// 缺失就退化成空值而不是崩掉——否则会变成 <c>9999</c>/500，而那明明只是「对端没给这个字段」。</para>
/// <para><b>日志纪律</b>：只记卷名与条数，<b>不</b>记 <c>Options</c>——
/// <c>local</c> 驱动的 <c>device</c> 常指向含业务含义的目录结构，
/// 而第三方驱动（如某些加密卷插件）的选项里可能含口令。</para>
/// </remarks>
public sealed class VolumeService
{
    /// <summary>列表默认每页条数。</summary>
    private const int DefaultPageSize = 20;

    /// <summary>列表每页条数上限（与容器 / 镜像一致）。</summary>
    private const int MaxPageSize = 100;

    /// <summary><c>GET /system/df</c> 的 <c>type</c> 过滤值：只要卷，跳过镜像层统计。</summary>
    private const string VolumeUsageType = "volume";

    /// <summary>「用量未知」：查询失败时的返回值。<b>不是</b>「用量为 0」。</summary>
    private static readonly IReadOnlyDictionary<string, UsageData> EmptyUsage =
        new Dictionary<string, UsageData>(0, StringComparer.Ordinal);

    private readonly IDockerHostAccessor _accessor;
    private readonly VolumeUsageCache _usageCache;
    private readonly ILogger<VolumeService> _logger;

    /// <summary>
    /// 初始化数据卷用例。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="usageCache">卷用量短时缓存（单例）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public VolumeService(
        IDockerHostAccessor accessor,
        VolumeUsageCache usageCache,
        ILogger<VolumeService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(usageCache);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _usageCache = usageCache;
        _logger = logger;
    }

    /// <summary>
    /// 列出主机上的数据卷（**服务端内存分页**，附带占用与引用数）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（分页）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前页数据、总数与守护进程给出的告警。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）、已停用（<c>1302</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>顺序沿用守护进程的返回顺序</b>（Docker 按名字升序返回）：
    /// 额外重排只会让「翻页时看到的东西变了」更难解释，而排序能力统一归 <c>0.16.0</c>。
    /// 排序若要做，必须发生在分页<b>之前</b>，否则 <c>total</c> 不变而每页内容错位。</para>
    /// <para><b>用量必须另查一次 <c>GET /system/df</c></b>：<c>GET /volumes</c> 的每一项里
    /// **没有 <c>UsageData</c>**（实测确认，不是版本问题），拿它算占用 / 引用数永远是 0。
    /// 带 <c>type=volume</c> 过滤后只统计卷，不会去扫镜像层。</para>
    /// <para><b>用量查询失败只降级、不整单失败</b>：列表本身是好的，为了两个附加字段
    /// 把「能看到卷名」变成 500 不划算；此时用量给 <c>null</c>，界面显示「—」。</para>
    /// </remarks>
    public async Task<VolumeListResult> ListAsync(
        string hostId,
        ListVolumesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var response = await DockerErrorTranslator.ExecuteAsync(
            () => client.Volumes.ListAsync(cancellationToken),
            "数据卷列表",
            cancellationToken).ConfigureAwait(false);

        var volumes = response.Volumes ?? [];
        var warnings = response.Warnings ?? [];

        var usage = await GetUsageAsync(client, hostId, cancellationToken).ConfigureAwait(false);

        var pageIndex = Math.Max(request.PageIndex ?? 0, 0);
        var pageSize = Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

        _logger.LogDebug(
            "主机 {HostId} 数据卷列表：共 {Total} 个（用量 {UsageCount} 项，告警 {WarningCount} 条），返回第 {PageIndex} 页（每页 {PageSize}）",
            hostId,
            volumes.Count,
            usage.Count,
            warnings.Count,
            pageIndex,
            pageSize);

        return new VolumeListResult(
            [.. volumes
                .Skip(pageIndex * pageSize)
                .Take(pageSize)
                .Select(volume => MapSummary(volume, usage.GetValueOrDefault(volume.Name ?? string.Empty)))],
            volumes.Count,
            pageIndex,
            pageSize,
            [.. warnings]);
    }

    /// <summary>
    /// 查询各卷的用量（占用字节数与引用它的容器数）。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="hostId">主机标识（仅用于日志）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>卷名 → 用量；查询失败时为空字典（表示「不知道」，与「0」不同）。</returns>
    /// <remarks>
    /// <para><b>为什么不是列表接口</b>：<c>GET /volumes</c> 返回的每一项里没有 <c>UsageData</c>，
    /// 用量只出现在 <c>GET /system/df</c>。本仓库早期按「列表接口在 1.42+ 会返回用量」
    /// 实现，结果真实环境里占用与引用数一直空着。</para>
    /// <para><b><c>type=volume</c> 过滤不可省</b>：不带过滤时守护进程会顺带计算镜像层大小，
    /// 在有几十 GB 镜像的机器上要等好几秒。</para>
    /// <para><b>结果走 30 秒缓存</b>（见 <see cref="VolumeUsageCache"/>）：拿一次就要几秒，
    /// 而翻页会反复调这个列表接口——不缓存的话分页带来的快会被它整个吃掉。</para>
    /// <para><b>只吞「查不到用量」这一类异常</b>：主机不可达、调用方取消都会照常上抛——
    /// 前者会让列表调用本身也失败，后者是请求已中止。失败<b>不写缓存</b>：
    /// 一次瞬时抖动不该变成半分钟的空白列。</para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, UsageData>> GetUsageAsync(
        IDockerClient client,
        string hostId,
        CancellationToken cancellationToken)
    {
        if (_usageCache.TryGet(hostId, out var cached))
        {
            return cached;
        }

        try
        {
            var response = await client.System.GetDataUsageInfoAsync(
                new SytemDataUsageInfoParameters { Type = [VolumeUsageType] },
                cancellationToken).ConfigureAwait(false);

            var items = response.VolumeUsage?.Items;

            if (items is null)
            {
                // 老版守护进程的 /system/df 只有旧的 Volumes 数组（无分组用量），
                // SDK 模型里没有那个属性 → 拿不到就是拿不到，不能当成「都是 0」。
                return EmptyUsage;
            }

            var usage = items
                .Where(item => item.Name is not null && item.UsageData is not null)
                .GroupBy(item => item.Name!, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Last().UsageData!,
                    StringComparer.Ordinal);

            _usageCache.Set(hostId, usage);

            return usage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DockerApiException
            or HttpRequestException
            or SocketException
            or IOException
            or TimeoutException)
        {
            _logger.LogWarning(
                exception,
                "主机 {HostId} 数据卷用量查询失败：占用与引用数将留空（列表本身仍返回）",
                hostId);

            return EmptyUsage;
        }
    }

    /// <summary>
    /// 查询数据卷详情。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">查询请求（卷名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>数据卷详情投影。</returns>
    /// <exception cref="BusinessException">
    /// 卷名为空或非法（<c>1000</c>）、卷不存在（<c>1401</c>）、主机不可达（<c>1301</c>）
    /// 或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    public async Task<VolumeDetail> InspectAsync(
        string hostId,
        InspectVolumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = DockerResourceName.Require(request.Name, "name", "数据卷");
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var volume = await DockerErrorTranslator.ExecuteAsync(
            () => client.Volumes.InspectAsync(name, cancellationToken),
            $"数据卷 {name}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("主机 {HostId} 查询数据卷 {Name} 详情", hostId, name);

        return MapDetail(volume);
    }

    /// <summary>
    /// 创建数据卷。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">创建请求（卷名、驱动、驱动选项、标签）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建出的数据卷列表项。</returns>
    /// <exception cref="BusinessException">
    /// 卷名为空或非法（<c>1000</c>）、主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>创建同名卷不会报错，而是直接返回已存在的那一个</b>——
    /// 这是 Docker 的既定行为，不是本项目的取舍。因此界面上「创建成功」后必须刷新列表，
    /// 否则用户会看到一个凭空出现的卷（其实早就存在）。</para>
    /// <para><b>返回创建结果而不是只回 200</b>：守护进程的创建响应里带完整卷对象，
    /// 直接映射成列表项，界面就能把它并进列表而不必再发一次请求。</para>
    /// </remarks>
    public async Task<VolumeSummary> CreateAsync(
        string hostId,
        CreateVolumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = DockerResourceName.Require(request.Name, "name", "数据卷");
        var driver = request.Driver?.Trim();
        var driverOptions = DockerResourceName.NormalizeLabels(request.DriverOptions);
        var labels = DockerResourceName.NormalizeLabels(request.Labels);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var volume = await DockerErrorTranslator.ExecuteAsync(
            () => client.Volumes.CreateAsync(
                new VolumesCreateParameters
                {
                    Name = name,

                    // 空串而不是 null：SDK 的 Driver 声明为非空可空类型，而 Docker 侧
                    // Go 的 encoding/json 把 `"Driver": null` 与「不写该字段」同样处理成零值，
                    // 因此两者对守护进程完全等价——用空串就不必抑制可空性检查。
                    Driver = driver ?? string.Empty,
                    DriverOpts = ToMutable(driverOptions),
                    Labels = ToMutable(labels),
                },
                cancellationToken),
            $"数据卷 {name}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostId} 创建数据卷 {Name}（驱动 {Driver}）",
            hostId,
            name,
            string.IsNullOrEmpty(driver) ? "默认" : driver);

        // 创建响应里没有用量字段：新建的卷要等下次列表（或 system df）才有大小与引用数。
        // 传 null 而不是 0，界面因此显示「—」而不是伪造的「0 B」。
        return MapSummary(volume, volume.UsageData);
    }

    /// <summary>
    /// 删除数据卷。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">删除请求（卷名、是否忽略不存在）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="BusinessException">
    /// 卷名为空或非法（<c>1000</c>）、卷不存在（<c>1401</c>）、仍被容器引用（<c>1404</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <para><b>不做多余的 inspect</b>：与网络删除不同，这里故意不预读一次。
    /// 列表里虽已带回 <c>RefCount</c>，但它只说明「有没有人引用」，
    /// 说清「是谁在引用」还得再查容器——而错误消息里 Docker 已经把容器 ID 带出来了。
    /// 多一次往返换来一句仍然不够用的提示，不值。</para>
    /// <para><b><c>force</c> 只能让「卷不存在」变成成功</b>，并不能删掉仍被引用的卷
    /// （已实测）。因此本服务不把它包装成「强制」语义，也不为它做额外处理——
    /// 直接透传给守护进程，让 409 原样冒成 <c>1404</c>。</para>
    /// </remarks>
    public async Task RemoveAsync(
        string hostId,
        RemoveVolumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var name = DockerResourceName.Require(request.Name, "name", "数据卷");
        var force = request.Force ?? false;

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        await this.RemoveAsync(client, name, force, hostId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量删除数据卷（逐条汇报失败，**部分成功也算成功**）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">批量删除请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>汇总结果。</returns>
    /// <exception cref="BusinessException">
    /// 主机不可达（<c>1301</c>）或已停用（<c>1302</c>）；列表为空 / 超限时 <c>1000</c>。
    /// </exception>
    /// <remarks>
    /// <para><b>与容器、镜像的批量删除同一套骨架</b>：客户端取一次、串行、逐条收失败。
    /// 逐条错误码与单条端点完全一致，界面复用同一套提示文案。</para>
    /// <para><b>为什么仍然串行</b>：删卷会更新容器的挂载引用，
    /// 并发时同一个卷可能被两个条目同时处理，失败原因将无法解释。</para>
    /// </remarks>
    public async Task<BatchOperationResult> BatchRemoveAsync(
        string hostId,
        BatchRemoveVolumesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var names = BatchRequest.NormalizeIds(request.Names, "names");
        var force = request.Force ?? false;

        // 放在循环外：主机不可达是整批的失败，逐条汇报「100 个都失败了」对用户毫无帮助
        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var failures = new List<BatchOperationFailure>();

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await this.RemoveAsync(client, name, force, hostId, cancellationToken).ConfigureAwait(false);
            }
            catch (BusinessException exception)
            {
                failures.Add(new BatchOperationFailure(name, exception.Code, exception.Message));
            }
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning(
                "主机 {HostId} 批量删除数据卷：成功 {Succeeded}/{Requested}，失败 {Failed} 条（强制={Force}）",
                hostId,
                names.Count - failures.Count,
                names.Count,
                failures.Count,
                force);
        }
        else
        {
            _logger.LogInformation(
                "主机 {HostId} 批量删除数据卷：全部 {Count} 个成功（强制={Force}）",
                hostId,
                names.Count,
                force);
        }

        return new BatchOperationResult(names.Count, names.Count - failures.Count, failures);
    }

    /// <summary>
    /// 用已有客户端删除单个数据卷（单条与批量端点共用）。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="name">卷名。</param>
    /// <param name="force">是否忽略「卷不存在」。</param>
    /// <param name="hostId">主机标识（仅用于日志）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// <para><b>幂等语义必须在这里自己实现</b>：<c>Docker.DotNet.Enhanced</c> 4.3.3 的
    /// <c>Volumes.RemoveAsync(name, force)</c> <b>完全忽略 <c>force</c> 参数</b>——
    /// 实测两次调用的 HTTP 请求行一模一样（<c>DELETE /volumes/{name}</c>，没有 <c>?force=</c>），
    /// 而守护进程在带 <c>?force=true</c> 时会回 204 而不是 404。
    /// 也就是说这个参数透传下去等于没传：删一个已不存在的卷照样报 404。
    /// <br/>
    /// 因此这里先探一次存在性，缺失就当作已删除。
    /// 多一次往返只在 <c>force=true</c> 时发生，而那正是「要保证重试安全」的场景，
    /// 值得。</para>
    /// <para><b><c>force=false</c> 时不做这次探测</b>：直接让删除自己报 404，
    /// 少一次往返，而且「不存在」这个结论来自删除操作本身而不是另一次查询。</para>
    /// </remarks>
    private async Task RemoveAsync(
        IDockerClient client,
        string name,
        bool force,
        string hostId,
        CancellationToken cancellationToken)
    {
        if (force && !await ExistsAsync(client, name, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "主机 {HostId} 删除数据卷 {Name}：该卷已不存在，按幂等视为成功",
                hostId,
                name);

            return;
        }

        await DockerErrorTranslator.ExecuteAsync(
            () => client.Volumes.RemoveAsync(name, force, cancellationToken),
            $"数据卷 {name}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("主机 {HostId} 删除数据卷 {Name}（忽略不存在={Force}）", hostId, name, force);
    }

    /// <summary>
    /// 判断数据卷是否存在。
    /// </summary>
    /// <param name="client">该主机的 Docker 客户端。</param>
    /// <param name="name">卷名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在返回 <see langword="true"/>；不存在返回 <see langword="false"/>。</returns>
    /// <exception cref="BusinessException">主机不可达（<c>1301</c>）等非「不存在」的故障。</exception>
    /// <remarks>
    /// <b>只有 404 被吞掉</b>：把「连不上守护进程」也当成「不存在」，
    /// 会让 <c>force=true</c> 在主机离线时静默返回成功——
    /// 用户以为删干净了，实际一个没删。
    /// </remarks>
    private static async Task<bool> ExistsAsync(
        IDockerClient client,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.Volumes.InspectAsync(name, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (DockerApiException exception) when (exception.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not BusinessException)
        {
            throw DockerErrorTranslator.TranslateException(exception, $"数据卷 {name}");
        }
    }

    /// <summary>
    /// 把守护进程返回的卷对象映射成列表项。
    /// </summary>
    /// <param name="volume">卷对象。</param>
    /// <param name="usage">该卷的用量；<see langword="null"/> 表示查不到（显示成「—」而不是 0）。</param>
    /// <returns>列表项。</returns>
    private static VolumeSummary MapSummary(VolumeResponse volume, UsageData? usage) =>
        new(
            volume.Name ?? string.Empty,
            volume.Driver ?? string.Empty,
            volume.Mountpoint ?? string.Empty,
            volume.Scope ?? string.Empty,
            ParseCreatedAt(volume.CreatedAt),
            usage?.Size,
            usage?.RefCount,
            Copy(volume.Labels));

    /// <summary>
    /// 把守护进程返回的卷对象映射成详情投影。
    /// </summary>
    /// <param name="volume">卷对象。</param>
    /// <returns>详情。</returns>
    private static VolumeDetail MapDetail(VolumeResponse volume) =>
        new(
            volume.Name ?? string.Empty,
            volume.Driver ?? string.Empty,
            volume.Mountpoint ?? string.Empty,
            volume.Scope ?? string.Empty,
            ParseCreatedAt(volume.CreatedAt),
            Copy(volume.Options),
            Copy(volume.Labels));

    /// <summary>
    /// 解析守护进程给出的创建时间。
    /// </summary>
    /// <param name="createdAt">RFC 3339 时间串（如 <c>2026-01-02T03:04:05.123456789Z</c>）。</param>
    /// <returns>解析结果；缺失或格式不认识时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>为什么不用 <c>DateTimeOffset.Parse</c> 直接调</b>：守护进程输出的是 9 位纳秒精度，
    /// 比 .NET 支持的 7 位 tick 多。直接解析在部分运行时会抛
    /// <see cref="FormatException"/>——「时间戳看不懂」显然不该让整个列表 500。
    /// </remarks>
    private static DateTimeOffset? ParseCreatedAt(string? createdAt)
    {
        if (string.IsNullOrWhiteSpace(createdAt))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            createdAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 把守护进程返回的可空字典复制成不可空的只读字典。
    /// </summary>
    /// <param name="source">源字典。</param>
    /// <returns>非 <see langword="null"/> 的字典（内部为可变副本，类型上仍是只读视图）。</returns>
    /// <remarks>
    /// 用 <c>StringComparer.Ordinal</c>：标签键是大小写敏感的，
    /// 用默认比较器会让 <c>env</c> 与 <c>ENV</c> 在字典里撞成同一个键。
    /// </remarks>
    private static Dictionary<string, string> Copy(IDictionary<string, string>? source) =>
        source is { Count: > 0 }
            ? new Dictionary<string, string>(source, StringComparer.Ordinal)
            : EmptyMap;

    /// <summary>
    /// 把只读字典转成 SDK 参数需要的可变字典（不可变字典没有实现该接口的具体类型）。
    /// </summary>
    /// <param name="source">源字典。</param>
    /// <returns>空字典或内容的副本。</returns>
    /// <remarks>
    /// <b>必须复制而不是直接转换</b>：SDK 的参数对象会被持有到请求发出，
    /// 直接塞入共享的 <see cref="EmptyMap"/> 会让任何一次调用都可能改到别人的数据。
    /// </remarks>
    private static Dictionary<string, string> ToMutable(IReadOnlyDictionary<string, string> source) =>
        source.Count > 0
            ? new Dictionary<string, string>(source, StringComparer.Ordinal)
            : EmptyMap;

    /// <summary>空字典的共享实例。</summary>
    private static readonly Dictionary<string, string> EmptyMap = new(StringComparer.Ordinal);
}
