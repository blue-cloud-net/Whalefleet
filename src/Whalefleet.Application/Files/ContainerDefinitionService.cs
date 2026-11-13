using Docker.DotNet.Models;

using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Common;
using Whalefleet.Application.Containers;
using Whalefleet.Application.Docker;
using Whalefleet.Core.Permissions;

namespace Whalefleet.Application.Files;

/// <summary>
/// 生成容器的可复制定义（<c>docker run</c> 命令 / compose 片段）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：界面上能看到一个容器的全部配置，却没法把它们带走——
/// 想在同一台机器上再起一个、或搬到另一台机器，只能照着参数手抄。
/// 这个用例把 inspect 结果直接翻成命令，让「复制」成为可能。</para>
/// <para><b>读的是原始 inspect，不是详情的投影</b>：理由见
/// <see cref="ContainerRunSpecBuilder"/> 的类型注释——投影缺一半字段，
/// 用它生成命令会静默丢配置。</para>
/// </remarks>
public sealed class ContainerDefinitionService
{
    private readonly IDockerHostAccessor _accessor;
    private readonly ContainerAuthorizationService _authorization;
    private readonly ILogger<ContainerDefinitionService> _logger;

    /// <summary>
    /// 初始化容器定义用例。
    /// </summary>
    /// <param name="accessor">主机访问器。</param>
    /// <param name="authorization">容器权限判定。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public ContainerDefinitionService(
        IDockerHostAccessor accessor,
        ContainerAuthorizationService authorization,
        ILogger<ContainerDefinitionService> logger)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(logger);

        _accessor = accessor;
        _authorization = authorization;
        _logger = logger;
    }

    /// <summary>
    /// 生成指定格式的容器定义。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    /// <param name="request">生成请求（容器标识与格式）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>格式与生成结果。</returns>
    /// <exception cref="BusinessException">
    /// 容器标识为空或格式非法（<c>1000</c>）、容器不存在（<c>1401</c>）、
    /// 主机不可达（<c>1301</c>）或 Docker 调用失败（<c>1402</c>）。
    /// </exception>
    /// <remarks>
    /// <b>只要求 <c>container.read</c></b>：生成结果里没有任何详情的<b>新</b>信息——
    /// 能打开详情页的人早就能看到这些参数。它为「写」提供的是<b>便利</b>而不是权限，
    /// 因此不该把门槛提到 <c>container.write</c>（那会让只读用户无法导出自己的部署方式）。
    /// </remarks>
    public async Task<ContainerRunSpec> BuildAsync(
        string hostId,
        BuildContainerRunSpecRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentNullException.ThrowIfNull(request);

        var containerId = request.ContainerId?.Trim();

        if (String.IsNullOrWhiteSpace(containerId))
        {
            throw BusinessException.Validation("containerId 不能为空");
        }

        var format = ContainerRunSpecFormats.Normalize(request.Format);

        containerId = await _authorization
            .EnsureAsync(hostId, containerId, Permission.ContainerRead, cancellationToken)
            .ConfigureAwait(false);

        var client = await _accessor.GetClientAsync(hostId, cancellationToken).ConfigureAwait(false);

        var container = await DockerErrorTranslator.ExecuteAsync(
            () => client.Containers.InspectContainerAsync(containerId, cancellationToken),
            $"容器 {containerId}",
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("主机 {HostId} 生成容器 {ContainerId} 的 {Format}", hostId, containerId, format);

        return format switch
        {
            ContainerRunSpecFormats.ComposeYaml => new ContainerRunSpec(
                format,
                ContainerRunSpecBuilder.BuildComposeFragment(container)),

            _ => new ContainerRunSpec(
                ContainerRunSpecFormats.DockerRun,
                ContainerRunSpecBuilder.BuildDockerRun(container)),
        };
    }
}

/// <summary>
/// 容器定义的输出格式。
/// </summary>
public static class ContainerRunSpecFormats
{
    /// <summary>
    /// <c>docker run</c> 命令。
    /// </summary>
    public const string DockerRun = "dockerRun";

    /// <summary>
    /// compose 服务片段（YAML）。
    /// </summary>
    public const string ComposeYaml = "composeYaml";

    /// <summary>
    /// 校验并规范化格式取值。
    /// </summary>
    /// <param name="format">原始取值。</param>
    /// <returns>规范化的格式。</returns>
    /// <exception cref="BusinessException">格式不受支持（<c>1000</c>）。</exception>
    /// <remarks>
    /// 缺省取 <c>dockerRun</c>：那是「粘进终端就能跑」的形式，
    /// 也是绝大多数人点这个按钮想要的东西。
    /// </remarks>
    public static string Normalize(string? format)
    {
        if (String.IsNullOrWhiteSpace(format))
        {
            return DockerRun;
        }

        if (String.Equals(format.Trim(), DockerRun, StringComparison.OrdinalIgnoreCase))
        {
            return DockerRun;
        }

        if (String.Equals(format.Trim(), ComposeYaml, StringComparison.OrdinalIgnoreCase))
        {
            return ComposeYaml;
        }

        throw BusinessException.Validation($"不支持的定义格式「{format}」，只支持 dockerRun 与 composeYaml");
    }
}

/// <summary>
/// 容器定义生成请求。
/// </summary>
public sealed record BuildContainerRunSpecRequest
{
    /// <summary>
    /// 容器标识；必填。
    /// </summary>
    public string? ContainerId { get; init; }

    /// <summary>
    /// 输出格式（<c>dockerRun</c> / <c>composeYaml</c>）；省略为 <c>dockerRun</c>。
    /// </summary>
    public string? Format { get; init; }
}

/// <summary>
/// 容器定义生成结果。
/// </summary>
/// <param name="Format">实际使用的格式。</param>
/// <param name="Content">生成的文本。</param>
/// <remarks>
/// <b>回带格式而不是让调用方自己记着</b>：请求里的格式可省略（取默认值），
/// 界面要按<b>实际</b>格式选语法高亮与「复制」按钮的说明文字。
/// </remarks>
public sealed record ContainerRunSpec(
    string Format,
    string Content);
