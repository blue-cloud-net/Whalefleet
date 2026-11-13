namespace Whalefleet.Application.Files;

/// <summary>
/// 导入镜像的结果。
/// </summary>
/// <param name="Images">导入（或已存在）的镜像引用列表。</param>
/// <param name="MessageCount">守护进程返回的消息条数（用于诊断「什么都没导入」的情况）。</param>
/// <remarks>
/// <b>为什么带上消息条数</b>：<c>docker load</c> 成功时也可能一条消息都不回
/// （镜像已存在且内容一致）。只给一个空列表的话，界面无法区分
/// 「导入成功但镜像本来就在」与「什么都没发生」——前者是正常的，后者需要人看一眼。
/// </remarks>
public sealed record ImageImportResult(IReadOnlyList<string> Images, int MessageCount);

/// <summary>
/// 导入容器文件系统的结果。
/// </summary>
/// <param name="ImageId">导入得到的镜像 ID。</param>
/// <param name="Repository">镜像仓库名（若指定）。</param>
/// <param name="Tag">镜像标签（若指定）。</param>
/// <param name="Warnings">守护进程给出的提示（不是错误）。</param>
public sealed record ContainerImportResult(
    string ImageId,
    string? Repository,
    string? Tag,
    IReadOnlyList<string> Warnings);
