namespace Whalefleet.Application.Files;

/// <summary>
/// 一次传输的进度回报。
/// </summary>
/// <param name="Percent">总体进度百分比（0–100）；无法确定时为 <see langword="null"/>。</param>
/// <param name="Step">当前步骤描述（可直接展示给用户）。</param>
/// <remarks>
/// <b>为什么百分比可空</b>：导出时守护进程不给总长度
/// （<c>docker save</c> / <c>docker export</c> 都是流式的，没有 Content-Length），
/// 因此算不出总体百分比。这种时候必须显式说「不知道」，
/// 而不能给 0（那是「确实是 0」）或 50（那是编造的数字）。
/// </remarks>
public sealed record TransferProgress(double? Percent, string Step);

/// <summary>
/// 一次传输的产物。
/// </summary>
/// <param name="FilePath">产物文件的绝对路径。</param>
/// <param name="FileName">下载时呈现给用户的文件名。</param>
/// <param name="SizeBytes">产物大小（字节）。</param>
public sealed record TransferArtifact(string FilePath, string FileName, long SizeBytes);
