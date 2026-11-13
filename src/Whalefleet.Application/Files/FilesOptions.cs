namespace Whalefleet.Application.Files;

/// <summary>
/// 文件传输（导入 / 导出 / 浏览）相关的配置（配置节 <c>Files</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个配置节</b>：0.14.0 引入了「可配置的上传上限」「写入开关」
/// 「辅助镜像」这几项，它们都只服务于文件传输这一件事，
/// 混进 <c>Persistence</c> 或 <c>Docker</c> 会让那些节名不再表达自己的职责。</para>
/// <para><b>环境变量</b>：<c>FILES__ALLOWWRITE</c> / <c>FILES__MAXUPLOADBYTES</c> /
/// <c>FILES__TEMPdirectory</c> / <c>FILES__TICKETTTL</c> / <c>FILES__HELPERIMAGE</c> 等
/// （层级用 <c>__</c>）。</para>
/// <para><b>为什么默认值偏宽松</b>：<see cref="AllowWrite"/> 默认 <see langword="true"/>、
/// <see cref="MaxUploadBytes"/> 默认 20 GiB。这两项都可以由管理员在部署后调小，
/// 但默认关着写入会让「装好就能用」变成「装好还得先开权限」——
/// 而写入能力本身有完整的授权判定与审计兜底。</para>
/// </remarks>
public sealed class FilesOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Files";

    /// <summary>
    /// 是否允许<b>写入</b>（把文件上传进容器 / 卷）。
    /// </summary>
    /// <remarks>
    /// <b>只读浏览不受它影响</b>：关掉它之后目录浏览与文件下载仍然可用，
    /// 只是「上传」入口会被拒绝（返回 <c>1409</c>）。
    /// </remarks>
    public bool AllowWrite { get; set; } = true;

    /// <summary>
    /// 单次上传的体积上限（字节）。
    /// </summary>
    /// <remarks>
    /// 默认 20 GiB。服务端在<b>读流的过程中</b>逐字节计数，超限即中止——
    /// 读完整份再判大小的话，超限的请求已经把磁盘写满了。
    /// </remarks>
    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>
    /// 导出产物（临时 tar 文件）的存放目录。
    /// </summary>
    /// <remarks>
    /// 为空时按「数据库文件所在目录下的 <c>exports</c> 子目录」解析——
    /// 导出产物与数据库同处一个可写卷，容器部署时不必额外挂一个卷。
    /// </remarks>
    public string TempDirectory { get; set; } = String.Empty;

    /// <summary>
    /// 下载票据的有效期。
    /// </summary>
    /// <remarks>
    /// 默认 10 分钟。票据过期后临时文件会被清理；
    /// 导出本身可能跑几分钟，因此票据是在<b>导出完成之后</b>才签发的，
    /// 它只覆盖「用户点下载」这一小段时间。
    /// </remarks>
    public TimeSpan TicketTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 导出任务在登记表里的存活时长。
    /// </summary>
    /// <remarks>
    /// 默认 1 小时。导出产物（GB 级 tar）在磁盘上的保留时长与之相同。
    /// </remarks>
    public TimeSpan ExportTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 同时进行的导出任务数上限。
    /// </summary>
    /// <remarks>
    /// 导出是 IO 密集型（读守护进程 → 写磁盘），并发太多会互相抢带宽与磁盘。
    /// 默认 2：既能利用多盘 / 多网络，也足以让第二个用户不必干等。
    /// </remarks>
    public int MaxConcurrentExports { get; set; } = 2;

    /// <summary>
    /// 卷文件浏览使用的辅助镜像。
    /// </summary>
    /// <remarks>
    /// <para><b>它必须是一个「可以被创建成容器」的镜像</b>，但<b>永远不会被启动</b>——
    /// Docker 的 <c>/containers/{id}/archive</c> 端点在<b>已创建但未启动</b>的容器上就能工作
    /// （2026-11-13 实测），因此它不需要包含任何可执行文件（连 shell 都不需要）。
    /// 最小可用的辅助镜像是一个空 rootfs（约 5 kB）。</para>
    /// <para>带<b>版本标签</b>而不是 <c>latest</c>：辅助镜像的行为不能随上游漂移。</para>
    /// </remarks>
    public string HelperImage { get; set; } = "whalefleet/volfs-helper:0.14.0";

    /// <summary>
    /// 过期票据与过期导出产物的扫描间隔。
    /// </summary>
    /// <remarks>
    /// 默认 1 分钟。扫描是<b>兜底</b>：票据在解析时也会顺带判断是否过期，
    /// 因此这里不必频繁——它只负责「没人再碰的产物最终被删掉」。
    /// </remarks>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 目录浏览单次返回的最大条目数。
    /// </summary>
    /// <remarks>
    /// 默认 5000。目录里可能有几十万个文件，一次性返回会撑爆响应体，
    /// 而界面上的目录浏览本来就只看得到前面若干行。
    /// </remarks>
    public int MaxListEntries { get; set; } = 5000;
}
