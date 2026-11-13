namespace Whalefleet.Application.Files;

/// <summary>
/// 发起一次导出的请求。
/// </summary>
public sealed record StartExportRequest
{
    /// <summary>
    /// 资源标识（镜像 ID / 引用、容器 ID）。
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// 进度令牌（客户端生成）；用于实时推送导出进度。
    /// </summary>
    /// <remarks>
    /// 省略时仍可导出，只是没有实时进度——客户端可以靠轮询状态接口拿到进度。
    /// </remarks>
    public string? ProgressToken { get; init; }
}

/// <summary>
/// 查询导出任务状态的请求。
/// </summary>
public sealed record TransferJobStatusRequest
{
    /// <summary>
    /// 任务标识（发起导出时返回的那个）。
    /// </summary>
    public string? JobId { get; init; }
}

/// <summary>
/// 发起一次导出后的响应。
/// </summary>
/// <param name="JobId">任务标识（用它查进度）。</param>
/// <param name="Status">任务状态（通常是 <c>queued</c>）。</param>
/// <param name="Reused">是否复用了<b>已在进行</b>的同资源导出任务。</param>
/// <remarks>
/// <para><b>为什么导入 tar 要单独走一个端点而不是复用这一个</b>：导入的输入是
/// <b>请求体</b>（GB 级字节流），而导出的输入是<b>一小段 JSON</b>。
/// 两者形态不同，合成一个端点会让「请求体是什么」变成运行时才知道的事。</para>
/// </remarks>
public sealed record StartExportResult(string JobId, string Status, bool Reused);

/// <summary>
/// 导出任务的状态响应。
/// </summary>
/// <param name="JobId">任务标识。</param>
/// <param name="Operation">操作类型。</param>
/// <param name="Status">任务状态。</param>
/// <param name="Percent">总体进度百分比；无法确定时为 <see langword="null"/>。</param>
/// <param name="Step">当前步骤描述。</param>
/// <param name="ErrorCode">失败时的业务错误码。</param>
/// <param name="ErrorMessage">失败原因。</param>
/// <param name="DownloadUrl">成功后的下载地址。</param>
/// <param name="FileName">产物文件名。</param>
/// <param name="SizeBytes">产物大小（字节）。</param>
/// <param name="ExpiresAt">产物与票据的过期时刻。</param>
public sealed record TransferJobStatusResult(
    string JobId,
    string Operation,
    string Status,
    double? Percent,
    string? Step,
    int? ErrorCode,
    string? ErrorMessage,
    string? DownloadUrl,
    string? FileName,
    long? SizeBytes,
    DateTime ExpiresAt);

/// <summary>
/// 提交容器为镜像的请求。
/// </summary>
public sealed record CommitContainerRequest
{
    /// <summary>
    /// 容器标识。
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// 镜像仓库名（如 <c>myapp</c>）。
    /// </summary>
    public string? Repository { get; init; }

    /// <summary>
    /// 镜像标签（省略时为 <c>latest</c>）。
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// 提交说明。
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// 作者。
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// 提交前是否暂停容器。
    /// </summary>
    /// <remarks>
    /// 暂停能让文件状态更一致（不会拍到写了一半的文件），
    /// 代价是提交期间容器不可用。默认暂停。
    /// </remarks>
    public bool? Pause { get; init; }

    /// <summary>
    /// 额外的 Dockerfile 指令（如 <c>ENV KEY=value</c>）。
    /// </summary>
    public IReadOnlyList<string>? Changes { get; init; }
}

/// <summary>
/// 提交容器为镜像的结果。
/// </summary>
/// <param name="ImageId">提交得到的镜像 ID。</param>
/// <param name="Repository">镜像仓库名。</param>
/// <param name="Tag">镜像标签。</param>
public sealed record CommitContainerResult(string ImageId, string Repository, string Tag);
