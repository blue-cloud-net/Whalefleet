using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Whalefleet.Api.Errors;
using Whalefleet.Application.Abstractions.Terminal;
using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Whalefleet.Api.Terminal;

/// <summary>
/// 容器交互式终端的 WebSocket 端点（<c>/ws/hosts/{hostId}/containers/exec</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么是原生 WebSocket 而不是 SignalR</b>：终端是原始双向字节流
/// （加上 resize 这类控制帧），与 Hub 的「方法调用 + 事件」模型不匹配。</para>
/// <para><b>为什么所有可诊断的失败都在 <c>AcceptWebSocketAsync</c> 之前</b>：浏览器建立的
/// WebSocket 一旦失败，前端拿到的只有一个 <c>close</c> 事件（多半还是 1006），
/// <b>看不到响应体、也看不到状态码</b>。因此这里在握手前把 token、主机、容器状态、权限、
/// 并发上限逐项校验完，失败时按普通 HTTP 写 <c>ApiResult</c>——
/// 客户端虽然读不到，但服务端日志与 <c>curl -i</c> 能看清是哪一步拒的。
/// 前端因此另有一步 <c>POST …/containers/terminal-token</c>：那一步同样会做这些校验，
/// 而它的错误是能正常显示给用户的。</para>
/// <para><b>为什么用 <c>MapGet</c> 而不是自定义中间件分支</b>：只有路由端点才能拿到
/// <c>{hostId}</c> 路由值，也才能让 <c>CurrentHostMiddleware</c> 的排除规则有据可依
/// （见该中间件里对 <see cref="TerminalRoutes.Prefix"/> 的判断）。</para>
/// </remarks>
internal static class ContainerTerminalEndpoint
{
    /// <summary>
    /// 输出方向的缓冲区大小（容器 → 浏览器）。
    /// </summary>
    /// <remarks>
    /// 8 KiB 与 xterm 的典型输出块同量级：更小会让高频输出退化成大量小帧，
    /// 更大则让「按下回车后多久看到输出」变差（缓冲区要填满才发）。
    /// </remarks>
    private const int OutputBufferSize = 8192;

    /// <summary>
    /// 输入方向的缓冲区大小（浏览器 → 容器）。
    /// </summary>
    /// <remarks>
    /// 按键是逐个产生的，4 KiB 已经远超「一次粘贴」的常见规模。
    /// </remarks>
    private const int InputBufferSize = 4096;

    /// <summary>
    /// 注册终端端点。
    /// </summary>
    /// <param name="endpoints">端点构建器。</param>
    /// <returns>同一 <see cref="IEndpointRouteBuilder"/>，便于链式调用。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> 为 <see langword="null"/>。</exception>
    public static IEndpointRouteBuilder MapContainerTerminal(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(TerminalRoutes.ContainerExec, HandleAsync);

        return endpoints;
    }

