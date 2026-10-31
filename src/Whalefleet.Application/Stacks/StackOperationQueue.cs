using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Whalefleet.Application.Stacks;

/// <summary>
/// 编排操作类型。
/// </summary>
/// <remarks>
/// 用字符串常量而不是枚举：它会进审计动作、任务快照与前端 i18n，
/// 需要与「操作类型」这一外部可见的概念保持一致。
/// </remarks>
public static class StackOperations
{
    /// <summary>部署（新项目）。</summary>
    public const string Deploy = "deploy";

    /// <summary>更新（重新应用 YAML）。</summary>
    public const string Update = "update";

    /// <summary>停止（停掉全部服务，容器保留）。</summary>
    public const string Stop = "stop";

    /// <summary>删除（连容器与网络一起清掉）。</summary>
    public const string Remove = "remove";
}

/// <summary>
/// 一次编排操作的请求（入队后由后台工作线程消费）。
/// </summary>
/// <remarks>
/// <para><b>为什么把「操作者」一起塞进请求</b>：后台工作线程运行在<b>请求上下文之外</b>，
/// 那里的 <c>ICurrentUser</c> 是空的（没人登录）。
/// 若不把操作者带过来，审计记录里的 actor 会变成「anonymous」——
/// 而部署整套编排恰恰是最该留痕的操作之一。
/// 因此在<b>请求期</b>捕获操作者信息，随请求一起传给后台。</para>
/// <para><b>为什么 Compose 内容随请求带过去</b>：后台线程不重新读库，
/// 用的是请求期已经解析并校验过的内容——
/// 免得「解析一次、校验一次、执行时又按另一套规则解析一次」。</para>
/// </remarks>
/// <param name="TaskId">任务标识（客户端生成的进度令牌）。</param>
/// <param name="HostName">所属主机名。</param>
/// <param name="StackName">Stack 名称。</param>
/// <param name="Operation">操作类型。</param>
/// <param name="ComposeContent">Compose 文件原文（部署 / 更新时有效）。</param>
/// <param name="PullAlways">是否总是拉取最新镜像（<c>false</c> 表示只在本地缺失时拉）。</param>
/// <param name="PruneOrphans">是否清掉「已不在 YAML 里的」孤儿服务。</param>
/// <param name="RemoveVolumes">删除时是否连数据卷一起删。</param>
/// <param name="ActorId">操作者主键；<see langword="null"/> 表示系统操作。</param>
/// <param name="ActorName">操作者名（快照）。</param>
/// <param name="ClientIp">来源 IP。</param>
/// <param name="WebhookTokenHint">触发来源的 Webhook 令牌提示；<see langword="null"/> 表示用户手动触发。</param>
public sealed record StackOperationRequest(
    string TaskId,
    string HostName,
    string StackName,
    string Operation,
    string? ComposeContent,
    bool PullAlways,
    bool PruneOrphans,
    bool RemoveVolumes,
    Guid? ActorId,
    string? ActorName,
    string? ClientIp,
    string? WebhookTokenHint);

/// <summary>
/// 编排操作队列：把「要做的事」交给后台工作线程。
/// </summary>
/// <remarks>
/// <para><b>为什么用队列而不是直接在请求里做</b>：部署可能要拉镜像（几分钟），
/// 而 HTTP 请求扛不住那么长——中间的反代会把连接掐掉，
/// 客户端拿到的会是「504 网关超时」而不是「部署失败」。
/// 改成「立刻返回任务标识 + 后台执行 + 实时进度」之后，
/// 长耗时不再是一个需要特殊处理的场景。</para>
/// <para><b>单读者</b>：一个后台工作线程按先进先出处理，
/// 因此同一时刻只有一个编排操作在动守护进程。
/// 这不只是性能取舍——两个编排同时改同一个 Docker 主机时，
/// 网络与容器的创建 / 删除会互相踩。</para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "StackOperationQueue 是本项目的领域名词（编排操作队列），非 CA1711 所针对的系统命名空间后缀。")]
public sealed class StackOperationQueue
{
    private readonly Channel<StackOperationRequest> _channel =
        Channel.CreateUnbounded<StackOperationRequest>(new UnboundedChannelOptions
        {
            // 单读者：不需要多播；关闭时把排队里的都放出去
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// 入队。
    /// </summary>
    /// <param name="request">操作请求。</param>
    /// <returns>入队成功返回 <see langword="true"/>。</returns>
    public bool TryEnqueue(StackOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _channel.Writer.TryWrite(request);
    }

    /// <summary>
    /// 依次取出待处理的操作。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作请求的异步序列。</returns>
    public IAsyncEnumerable<StackOperationRequest> ReadAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// 关闭队列（不再接受新操作）。
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();
}
