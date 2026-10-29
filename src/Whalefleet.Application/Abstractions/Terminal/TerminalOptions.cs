namespace Whalefleet.Application.Abstractions.Terminal;

/// <summary>
/// 交互式终端选项（配置节 <c>Terminal</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么这些值可配</b>：每一条都对应一个真实的风险或成本——
/// 终端 token 的有效期决定「泄漏后能被用多久」，每用户会话数决定「一个人能同时开几个
/// <c>docker exec</c>」，shell 白名单决定「能拉起哪些可执行文件」。硬编码这些值等于
/// 把运维的判断权收走。</para>
/// <para><b>为什么全部 <c>ValidateOnStart</c></b>：配错（比如白名单里写了相对路径、
/// 会话数写成 0）必须在启动时炸，而不是在第一次打开终端时表现为「点了没反应」。</para>
/// </remarks>
public sealed class TerminalOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Terminal";

    /// <summary>
    /// 默认 shell（未指定时使用）。默认 <c>/bin/sh</c>。
    /// </summary>
    /// <remarks>
    /// <c>/bin/sh</c> 是唯一在「精简镜像」里也几乎一定存在的解释器：
    /// <c>alpine</c>、<c>distroless</c> 之外的绝大多数镜像都有它，而 <c>bash</c> 在
    /// alpine / busybox 镜像里通常没有。
    /// </remarks>
    public string DefaultShell { get; set; } = "/bin/sh";

    /// <summary>
    /// 允许启动的 shell 绝对路径白名单。
    /// </summary>
    /// <remarks>
    /// <b>为什么是白名单而不是「任意路径」</b>：终端的入参会被写进 JWT，
    /// 而 JWT 是<b>客户端可见</b>的。任意可执行文件意味着可以把令牌当成一张
    /// 「在容器里执行任意程序」的许可证——白名单让这张许可证的用途被限定在「开一个 shell」。
    /// 需要更自由时，用 shell 的 <c>-c</c> 参数（见 <see cref="AllowCustomCommand"/>）。
    /// </remarks>
    public string[] AllowedShells { get; set; } = ["/bin/sh", "/bin/bash", "/bin/ash", "/bin/zsh"];

    /// <summary>
    /// 是否允许在终端里直接执行一条自定义命令（<c>shell -c "…"</c>）。
    /// </summary>
    /// <remarks>
    /// 默认开启：能通过 <c>container.write</c> 检查的用户本来就能等价地操作容器，
    /// 拦住它只是让人多敲一次命令。真正约束边界的是权限，不是这个开关；
    /// 关掉它的场景是「运维希望界面上只剩一个纯 shell」。
    /// </remarks>
    public bool AllowCustomCommand { get; set; } = true;

    /// <summary>
    /// terminal token 有效期。默认 5 分钟。
    /// </summary>
    /// <remarks>
    /// 它只用于<b>建立连接</b>，连接建立后不再校验——因此 5 分钟足够覆盖「打开面板 → 握手」，
    /// 又短到让一条泄漏的令牌几乎没有利用窗口。
    /// </remarks>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 单个用户最多同时持有的终端会话数。默认 5。
    /// </summary>
    /// <remarks>
    /// <b>为什么必须有上限</b>：每个会话在容器里都是一个真实进程，在服务端是一条
    /// WebSocket + 一条到守护进程的 hijacked 连接。没有上限时，一个循环打开终端的脚本
    /// 就能把容器进程表与守护进程连接数同时撑爆。
    /// </remarks>
    public int MaxSessionsPerUser { get; set; } = 5;

    /// <summary>
    /// WebSocket 保活心跳间隔（秒）。默认 30。
    /// </summary>
    /// <remarks>
    /// <para><b>它真正解决的是反代的空闲超时</b>：nginx 默认 <c>proxy_read_timeout 60s</c>
    /// （本项目示例配置给到 300s）。用户盯着终端想事情时没有任何数据流，
    /// 反代会把这条连接当成空闲连接掐掉——症状是「终端莫名其妙断开，重连又好了」。</para>
    /// <para>因此本值必须<b>小于</b>运维所用的反代读超时。心跳帧本身不消耗容器的 IO。</para>
    /// </remarks>
    public int KeepAliveSeconds { get; set; } = 30;
}
