using Whalefleet.Application.Abstractions.Terminal;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 申请一次终端会话的请求。
/// </summary>
/// <remarks>
/// <para><b>为什么没有 <c>hostId</c></b>：主机在 URL 路径里（<c>/api/v1/hosts/{hostId}/…</c>）。
/// 请求体里再放一份会立刻出现「路径说 A、体内说 B」的歧义，而歧义的解法只能是「以后者为准」
/// 或「不一致就报错」——两种都让人困惑。</para>
/// <para><b>为什么要区分 <see cref="Shell"/> 与 <see cref="Command"/></b>：
/// 交互式会话是 <c>[shell]</c>，而「跑一条命令」是 <c>[shell, "-c", command]</c>——
/// 后者需要 shell 来解析引号与管道，直接 exec 一个命令字符串只会得到「找不到文件」。</para>
/// </remarks>
public sealed record ContainerTerminalRequest
{
    /// <summary>容器标识（完整 ID / 短 ID / 名称）。</summary>
    public string ContainerId { get; init; } = string.Empty;

    /// <summary>要启动的 shell 绝对路径；省略时取 <see cref="TerminalOptions.DefaultShell"/>。</summary>
    public string? Shell { get; init; }

    /// <summary>非交互模式下要执行的一条命令；省略表示开交互式 shell。</summary>
    public string? Command { get; init; }

    /// <summary>初始列数；省略时用 <see cref="TerminalConsoleSize.Default"/>。</summary>
    public int? Columns { get; init; }

    /// <summary>初始行数；省略时用 <see cref="TerminalConsoleSize.Default"/>。</summary>
    public int? Rows { get; init; }

    /// <summary>
    /// 取初始控制台尺寸（缺省或非正数一律回落到默认值）。
    /// </summary>
    /// <returns>可直接下发给守护进程的尺寸。</returns>
    /// <remarks>
    /// <b>为什么非正数也回落</b>：<c>cols=0</c> 会让守护进程回的 <c>resize</c> 直接 400，
    /// 而界面只表现为「全屏程序画歪了」。与其把非法值透传下去，不如选一个能用的默认值。
    /// </remarks>
    public TerminalConsoleSize ResolveSize() =>
        this.Columns is > 0 and < 1000 && this.Rows is > 0 and < 1000
            ? new TerminalConsoleSize(this.Columns.Value, this.Rows.Value)
            : TerminalConsoleSize.Default;
}

/// <summary>
/// 授权通过后的会话目标（签发令牌与建立连接共用）。
/// </summary>
/// <param name="HostName">主机名（<c>DockerHost.Name</c>，即 URL 里的 <c>hostId</c>）。</param>
/// <param name="ContainerId">容器的完整 ID（inspect 回填后的规范值）。</param>
/// <param name="ContainerName">容器名（不含前导 <c>/</c>，用于日志与界面）。</param>
/// <param name="Shell">要启动的 shell 绝对路径。</param>
/// <param name="Command">非交互命令；<see langword="null"/> 表示交互式 shell。</param>
/// <remarks>
/// <b>为什么不直接把 <see cref="ContainerTerminalRequest"/> 传下去</b>：请求里是「用户写的」，
/// 这里是「校验并规范化之后的」。把两者混成一个类型，等于给了下游「拿到未校验值」的机会。
/// </remarks>
public sealed record ContainerTerminalTarget(
    string HostName,
    string ContainerId,
    string ContainerName,
    string Shell,
    string? Command)
{
    /// <summary>
    /// 组装要交给守护进程的 <c>cmd</c> 数组。
    /// </summary>
    /// <returns><c>[shell]</c>（交互）或 <c>[shell, "-c", command]</c>（非交互）。</returns>
    public IReadOnlyList<string> BuildCommand() =>
        this.Command is null
            ? [this.Shell]
            : [this.Shell, "-c", this.Command];
}

/// <summary>
/// 打开成功的终端会话句柄。
/// </summary>
/// <param name="Session">双向流。</param>
/// <param name="Target">会话目标（主机 / 容器 / 命令，用于留痕日志）。</param>
public sealed record ContainerTerminalSessionHandle(
    IContainerTerminalSession Session,
    ContainerTerminalTarget Target);

/// <summary>
/// 签发 terminal token 的响应。
/// </summary>
/// <param name="TerminalToken">令牌原文（放进 WebSocket 的 <c>?token=</c>）。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
/// <remarks>
/// <b>不返回主机 / 容器 / 命令</b>：它们已经在令牌里，回带一份只会多出一个可能与令牌不一致的来源。
/// 界面需要展示"将要执行什么"时，直接用自己刚提交的值即可。
/// </remarks>
public sealed record ContainerTerminalTokenResult(string TerminalToken, DateTime ExpiresAt);
