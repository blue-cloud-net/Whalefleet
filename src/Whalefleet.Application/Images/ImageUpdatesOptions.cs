namespace Whalefleet.Application.Images;

/// <summary>
/// 镜像自动更新选项（配置节 <c>ImageUpdates</c>）。
/// </summary>
public sealed class ImageUpdatesOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "ImageUpdates";

    /// <summary>
    /// 是否启用后台扫描。
    /// </summary>
    /// <remarks>
    /// 关掉之后策略仍在、手动「立即检查」仍可用——这个开关只停后台轮询，
    /// 不停功能本身。
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 扫描周期（秒）。
    /// </summary>
    /// <remarks>
    /// 默认 1 小时：镜像标签的变化是天级事件，而每次扫描都要对每个策略打一次仓库
    /// 请求——周期太短会先把 Docker Hub 的匿名配额用光，然后所有策略一起变成「限流」。
    /// </remarks>
    public int ScanIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// 首轮扫描前的随机抖动上限（秒）。
    /// </summary>
    /// <remarks>
    /// <b>抖动不是可选项</b>：多实例部署时若都不抖动，所有实例会在启动的同一秒
    /// 一起把请求打向仓库；单实例下它也让「重启程序」不会固定地落在某个整点。
    /// </remarks>
    public int ScanJitterSeconds { get; set; } = 90;

    /// <summary>
    /// 单轮扫描最多检查多少个策略。
    /// </summary>
    /// <remarks>
    /// 上限用于兜住「策略被批量导入」的情形：一轮扫 500 个策略会让仓库把来源 IP
    /// 整个限流，而剩余策略下一轮自然会轮到。
    /// </remarks>
    public int MaxPoliciesPerScan { get; set; } = 50;

    /// <summary>
    /// Docker Hub 的镜像加速器地址（如 <c>https://docker.mirrors.example.com</c>）。
    /// </summary>
    /// <remarks>
    /// <para>只对 Docker Hub 生效。<b>守护进程自己也配了加速器</b>，但读取它的两条
    /// 路径都不可靠：读容器内的 <c>/etc/docker/daemon.json</c> 在「远程主机」场景下
    /// 根本读不到，而 <c>/info</c> 的 <c>RegistryConfig.Mirrors</c> 是守护进程的
    /// <b>启动时</b>快照。两条都对不上时，这里的配置就是唯一可控的入口。</para>
    /// <para>顺序即优先级：前面的拿不到摘要才会试后面的。</para>
    /// </remarks>
    public IList<string> RegistryMirrors { get; init; } = [];

    /// <summary>
    /// 配置是否合法。
    /// </summary>
    public bool IsValid() =>
        this.ScanIntervalSeconds > 0 &&
        this.ScanJitterSeconds >= 0 &&
        this.MaxPoliciesPerScan > 0;
}
