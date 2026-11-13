using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Whalefleet.Application.Files;

/// <summary>
/// 传输任务队列：把「要导出的东西」交给后台工作线程。
/// </summary>
/// <remarks>
/// <para><b>为什么用队列而不是直接在请求里做</b>：导出一个 GB 级镜像要读完整的守护进程数据流
/// 再写到磁盘，可能好几分钟；而 HTTP 请求抗不住那么长——
/// 中间的反代会把连接掐掉，客户端拿到的会是「504 网关超时」而不是「导出失败」。
/// 改成「立刻返回任务标识 + 后台执行 + 实时进度」之后，
/// 长耗时不再是一个需要特殊处理的场景。</para>
/// <para><b>多读者</b>：与编排队列（单读者）不同，导出是<b>只读</b>操作——
/// 两个导出同时跑不会互相踩（它们不创建也不删除任何 Docker 资源），
/// 因此允许多个读者并行处理，靠 <c>Files:MaxConcurrentExports</c> 控制并发上限。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "TransferJobQueue 是本项目的领域名词（传输任务队列），非 CA1711 所针对的系统命名空间后缀。")]
public sealed class TransferJobQueue
{
    private readonly Channel<TransferJobRequest> _channel =
        Channel.CreateUnbounded<TransferJobRequest>(new UnboundedChannelOptions
        {
            // 多读者：导出互不干扰，允许并行
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>
    /// 入队。
    /// </summary>
    /// <param name="request">任务请求。</param>
    /// <returns>入队成功返回 <see langword="true"/>。</returns>
    public bool TryEnqueue(TransferJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _channel.Writer.TryWrite(request);
    }

    /// <summary>
    /// 依次取出待处理的任务。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务请求的异步序列。</returns>
    public IAsyncEnumerable<TransferJobRequest> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
