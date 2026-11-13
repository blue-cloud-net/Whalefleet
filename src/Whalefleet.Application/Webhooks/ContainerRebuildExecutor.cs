using Docker.DotNet;
using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.Operations;

namespace Whalefleet.Application.Webhooks;

/// <summary>
/// 容器重建的结果摘要。
/// </summary>
/// <param name="ContainerId">重建后的新容器 ID。</param>
/// <param name="ContainerName">容器名（重建前后不变）。</param>
/// <param name="ReplacedContainerId">被让位的旧容器 ID（回滚的后手）。</param>
/// <param name="Running">新容器是否已启动。</param>
/// <param name="Warnings">守护进程给出的警告。</param>
public sealed record ContainerRecreateResult(
    string ContainerId,
    string ContainerName,
    string ReplacedContainerId,
    bool Running,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 容器重建的执行器（后台任务用）。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个执行器</b>：重建容器的动作与「重新应用 Compose 项目」
/// 完全不同——前者只动一个容器，后者动一整套网络 / 卷 / 容器。
/// 混在一个执行器里会让两者的失败语义、审计口径与回滚逻辑纠缠在一起。</para>
/// <para><b>按名字找容器</b>：令牌绑的是容器名（重建后 ID 会变），
/// 因此这里必须用名字反查；找不到就是「容器不存在」——
/// 典型场景是容器在令牌生成之后被删了或改了名。</para>
/// </remarks>
public sealed class ContainerRebuildExecutor
{
    private readonly IDockerHostAccessor _accessor;
    private readonly ContainerService _containers;
    private readonly ILogger<ContainerRebuildExecutor> _logger;

    /// <summary>
    /// 初始化执行器。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="containers">容器用例（复用它的重建内核与授权搬移）。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerRebuildExecutor(
        IDockerHostAccessor accessor,
        ContainerService containers,
        ILogger<ContainerRebuildExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _containers = containers;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次容器重建。
    /// </summary>
    /// <param name="request">操作请求（资源名 = 容器名）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重建结果摘要。</returns>
    /// <exception cref="BusinessException">容器不存在（<c>1401</c>）或重建失败。</exception>
    public async Task<ContainerRecreateResult> ExecuteAsync(
        ResourceOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = await _accessor.GetClientAsync(request.HostName, cancellationToken).ConfigureAwait(false);

        var containerId = await ResolveContainerIdAsync(
            client,
            request.ResourceName,
            cancellationToken).ConfigureAwait(false);

        var recreated = await _containers
            .RecreateByWebhookAsync(request.HostName, containerId, request.Started, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "主机 {HostName} 的容器 {ContainerName} 由 Webhook 重建完成（新容器 {NewId}）",
            request.HostName,
            recreated.Name,
            recreated.ContainerId);

        return new ContainerRecreateResult(
            recreated.ContainerId,
            recreated.Name,
            recreated.ReplacedContainerId ?? String.Empty,
            recreated.Running,
            recreated.Warnings);
    }

    /// <summary>
    /// 按容器名反查容器 ID。
    /// </summary>
    /// <param name="client">Docker 客户端。</param>
    /// <param name="containerName">容器名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>容器 ID。</returns>
    /// <exception cref="BusinessException">容器不存在（<c>1401</c>）。</exception>
    private static async Task<string> ResolveContainerIdAsync(
        IDockerClient client,
        string containerName,
        CancellationToken cancellationToken)
    {
        var containers = await client.Containers
            .ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                },
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var container in containers)
        {
            foreach (var name in container.Names)
            {
                if (String.Equals(name.TrimStart('/'), containerName, StringComparison.Ordinal))
                {
                    return container.ID;
                }
            }
        }

        throw new BusinessException(
            ErrorCodes.DockerResourceNotFound,
            $"容器「{containerName}」不存在——它可能已被删除或改名（改名会让 Webhook 令牌失效）");
    }
}
