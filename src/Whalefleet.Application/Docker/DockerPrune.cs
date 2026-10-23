using System.Net;
using Docker.DotNet;
using Whalefleet.Application.Common;

namespace Whalefleet.Application.Docker;

/// <summary>
/// 清理（prune）类 Docker 调用的共用外壳。
/// </summary>
/// <remarks>
/// <para><b>为什么清理要单独一层</b>：守护进程对「已经有清理在进行」回 <c>409</c>，
/// 而 <see cref="DockerErrorTranslator"/> 的兜底分支会把它归成 <c>1402</c>/<b>500</b>——
/// 「服务端故障」既不属实也没法指导用户（正确的反应是<b>稍后重试</b>）。
/// 这里把它单独接住并改写成 <c>1404</c>/409。</para>
/// <para><b>这不是「资源冲突」那类 409</b>：<c>1404</c> 在本项目里的语义是
/// 「资源当前状态不允许该操作」。并发清理确实属于这一类——
/// 上一次清理还没结束，操作就发起不了。</para>
/// <para><b>其余异常仍走同一张映射表</b>：404 → <c>1401</c>、其它 → <c>1402</c>、
/// 超时 → <c>1403</c>、网络不通 → <c>1301</c>。</para>
/// </remarks>
internal static class DockerPrune
{
    /// <summary>
    /// 执行清理类 Docker 调用，并把「已有清理在进行」翻译成 <c>1404</c>。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    /// <param name="operation">Docker 调用。</param>
    /// <param name="resourceDescription">错误消息里的资源描述（如「镜像清理」）。</param>
    /// <param name="cancellationToken">取消令牌（用于区分「调用方取消」与「超时」）。</param>
    /// <returns>Docker 调用的返回值。</returns>
    /// <exception cref="BusinessException">翻译后的业务错误。</exception>
    /// <exception cref="OperationCanceledException">调用方主动取消。</exception>
    internal static async Task<T> ExecuteAsync<T>(
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
            // 调用方主动取消：继续抛，框架会当成「请求中止」而不是故障。
            throw;
        }
        catch (Exception exception) when (exception is DockerApiException { StatusCode: HttpStatusCode.Conflict })
        {
            throw new BusinessException(
                ErrorCodes.DockerResourceConflict,
                "另一项清理操作正在进行中，请稍后再试",
                exception);
        }
        catch (Exception exception) when (exception is not BusinessException)
        {
            throw DockerErrorTranslator.TranslateException(exception, resourceDescription);
        }
    }
}