    /// <summary>
    /// 处理一次终端连接。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="hostId">路径里的主机标识。</param>
    /// <param name="terminals">终端用例（授权 + 开流）。</param>
    /// <param name="tokens">terminal token 服务。</param>
    /// <param name="registry">会话计数器。</param>
    /// <param name="options">终端选项。</param>
    /// <param name="clock">时间源（令牌过期判定用）。</param>
    /// <param name="loggerFactory">日志工厂（端点本身不是类，取不到 <c>ILogger&lt;T&gt;</c>）。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task HandleAsync(
        HttpContext context,
        [FromRoute] string hostId,
        [FromServices] ContainerTerminalService terminals,
        [FromServices] ITerminalTokenService tokens,
        [FromServices] TerminalSessionRegistry registry,
        [FromServices] IOptions<TerminalOptions> options,
        [FromServices] TimeProvider clock,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(ContainerTerminalEndpoint).FullName!);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteErrorAsync(context, ErrorCodes.ValidationFailed, "该端点只接受 WebSocket 连接。")
                .ConfigureAwait(false);

            return;
        }

        var rawToken = context.Request.Query[TerminalRoutes.TokenQueryKey].ToString();
        var grant = await tokens
            .ValidateAsync(rawToken, clock.GetUtcNow().UtcDateTime, context.RequestAborted)
            .ConfigureAwait(false);

        if (grant is null)
        {
            logger.LogWarning("终端握手被拒：terminal token 无效或已过期（主机={HostId}）", hostId);

            await WriteErrorAsync(
                    context,
                    ErrorCodes.TerminalTokenInvalid,
                    "终端令牌无效或已过期，请重新打开终端。")
                .ConfigureAwait(false);

            return;
        }

        // 路径里的主机必须与令牌里绑定的主机一致：令牌是为「某台主机上的某个容器」签发的，
        // 换个主机路径复用同一张令牌就说明它被改造过。
        if (!String.Equals(grant.HostId, hostId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "终端握手被拒：令牌主机与路径主机不一致（路径={HostId}，令牌={TokenHost}）",
                hostId,
                grant.HostId);

            await WriteErrorAsync(context, ErrorCodes.TerminalTokenInvalid, "终端令牌与目标主机不匹配。")
                .ConfigureAwait(false);

            return;
        }

        if (!registry.TryAcquire(grant.UserId, options.Value.MaxSessionsPerUser, out var lease))
        {
            await WriteErrorAsync(
                    context,
                    ErrorCodes.DockerResourceConflict,
                    $"该账号已有 {options.Value.MaxSessionsPerUser} 个终端会话，请先关闭其它窗口。")
                .ConfigureAwait(false);

            return;
        }

        using (lease)
        {
            await RunAsync(context, hostId, grant, terminals, clock, logger).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 建立会话并在两个方向之间搬运数据。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="hostId">主机标识。</param>
    /// <param name="grant">令牌里的授权内容。</param>
    /// <param name="terminals">终端用例。</param>
    /// <param name="clock">时间源（记录会话时长）。</param>
    /// <param name="logger">日志。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task RunAsync(
        HttpContext context,
        string hostId,
        TerminalSessionGrant grant,
        ContainerTerminalService terminals,
        TimeProvider clock,
        ILogger logger)
    {
        var request = new ContainerTerminalRequest
        {
            ContainerId = grant.ContainerId,
            Shell = grant.Shell,
            Command = grant.Command,
            Columns = ParseSize(context.Request.Query[TerminalRoutes.ColumnsQueryKey]),
            Rows = ParseSize(context.Request.Query[TerminalRoutes.RowsQueryKey]),
        };

        ContainerTerminalSessionHandle handle;

        try
        {
            handle = await terminals
                .OpenAsync(hostId, grant.UserId, request, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (BusinessException exception)
        {
            logger.LogWarning(
                "终端连接被拒（用户={UserId}，主机={HostId}，容器={ContainerId}，Code={Code}）：{Reason}",
                grant.UserId,
                hostId,
                grant.ContainerId,
                exception.Code,
                exception.Message);

            await WriteErrorAsync(context, exception.Code, exception.Message).ConfigureAwait(false);

            return;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var startedAt = clock.GetUtcNow();
        var reason = "unknown";

        var session = handle.Session;

        try
        {
            WebSocket socket;

            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            }
            catch (WebSocketException exception)
            {
                // 握手在 accept 阶段失败（客户端已断开、或反代没透传 Upgrade）。
                logger.LogWarning(
                    exception,
                    "终端握手失败（会话={SessionId}，主机={HostId}，容器={ContainerName}）",
                    sessionId,
                    handle.Target.HostName,
                    handle.Target.ContainerName);

                return;
            }

            using (socket)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

                var toClient = PumpToClientAsync(session, socket, linked.Token);
                var fromClient = PumpFromClientAsync(socket, session, linked.Token);

                var finished = await Task.WhenAny(toClient, fromClient).ConfigureAwait(false);
                reason = await finished.ConfigureAwait(false);

                // 一个方向结束就拆整条会话：交互式终端的两个方向是绑死的，
                // 只关一半会留下一个「还在往容器里写、但没人读输出」的连接。
                await linked.CancelAsync().ConfigureAwait(false);

                await AwaitQuietlyAsync(toClient, fromClient).ConfigureAwait(false);

                await CloseSocketAsync(socket).ConfigureAwait(false);
            }
        }
        finally
        {
            // 释放会话必须在写留痕日志**之前**：退出码要从 exec inspect 取，
            // 而它只有等连接关掉之后才由守护进程回填。
            await session.DisposeAsync().ConfigureAwait(false);
        }

        var elapsed = clock.GetUtcNow() - startedAt;

        // 会话留痕（0.8.0）：一条结构化日志，含起止时间、退出码与结束原因。
        // **不记录**用户输入的内容与终端输出——里面可能有密码、token 与内网拓扑。
        // 0.9.0 落地操作审计后，本条会改为写审计表。
        logger.LogInformation(
            "终端会话结束（会话={SessionId}，用户={UserId}，主机={HostId}，容器={ContainerName}，"
            + "Shell={Shell}，非交互={HasCommand}，开始={StartedAt}，时长毫秒={DurationMs}，"
            + "退出码={ExitCode}，结束原因={Reason}）",
            sessionId,
            grant.UserId,
            handle.Target.HostName,
            handle.Target.ContainerName,
            handle.Target.Shell,
            handle.Target.Command is not null,
            startedAt,
            (long)elapsed.TotalMilliseconds,
            handle.Session.ExitCode,
            reason);
    }

    /// <summary>
    /// 容器输出 → 浏览器。
    /// </summary>
    /// <param name="session">终端流。</param>
    /// <param name="socket">浏览器侧连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结束原因（写进留痕日志）。</returns>
    /// <remarks>
    /// <b>只发二进制帧</b>：终端输出是字节流（可能不是合法 UTF-8 的中间态），
    /// 拆成字符串既要处理跨帧的多字节字符，又会丢控制字符。
    /// 控制信息（如 resize 的应答）才用文本帧，两个方向按帧类型区分，不会混淆。
    /// </remarks>
    private static async Task<string> PumpToClientAsync(
        IContainerTerminalSession session,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[OutputBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await session
                    .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return "container-exit";
                }

                await socket
                    .SendAsync(
                        new ArraySegment<byte>(buffer, 0, read),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return "cancelled";
        }
        catch (Exception exception) when (IsExpectedDisconnect(exception))
        {
            return "client-closed";
        }
    }

    /// <summary>
    /// 浏览器 → 容器输入。
    /// </summary>
    /// <param name="socket">浏览器侧连接。</param>
    /// <param name="session">终端流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结束原因（写进留痕日志）。</returns>
    /// <remarks>
    /// <para><b>文本帧是控制帧</b>：目前只有 resize（<c>{"type":"resize","cols":..,"rows":..}</c>），
    /// 其余一律按二进制当作用户输入直接写进 stdin。</para>
    /// <para><b>为什么 resize 走 JSON 而不做二进制协议</b>：终端数据的方向已经全二进制，
    /// 控制帧用文本能一眼分清；且它是低频帧，解析成本无意义。定长二进制头会省几微秒，
    /// 但排障时要查表——收益远小于代价。</para>
    /// </remarks>
    private static async Task<string> PumpFromClientAsync(
        WebSocket socket,
        IContainerTerminalSession session,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[InputBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return "client-closed";
                }

                if (result.Count == 0)
                {
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text
                    && await TryApplyControlAsync(buffer, result.Count, session, cancellationToken)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                await session
                    .WriteAsync(buffer, 0, result.Count, cancellationToken)
                    .ConfigureAwait(false);
            }

            return "cancelled";
        }
        catch (Exception exception) when (IsExpectedDisconnect(exception))
        {
            return "client-closed";
        }
    }

    /// <summary>
    /// 尝试把一段文本帧当作控制指令处理。
    /// </summary>
    /// <param name="buffer">收到的字节。</param>
    /// <param name="count">字节数。</param>
    /// <param name="session">终端流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是控制指令并已处理返回 <see langword="true"/>；否则 <see langword="false"/>（按输入处理）。</returns>
    /// <remarks>
    /// <b>解析失败按输入处理</b>：用户完全可能「粘贴一段 JSON」到终端里（比如在编辑配置文件），
    /// 把无法解析的文本帧丢掉会让这类回车莫名其妙地消失。
    /// </remarks>
    private static async Task<bool> TryApplyControlAsync(
        byte[] buffer,
        int count,
        IContainerTerminalSession session,
        CancellationToken cancellationToken)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, count));
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || !String.Equals(type.GetString(), "resize", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("cols", out var cols)
                || !root.TryGetProperty("rows", out var rows)
                || !cols.TryGetInt32(out var columns)
                || !rows.TryGetInt32(out var lineCount)
                || columns <= 0
                || lineCount <= 0)
            {
                return false;
            }

            await session
                .ResizeAsync(new TerminalConsoleSize(columns, lineCount), cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
    }

    /// <summary>
    /// 等两个方向的搬运任务彻底结束（吞掉取消与断连）。
    /// </summary>
    /// <param name="tasks">任务集合。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task AwaitQuietlyAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception) when (IsExpectedDisconnect(exception))
            {
                // 已经在收尾了：这里的异常只可能是"连接没了"，没有别的处理可做。
            }
        }
    }

    /// <summary>
    /// 尽力优雅关闭 WebSocket。
    /// </summary>
    /// <param name="socket">浏览器侧连接。</param>
    /// <returns>表示异步操作的任务。</returns>
    private static async Task CloseSocketAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await socket
                .CloseAsync(WebSocketCloseStatus.NormalClosure, "session closed", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedDisconnect(exception))
        {
            // 对端已经走了就别再纠缠：close 帧送不到也不会影响服务端状态。
        }
    }

    /// <summary>
    /// 判断异常是否属于「连接结束」这一类（而不是真正的服务端故障）。
    /// </summary>
    /// <param name="exception">异常。</param>
    /// <returns>属于正常断连返回 <see langword="true"/>。</returns>
    private static bool IsExpectedDisconnect(Exception exception) =>
        exception is OperationCanceledException
            or WebSocketException
            or IOException
            or ObjectDisposedException
            or EndOfStreamException;

    /// <summary>
    /// 解析尺寸查询参数。
    /// </summary>
    /// <param name="value">查询参数值。</param>
    /// <returns>正整数时返回该值；否则返回 <see langword="null"/>。</returns>
    private static int? ParseSize(Microsoft.Extensions.Primitives.StringValues value) =>
        Int32.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0
                ? parsed
                : null;

    /// <summary>
    /// 以统一 <see cref="ApiResult"/> 形状写一个错误响应。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="code">业务错误码。</param>
    /// <param name="message">错误消息。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <remarks>
    /// 复用 <see cref="ErrorResponseWriter"/>，保证 `/ws` 的失败响应与其余端点<b>逐字同形</b>——
    /// 否则前端「按信封取错误码」的那套逻辑在这里会拿到一个不同形状的 JSON。
    /// </remarks>
    private static Task WriteErrorAsync(HttpContext context, int code, string message)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = ErrorHttpStatus.FromErrorCode(code);
        context.Response.ContentType = ErrorResponseWriter.JsonContentType;

        return context.Response.WriteAsync(
            ErrorResponseWriter.Serialize(code, message),
            Encoding.UTF8,
            context.RequestAborted);
    }
}
