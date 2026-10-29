using Docker.DotNet;
using Docker.DotNet.Models;
using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Terminal;
using Whalefleet.Application.Common;
using Whalefleet.Application.Docker;
using Whalefleet.Core.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Whalefleet.Application.Containers;

/// <summary>
/// 容器交互式终端：授权、建立 exec 会话、双向桥接所需的流操作。
/// </summary>
/// <remarks>
/// <para><b>为什么与 <see cref="ContainerStreamService"/> 分开</b>：那个类提供的是
/// 「只读、单向、长连接」（日志 / 采样），本类提供的是「可写、双向、有状态」的会话。
/// 两者的失败语义完全不同——日志流断开只需重订阅，终端流断开则可能有半条命令已执行。</para>
/// <para><b>为什么授权与开流拆成两步</b>：令牌签发（<c>POST …/terminal-token</c>）与
/// WebSocket 握手是两次请求。若只在握手时校验，用户点开面板拿到的永远是
/// 「连接失败」而无从知道原因（浏览器对新 WebSocket 的失败只给一个 1006，拿不到响应体）。
/// 因此<b>令牌签发那一步就把能诊断的错误全部前置</b>，握手时再校验一次（容器可能已经停了）。</para>
/// <para><b>实测约束</b>：exec 要求容器<b>运行中</b>（<c>docker create</c> 后、<c>start</c> 前是 0 个可 exec 的进程），
/// 已暂停的容器同样不可用（exec 会挂住）。两者都归 <c>1404</c>/409，而不是笼统的 500。</para>
/// </remarks>
public sealed class ContainerTerminalService
{
    /// <summary>
    /// exec 会话里强制注入的终端类型。
    /// </summary>
    /// <remarks>
    /// 不设它时容器里的 <c>TERM</c> 往往是 <c>dumb</c>，<c>ls</c> 不出颜色、<c>top</c> 直接报错，
    /// 而界面上看不出是「环境变量没给」——只像是「这个容器里的程序坏了」。
    /// </remarks>
    private const string TerminalType = "xterm-256color";

    private readonly IDockerHostAccessor _accessor;
    private readonly IPermissionChecker _permissions;
    private readonly TerminalOptions _options;
    private readonly ILogger<ContainerTerminalService> _logger;

