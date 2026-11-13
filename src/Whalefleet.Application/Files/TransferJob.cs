namespace Whalefleet.Application.Files;

/// <summary>
/// 传输操作的类型。
/// </summary>
/// <remarks>
/// 用字符串常量而不是枚举：它会进任务快照、审计动作与前端 i18n，
/// 需要与「操作类型」这一外部可见的概念保持一致
/// （与 <c>ComposeOperations</c> 同一口径）。
/// </remarks>
public static class TransferOperations
{
    /// <summary>
    /// 导出镜像（<c>docker save</c>）。
    /// </summary>
    public const string ImageExport = "image.export";

    /// <summary>
    /// 导出容器文件系统（<c>docker export</c>）。
    /// </summary>
    public const string ContainerExport = "container.export";
}

/// <summary>
/// 传输任务的状态。
/// </summary>
public static class TransferJobStatus
{
    /// <summary>
    /// 已入队，尚未开始。
    /// </summary>
    public const string Queued = "queued";

    /// <summary>
    /// 执行中。
    /// </summary>
    public const string Running = "running";

    /// <summary>
    /// 已成功完成，产物可下载。
    /// </summary>
    public const string Succeeded = "succeeded";

    /// <summary>
    /// 已失败。
    /// </summary>
    public const string Failed = "failed";
}

/// <summary>
/// 一次传输任务（导出）的请求（入队后由后台工作线程消费）。
/// </summary>
/// <remarks>
/// <para><b>为什么把「操作者」一起塞进请求</b>：后台工作线程运行在<b>请求上下文之外</b>，
/// 那里的 <c>ICurrentUser</c> 是空的（没人登录）。
/// 若不把操作者带过来，审计记录里的 actor 会变成「anonymous」——
/// 而「把整个镜像下载走」恰恰是最该留痕的操作之一。
/// 因此在<b>请求期</b>捕获操作者信息，随请求一起传给后台。</para>
/// <para><b>为什么资源标识随请求带过去</b>：后台线程不重新解析资源，
/// 用的是请求期已经解析并校验过的标识——
/// 免得「解析一次、校验一次、执行时又按另一套规则解析一次」。</para>
/// </remarks>
/// <param name="JobId">任务标识（客户端生成，既是进度令牌也是查询句柄）。</param>
/// <param name="Operation">操作类型（见 <see cref="TransferOperations"/>）。</param>
/// <param name="HostId">所属主机名。</param>
/// <param name="ResourceId">资源标识（镜像 ID / 引用、容器 ID）。</param>
/// <param name="ResourceName">资源名称（审计用快照）。</param>
/// <param name="FileName">产物文件名（下载时呈现给用户）。</param>
/// <param name="ActorId">操作者主键；<see langword="null"/> 表示系统操作。</param>
/// <param name="ActorName">操作者名（快照）。</param>
/// <param name="ClientIp">来源 IP。</param>
public sealed record TransferJobRequest(
    string JobId,
    string Operation,
    string HostId,
    string ResourceId,
    string ResourceName,
    string FileName,
    Guid? ActorId,
    string? ActorName,
    string? ClientIp);

/// <summary>
/// 传输任务的快照（对外可见的状态）。
/// </summary>
/// <param name="JobId">任务标识。</param>
/// <param name="Operation">操作类型。</param>
/// <param name="HostId">所属主机名。</param>
/// <param name="ResourceId">资源标识。</param>
/// <param name="ResourceName">资源名称。</param>
/// <param name="Status">任务状态（见 <see cref="TransferJobStatus"/>）。</param>
/// <param name="Percent">总体进度百分比；无法确定时为 <see langword="null"/>。</param>
/// <param name="Step">当前步骤描述。</param>
/// <param name="ErrorCode">失败时的业务错误码。</param>
/// <param name="ErrorMessage">失败原因。</param>
/// <param name="DownloadUrl">成功后的下载地址（相对路径）。</param>
/// <param name="FileName">产物文件名。</param>
/// <param name="SizeBytes">产物大小（字节）。</param>
/// <param name="CreatedAt">创建时刻（UTC）。</param>
/// <param name="FinishedAt">结束时刻（UTC）。</param>
/// <param name="ExpiresAt">产物与票据的过期时刻（UTC）。</param>
public sealed record TransferJobSnapshot(
    string JobId,
    string Operation,
    string HostId,
    string ResourceId,
    string ResourceName,
    string Status,
    double? Percent,
    string? Step,
    int? ErrorCode,
    string? ErrorMessage,
    string? DownloadUrl,
    string? FileName,
    long? SizeBytes,
    DateTime CreatedAt,
    DateTime? FinishedAt,
    DateTime ExpiresAt);
