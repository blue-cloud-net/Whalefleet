namespace Whalefleet.Application.Abstractions.Terminal;

/// <summary>
/// 终端的控制台尺寸（字符列 / 行）。
/// </summary>
/// <param name="Columns">列数。</param>
/// <param name="Rows">行数。</param>
/// <remarks>
/// 首帧尺寸来自浏览器（xterm 的 <c>FitAddon</c> 算出来），之后每次窗口变化都会再推一次。
/// 尺寸不对的症状很具体：<c>top</c> / <c>vim</c> 这类全屏程序画出来的框是错位的，
/// 而普通命令的输出看不出问题——所以它不是「可选的优化」。
/// </remarks>
public sealed record TerminalConsoleSize(int Columns, int Rows)
{
    /// <summary>
    /// 默认尺寸（80 × 24）。
    /// </summary>
    /// <remarks>
    /// 客户端没给尺寸时的兜底值，取值依据是「传统终端的默认尺寸」。
    /// </remarks>
    public static TerminalConsoleSize Default { get; } = new(80, 24);
}

/// <summary>
/// 一条已建立的双向终端流。
/// </summary>
/// <remarks>
/// <para><b>为什么是「流」而不是「一次性 exec」</b>：交互式终端要求双向、持续、可交互。
/// 一次性 exec（<c>POST …/containers/exec</c>）在本项目里是<b>明确非目标</b>——
/// 它没有 stdin，也算不上终端。</para>
/// <para><b>为什么读是「拉到 <c>byte[]</c>」而不是 <c>Memory&lt;byte&gt;</c></b>：
/// 底层 SDK（<c>MultiplexedStream.ReadOutputAsync</c>）只接受数组；
/// 包一层 <c>Memory</c> 需要 <c>MemoryMarshal</c> 反解，反而多一次分支。</para>
/// </remarks>
public interface IContainerTerminalSession : IAsyncDisposable
{
    /// <summary>
    /// 读取一段输出。
    /// </summary>
    /// <param name="buffer">目标缓冲区。</param>
    /// <param name="offset">写入起始偏移。</param>
    /// <param name="count">最多读取的字节数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读到的字节数；<c>0</c> 表示对端已结束（容器内的进程退出了）。</returns>
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    /// <summary>
    /// 写入一段输入（用户的按键）。
    /// </summary>
    /// <param name="buffer">源缓冲区。</param>
    /// <param name="offset">起始偏移。</param>
    /// <param name="count">字节数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);

    /// <summary>
    /// 调整控制台尺寸。
    /// </summary>
    /// <param name="size">新的尺寸。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task ResizeAsync(TerminalConsoleSize size, CancellationToken cancellationToken = default);

    /// <summary>
    /// 会话结束后的退出码。
    /// </summary>
    /// <remarks>
    /// <b>只有</b>在 <see cref="IAsyncDisposable.DisposeAsync"/> 完成后才有值：
    /// 退出码要从守护进程 <c>exec inspect</c> 取，而它只有等进程真的结束（或连接被关掉）才回填。
    /// 会话进行中读它永远得到 <see langword="null"/>——那表示「还不知道」，不是失败。
    /// </remarks>
    long? ExitCode { get; }
}
