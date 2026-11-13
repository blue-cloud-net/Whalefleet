namespace Whalefleet.Application.Operations;

/// <summary>
/// 编排操作的结果摘要。
/// </summary>
/// <param name="Created">新建的服务数。</param>
/// <param name="Recreated">因配置变化而重建的服务数。</param>
/// <param name="Removed">被清掉的孤儿服务数。</param>
/// <param name="SkippedOrphans">被保留（用户选择不清）的孤儿服务数。</param>
/// <param name="Started">启动的容器数。</param>
/// <param name="Stopped">停止的容器数。</param>
/// <param name="NetworksCreated">新建的网络数。</param>
/// <param name="VolumesCreated">新建的卷数。</param>
/// <param name="VolumesRemoved">删除的卷数（只有显式要求时才非零）。</param>
public sealed record ComposeOperationResult(
    int Created,
    int Recreated,
    int Removed,
    int SkippedOrphans,
    int Started,
    int Stopped,
    int NetworksCreated,
    int VolumesCreated,
    int VolumesRemoved);
