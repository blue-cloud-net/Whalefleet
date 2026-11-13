using Whalefleet.Core.Common.Auditing;

namespace Whalefleet.Core.Images;

/// <summary>
/// 镜像自动更新策略（「主机 + 镜像引用」一条）。
/// </summary>
/// <remarks>
/// <para><b>为什么绑标签而不是绑镜像 ID</b>：要跟踪的正是「这个标签背后的摘要变了」。
/// 绑 ID 等于绑了一个不可变的快照，永远查不出更新。</para>
/// <para><b>基线摘要（<see cref="BaselineDigest"/>）与远端摘要分开存</b>：
/// 远端摘要是「仓库现在是什么」，基线是「我们上次确认过的那个」。
/// 两者相等才算「已是最新」——只看远端摘要的话，每次扫描都会重新报一次更新，
/// 因为没有任何东西记住「这一版已经看过了」。</para>
/// <para><b>首次检查只建立基线，不发通知</b>：刚加上的策略如果立刻报「有更新」，
/// 用户会被一堆本来就落后的镜像淹没，而他还没做出任何决定。
/// 首次检查把当前远端摘要记为基线，之后的变化才值得提醒。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class ImageUpdatePolicy : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 镜像引用最大长度（字符数）。
    /// </summary>
    public const int MaxReferenceLength = 255;

    /// <summary>
    /// 状态说明最大长度（字符数）。
    /// </summary>
    public const int MaxMessageLength = 500;

    /// <summary>
    /// 镜像引用（<c>仓库:标签</c>，标签缺省为 <c>latest</c>）。
    /// </summary>
    public string ImageReference { get; private set; } = default!;

    /// <summary>
    /// 所属主机主键（<c>hosts.id</c>）。
    /// </summary>
    /// <remarks>
    /// 存主键而不是主机名：主机名可改，而策略与主机的从属关系不该因改名断掉。
    /// </remarks>
    public Guid HostId { get; private set; }

    /// <summary>
    /// 执行模式。
    /// </summary>
    public ImageUpdateMode Mode { get; private set; }

    /// <summary>
    /// 是否启用（停用后不参与扫描，但保留历史结论）。
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// 基线摘要（上次确认过的远端摘要）；<see langword="null"/> 表示尚未建立基线。
    /// </summary>
    public string? BaselineDigest { get; private set; }

    /// <summary>
    /// 最近一次探测到的远端摘要；<see langword="null"/> 表示尚未探测成功。
    /// </summary>
    public string? RemoteDigest { get; private set; }

    /// <summary>
    /// 最近一次检查的结论。
    /// </summary>
    public ImageUpdateState State { get; private set; }

    /// <summary>
    /// 结论说明（失败原因 / 已应用的摘要）；可为空。
    /// </summary>
    public string? LastMessage { get; private set; }

    /// <summary>
    /// 最近一次检查时间（UTC）；<see langword="null"/> 表示从未检查。
    /// </summary>
    public DateTime? LastCheckedAt { get; private set; }

    /// <summary>
    /// 最近一次自动应用成功的时间（UTC）；<see langword="null"/> 表示从未应用。
    /// </summary>
    public DateTime? LastAppliedAt { get; private set; }

    /// <summary>
    /// 最近一次「已经为此摘要发过通知」的摘要。
    /// </summary>
    /// <remarks>
    /// <b>去重靠它而不是靠 <see cref="State"/></b>：扫描每隔几分钟就跑一次，
    /// 只看状态的话同一版更新会被反复提醒，铃铛里会堆满同一条消息。
    /// </remarks>
    public string? LastNotifiedDigest { get; private set; }

    /// <summary>
    /// 是否已经建立基线。
    /// </summary>
    public bool HasBaseline => this.BaselineDigest is not null;

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private ImageUpdatePolicy()
    {
    }

    /// <summary>
    /// 新建策略。
    /// </summary>
    /// <param name="id">主键。</param>
    /// <param name="hostId">所属主机主键。</param>
    /// <param name="reference">镜像引用（<c>仓库:标签</c>）。</param>
    /// <param name="mode">执行模式。</param>
    /// <param name="enabled">是否启用。</param>
    /// <returns>新建的策略实体。</returns>
    /// <exception cref="ArgumentException">主机主键为空或镜像引用非法。</exception>
    public static ImageUpdatePolicy Create(
        Guid id,
        Guid hostId,
        string reference,
        ImageUpdateMode mode = ImageUpdateMode.Notify,
        bool enabled = true)
    {
        if (hostId == Guid.Empty)
        {
            throw new ArgumentException("主机主键不能为空", nameof(hostId));
        }

        return new ImageUpdatePolicy
        {
            Id = id,
            HostId = hostId,
            ImageReference = NormalizeReference(reference),
            Mode = mode,
            Enabled = enabled,
            State = ImageUpdateState.Unknown,
        };
    }

    /// <summary>
    /// 修改执行模式。
    /// </summary>
    /// <param name="mode">新的执行模式。</param>
    public void SetMode(ImageUpdateMode mode) => this.Mode = mode;

    /// <summary>
    /// 启用或停用。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    public void SetEnabled(bool enabled) => this.Enabled = enabled;

    /// <summary>
    /// 记录一次探测结论。
    /// </summary>
    /// <param name="checkedAt">检查时间（UTC）。</param>
    /// <param name="remoteDigest">探测到的远端摘要（形如 <c>sha256:…</c>）。</param>
    /// <param name="localDigest">本地已缓存镜像的摘要；本地没有该镜像时为
    /// <see langword="null"/>。</param>
    /// <param name="message">附加说明；可为空。</param>
    /// <exception cref="ArgumentException">远端摘要为空。</exception>
    /// <remarks>
    /// <para><b>首次检查只建立基线</b>：此时把远端摘要同时写进
    /// <see cref="BaselineDigest"/>，结论记为 <see cref="ImageUpdateState.UpToDate"/>
    /// ——刚加上的策略不该立刻宣称「有更新」。</para>
    /// <para><b>之后按基线比对</b>：远端摘要与基线不同才算
    /// <see cref="ImageUpdateState.UpdateAvailable"/>。
    /// 用基线而不是本地摘要比对，是因为「本地镜像」可能已经被人手工
    /// <c>docker pull</c> 过——那时本地是最新的，而用户上次确认过的基线仍旧是旧的，
    /// 该不该再提醒应由「用户看过哪一版」决定。</para>
    /// </remarks>
    public void RecordProbe(
        DateTime checkedAt,
        string remoteDigest,
        string? localDigest,
        string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDigest);

        this.LastCheckedAt = checkedAt;
        this.RemoteDigest = remoteDigest.Trim();
        this.LastMessage = NormalizeMessage(message);

        if (!this.HasBaseline)
        {
            this.BaselineDigest = this.RemoteDigest;
            this.State = ImageUpdateState.UpToDate;

            return;
        }

        // 本地已经跟上远端（例如有人手工拉过，或自动应用刚完成）时把基线推上去，
        // 否则界面会一直显示「有更新」。
        if (localDigest is not null
            && String.Equals(localDigest.Trim(), this.RemoteDigest, StringComparison.Ordinal)
            && !String.Equals(this.RemoteDigest, this.BaselineDigest, StringComparison.Ordinal))
        {
            this.BaselineDigest = this.RemoteDigest;
        }

        this.State = String.Equals(this.BaselineDigest, this.RemoteDigest, StringComparison.Ordinal)
            ? ImageUpdateState.UpToDate
            : ImageUpdateState.UpdateAvailable;
    }

    /// <summary>
    /// 记录一次检查失败。
    /// </summary>
    /// <param name="checkedAt">检查时间（UTC）。</param>
    /// <param name="message">失败原因（仓库不可达 / 需要授权 / 限流等）。</param>
    /// <exception cref="ArgumentException">原因为空。</exception>
    /// <remarks>
    /// <b>不覆盖 <see cref="BaselineDigest"/></b>：一次网络抖动不该让用户
    /// 「确认过的版本」被清空，那会让下一次成功的检查重新走一遍首次基线逻辑，
    /// 于是真的更新被静默吞掉。
    /// </remarks>
    public void RecordFailure(DateTime checkedAt, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        this.LastCheckedAt = checkedAt;
        this.State = ImageUpdateState.Failed;
        this.LastMessage = NormalizeMessage(message);
    }

    /// <summary>
    /// 记录一次自动应用成功。
    /// </summary>
    /// <param name="appliedAt">应用时间（UTC）。</param>
    /// <param name="digest">已应用的摘要。</param>
    /// <param name="message">附加说明（如重建了几个容器）；可为空。</param>
    /// <exception cref="ArgumentException">摘要为空。</exception>
    public void RecordApplied(DateTime appliedAt, string digest, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);

        this.LastAppliedAt = appliedAt;
        this.BaselineDigest = digest.Trim();
        this.RemoteDigest = digest.Trim();
        this.State = ImageUpdateState.Applied;
        this.LastMessage = NormalizeMessage(message);
    }

    /// <summary>
    /// 判断是否应该为某个远端摘要发通知。
    /// </summary>
    /// <param name="remoteDigest">远端摘要。</param>
    /// <returns>该摘要尚未通知过时返回 <see langword="true"/>。</returns>
    public bool ShouldNotify(string remoteDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDigest);

        var digest = remoteDigest.Trim();

        return this.Enabled
            && this.HasBaseline
            && this.State == ImageUpdateState.UpdateAvailable
            && String.Equals(this.RemoteDigest, digest, StringComparison.Ordinal)
            && !String.Equals(this.LastNotifiedDigest, digest, StringComparison.Ordinal);
    }

    /// <summary>
    /// 记下「已为某个摘要发过通知」。
    /// </summary>
    /// <param name="remoteDigest">远端摘要。</param>
    /// <exception cref="ArgumentException">摘要为空。</exception>
    public void MarkNotified(string remoteDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDigest);

        this.LastNotifiedDigest = remoteDigest.Trim();
    }

    /// <summary>
    /// 规范化镜像引用：补齐 <c>latest</c> 标签。
    /// </summary>
    /// <param name="reference">原始引用。</param>
    /// <returns>规范化后的引用。</returns>
    /// <exception cref="ArgumentException">引用为空、含空白或过长。</exception>
    /// <remarks>
    /// <b>判据与 Docker 一致</b>：冒号在<b>最后一个斜杠之后</b>才算标签，
    /// 因此 <c>harbor.local:5000/app</c> 的 <c>:5000</c> 是端口而不是标签。
    /// </remarks>
    private static string NormalizeReference(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var trimmed = reference.Trim();

        if (trimmed.Length > MaxReferenceLength)
        {
            throw new ArgumentException($"镜像引用不得超过 {MaxReferenceLength} 个字符", nameof(reference));
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("镜像引用不能含空白字符", nameof(reference));
        }

        // 摘要引用（repo@sha256:…）与标签引用是两条互斥的跟踪路径：
        // 摘要永不变化，跟踪它等于永远「已是最新」，属于误配置。
        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException("请填写标签引用（仓库:标签）而不是摘要引用", nameof(reference));
        }

        var colon = trimmed.LastIndexOf(':');

        return colon <= trimmed.LastIndexOf('/')
            ? $"{trimmed}:latest"
            : trimmed;
    }

    /// <summary>
    /// 规范化说明文本：空串视为「无说明」，超长则截断。
    /// </summary>
    /// <param name="message">说明。</param>
    /// <returns>规范化后的说明或 <see langword="null"/>。</returns>
    private static string? NormalizeMessage(string? message)
    {
        if (String.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();

        return trimmed.Length <= MaxMessageLength
            ? trimmed
            : trimmed[..MaxMessageLength];
    }
}