    /// <summary>
    /// 初始化终端用例服务。
    /// </summary>
    /// <param name="accessor">主机访问器（解析主机并取得 Docker 客户端）。</param>
    /// <param name="permissions">权限判定（<c>container.write</c>）。</param>
    /// <param name="options">终端选项。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerTerminalService(
        IDockerHostAccessor accessor,
        IPermissionChecker permissions,
        IOptions<TerminalOptions> options,
        ILogger<ContainerTerminalService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _permissions = permissions;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 校验一次终端请求是否成立（不建立连接）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="userId">发起请求的用户主键（<b>来自令牌或当前用户，不是请求体</b>）。</param>
    /// <param name="request">终端请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>规范化后的会话目标。</returns>
    /// <exception cref="BusinessException">
    /// 参数不合法（<c>1000</c>）、主机不存在（<c>1300</c>）、主机不可达（<c>1301</c>）、
    /// 容器不存在（<c>1401</c>）、容器未运行或已暂停（<c>1404</c>）、
    /// 无权访问该主机（<c>1501</c>）或缺少 <c>container.write</c>（<c>1502</c>）。
    /// </exception>
    public async Task<ContainerTerminalTarget> AuthorizeAsync(
        string hostId,
        Guid userId,
        ContainerTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = RequireContainerId(request.ContainerId);
        var shell = TerminalCommandValidator.NormalizeShell(request.Shell, _options);
        var command = TerminalCommandValidator.NormalizeCommand(request.Command, _options);

        // 先解析主机（主机不存在 → 1300），再判权限（→ 1501 / 1502），最后才碰守护进程。
        // 顺序不能反：对一台自己看不见的主机，连「容器在不在」都不该被告知。
        var host = await _accessor.ResolveAsync(hostId, cancellationToken).ConfigureAwait(false);

        await _permissions
            .EnsurePermissionAsync(userId, host.Id, Permission.ContainerWrite, cancellationToken)
            .ConfigureAwait(false);

        var client = await _accessor.GetClientAsync(host.Name, cancellationToken).ConfigureAwait(false);

        var container = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.InspectContainerAsync(containerId, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        var state = container.State
            ?? throw BusinessException.NotFound($"容器 {containerId} 没有状态信息");

        // 「运行中」与「已暂停」是两件事：暂停的容器 Running 仍为 true，
        // 但 exec 会挂在启动那一刻，界面表现为「终端空白、打字没反应」。
        if (!state.Running || state.Paused)
        {
            throw new BusinessException(
                ErrorCodes.DockerResourceConflict,
                state.Paused
                    ? $"容器「{MapName(container.Name, container.ID)}」已暂停，请先恢复再打开终端。"
                    : $"容器「{MapName(container.Name, container.ID)}」未运行，终端只能连接到运行中的容器。");
        }

        var target = new ContainerTerminalTarget(
            host.Name,
            container.ID ?? containerId,
            MapName(container.Name, container.ID),
            shell,
            command);

        _logger.LogDebug(
            "终端授权通过（用户={UserId}，主机={HostId}，容器={ContainerName}，Shell={Shell}，非交互={HasCommand}）",
            userId,
            target.HostName,
            target.ContainerName,
            target.Shell,
            target.Command is not null);

        return target;
    }

    /// <summary>
    /// 授权并建立双向终端流。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="userId">发起请求的用户主键。</param>
    /// <param name="request">终端请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话句柄（调用方负责释放）。</returns>
    /// <exception cref="BusinessException">同 <see cref="AuthorizeAsync"/>；另含 Docker 调用失败（<c>1402</c>）。</exception>
    /// <remarks>
    /// <b>释放责任在调用方</b>：WebSocket 端点必须在 <c>finally</c> 里
    /// <c>await using</c> 或显式 <c>DisposeAsync</c>，否则容器里会留下一个仍在等待输入的进程，
    /// 而它在界面上已经「断开」了。
    /// </remarks>
    public async Task<ContainerTerminalSessionHandle> OpenAsync(
        string hostId,
        Guid userId,
        ContainerTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await this
            .AuthorizeAsync(hostId, userId, request, cancellationToken)
            .ConfigureAwait(false);

        var client = await _accessor.GetClientAsync(target.HostName, cancellationToken).ConfigureAwait(false);

        var created = await DockerErrorTranslator.ExecuteAsync(
            () => client.Exec.CreateContainerExecAsync(
                target.ContainerId,
                new ContainerExecCreateParameters
                {
                    Cmd = [.. target.BuildCommand()],

                    // TTY 打开后 stdout / stderr 在守护进程侧被合并成一条流，
                    // 因此三个 Attach 都必须为 true——只 AttachStdout 会丢掉常见的一整类输出。
                    AttachStdin = true,
                    AttachStdout = true,
                    AttachStderr = true,
                    TTY = true,
                    Env = [$"TERM={TerminalType}"],
                },
                cancellationToken),
            $"容器 {target.ContainerName} 的终端",
            cancellationToken).ConfigureAwait(false);

        // Detach = false 才会建立 hijacked 流；Detach = true 只是「在后台跑起来」，
        // 拿不到任何输出，也无法写入 stdin。
        var stream = await DockerErrorTranslator.ExecuteAsync(
            () => client.Exec.StartContainerExecAsync(
                created.ID,
                new ContainerExecStartParameters { Detach = false },
                cancellationToken),
            $"容器 {target.ContainerName} 的终端",
            cancellationToken).ConfigureAwait(false);

        var session = new DockerTerminalSession(client, created.ID, stream);

        try
        {
            await session.ResizeAsync(request.ResolveSize(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 首帧 resize 失败不该让整个会话打不开（有些精简镜像的 TTY 不支持 resize），
            // 但已建立的流必须先释放，否则就成了一条没人读的 exec 连接。
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "终端会话已建立（用户={UserId}，主机={HostId}，容器={ContainerName}，Shell={Shell}，ExecId={ExecId}）",
            userId,
            target.HostName,
            target.ContainerName,
            target.Shell,
            created.ID);

        return new ContainerTerminalSessionHandle(session, target);
    }

    /// <summary>
    /// 校验容器标识。
    /// </summary>
    /// <param name="containerId">请求里的容器标识。</param>
    /// <returns>去空白后的容器标识。</returns>
    /// <exception cref="BusinessException">为空或全空白（<c>1000</c>）。</exception>
    private static string RequireContainerId(string? containerId)
    {
        var trimmed = containerId?.Trim();

        return String.IsNullOrEmpty(trimmed)
            ? throw new BusinessException(ErrorCodes.ValidationFailed, "容器标识不能为空。")
            : trimmed;
    }

    /// <summary>
    /// 取容器的展示名（去掉守护进程给的前导 <c>/</c>）。
    /// </summary>
    /// <param name="name">inspect 返回的名字。</param>
    /// <param name="fallback">名字缺失时的回退值（容器 ID）。</param>
    /// <returns>可展示的容器名。</returns>
    private static string MapName(string? name, string? fallback) =>
        String.IsNullOrWhiteSpace(name)
            ? fallback ?? string.Empty
            : name.TrimStart('/');

    /// <summary>
    /// 基于 Docker <c>MultiplexedStream</c> 的终端会话。
    /// </summary>
    /// <remarks>
    /// <b>TTY 模式下流里没有 8 字节帧头</b>：<c>MultiplexedStream</c> 在非复用模式下
    /// <c>ReadOutputAsync</c> 直接返回原始字节并把来源标成 stdout，因此这里既不需要
    /// 自己解帧，也不能假设「一次读回来的一定是一帧」。
    /// </remarks>
    private sealed class DockerTerminalSession : IContainerTerminalSession
    {
        private readonly IDockerClient _client;
        private readonly string _execId;
        private readonly MultiplexedStream _stream;
        private bool _disposed;

        /// <summary>
        /// 初始化会话。
        /// </summary>
        /// <param name="client">该主机的 Docker 客户端（由客户端池管理，<b>不释放</b>）。</param>
        /// <param name="execId">exec 标识。</param>
        /// <param name="stream">hijacked 双向流（由本会话释放）。</param>
        public DockerTerminalSession(IDockerClient client, string execId, MultiplexedStream stream)
        {
            _client = client;
            _execId = execId;
            _stream = stream;
        }

        /// <inheritdoc />
        public long? ExitCode { get; private set; }

        /// <inheritdoc />
        public async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            var result = await _stream
                .ReadOutputAsync(buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);

            return result.EOF ? 0 : result.Count;
        }

        /// <inheritdoc />
        public Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            return _stream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        /// <inheritdoc />
        public async Task ResizeAsync(
            TerminalConsoleSize size,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(size);

            await _client.Exec
                .ResizeExecTtyAsync(
                    _execId,
                    new ContainerResizeParameters
                    {
                        Height = size.Rows,
                        Width = size.Columns,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // 先发 EOF 再关流：只关流的话，容器里的 shell 有时要等到下一次读失败才退出，
            // 表现为「面板关了，容器里的进程还在」。
            _stream.CloseWrite();
            _stream.Dispose();

            // 退出码要等进程真的结束才有值。会话可能因为容器被删 / 主机失联而收不到，
            // 那属于「不知道」，不是错误——留痕日志里记 null 即可。
            try
            {
                var inspected = await _client.Exec
                    .InspectContainerExecAsync(_execId, CancellationToken.None)
                    .ConfigureAwait(false);

                ExitCode = inspected.ExitCode;
            }
            catch (DockerApiException)
            {
                ExitCode = null;
            }
            catch (HttpRequestException)
            {
                ExitCode = null;
            }
            catch (ObjectDisposedException)
            {
                ExitCode = null;
            }
        }
    }
}
