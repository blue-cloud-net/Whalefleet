using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Docker;

/// <summary>
/// 把 Docker SDK 的异常翻译成项目统一的业务错误码。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：SDK 的异常类型是「传输层 / API 层」的语言，而客户端要的是项目自己的
/// 错误码。若让 <c>DockerApiException</c> 直接冒到接口层，会被兜底成 <c>9999</c>/500——
/// 于是「容器不存在」和「代码崩了」在调用方看来一模一样，前端也没法把前者处理成一次友好提示。</para>
/// <para><b>映射表</b>：</para>
/// <list type="table">
///   <listheader><term>SDK 异常</term><description>错误码 / HTTP</description></listheader>
///   <item><term><c>DockerContainerNotFoundException</c>、<c>DockerImageNotFoundException</c></term>
///     <description><c>1401</c> / 404（资源不存在）</description></item>
///   <item><term><c>DockerApiException</c> 且状态码为 404</term>
///     <description><c>1401</c> / 404（volume / network 等没有专用异常类型）</description></item>
///   <item><term><c>DockerApiException</c> 其他状态码</term>
///     <description><c>1402</c> / 500（Docker API 调用失败）</description></item>
///   <item><term>超时（<b>非</b>调用方取消）</term>
///     <description><c>1403</c> / 408（操作超时）</description></item>
///   <item><term><c>HttpRequestException</c>、<c>SocketException</c></term>
///     <description><c>1301</c> / 502（主机连通性失败）</description></item>
///   <item><term><see cref="BusinessException"/></term>
///     <description>原样上抛（已经是项目语言）</description></item>
/// </list>
/// <para><b>两种取消必须区分</b>：调用方主动取消（前端断开、请求中止）要继续抛
/// <see cref="OperationCanceledException"/>——框架会把它当成「请求中止」而不是故障；
/// 只有非调用方取消的超时才翻译成 <c>1403</c>。混为一谈的后果是：用户每关一个页面就多一条 ERROR 日志。</para>
/// </remarks>
public static class DockerErrorTranslator
{
    /// <summary>
    /// 执行返回结果的 Docker 调用，并把异常翻译成业务错误。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="operation">Docker 调用。</param>
    /// <param name="resourceDescription">错误消息里的资源描述（如「容器 abc」）。</param>
    /// <param name="cancellationToken">取消令牌（用于区分「调用方取消」与「超时」）。</param>
    /// <returns>Docker 调用的返回值。</returns>
    /// <exception cref="BusinessException">翻译后的业务错误。</exception>
    /// <exception cref="OperationCanceledException">调用方主动取消。</exception>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string resourceDescription,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDescription);

        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用裸 throw 而不是 throw exception：前者保留原始堆栈，后者会把堆栈重置到这里。
            throw;
        }
        catch (Exception exception) when (exception is not BusinessException)
        {
            throw Translate(exception, resourceDescription);
        }
    }

    /// <summary>
    /// 执行无返回值的 Docker 调用，并把异常翻译成业务错误。
    /// </summary>
    /// <param name="operation">Docker 调用。</param>
    /// <param name="resourceDescription">错误消息里的资源描述。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    /// <exception cref="BusinessException">翻译后的业务错误。</exception>
    /// <exception cref="OperationCanceledException">调用方主动取消。</exception>
    public static async Task ExecuteAsync(
        Func<Task> operation,
        string resourceDescription,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDescription);

        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not BusinessException)
        {
            throw Translate(exception, resourceDescription);
        }
    }

    /// <summary>
    /// 单个异常的翻译规则（纯函数，便于直接测）。
    /// </summary>
    /// <param name="exception">SDK 抛出的异常。</param>
    /// <param name="resourceDescription">资源描述。</param>
    /// <returns>对应的业务错误。</returns>
    private static BusinessException Translate(Exception exception, string resourceDescription) =>
        exception switch
        {
            // 资源不存在：SDK 为容器与镜像提供了专用异常类型。
            DockerContainerNotFoundException or DockerImageNotFoundException =>
                new BusinessException(
                    ErrorCodes.DockerResourceNotFound,
                    $"{resourceDescription}不存在",
                    exception),

            // 其他资源的 404（volume / network 等）没有专用类型，只能看状态码。
            DockerApiException { StatusCode: HttpStatusCode.NotFound } =>
                new BusinessException(
                    ErrorCodes.DockerResourceNotFound,
                    $"{resourceDescription}不存在",
                    exception),

            // 409：请求本身合法，是资源当前状态不允许（删除运行中的容器、删除被占用的镜像）。
            // 归成 1402/500 会让「先停容器再重试」看起来像服务端故障，前端也无从给出可操作的提示。
            DockerApiException { StatusCode: HttpStatusCode.Conflict } =>
                new BusinessException(
                    ErrorCodes.DockerResourceConflict,
                    $"{resourceDescription}的当前状态不允许该操作（如需强制操作请在请求里显式指定）",
                    exception),

            DockerApiException apiException =>
                new BusinessException(
                    ErrorCodes.DockerApiFailed,
                    $"Docker API 调用失败（HTTP {(int)apiException.StatusCode}）",
                    exception),

            OperationCanceledException or TaskCanceledException =>
                new BusinessException(ErrorCodes.DockerTimeout, "Docker 操作超时", exception),

            // 守护进程不可达（socket 断开、TCP 连接被拒）：这是「主机连不上」，不是 Docker API 报错。
            HttpRequestException or SocketException =>
                new BusinessException(ErrorCodes.HostUnreachable, "无法连接 Docker 守护进程", exception),

            _ => new BusinessException(ErrorCodes.DockerApiFailed, "Docker 操作失败", exception),
        };
}
