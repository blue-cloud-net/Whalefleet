namespace Whalefleet.Core.Alerting;

/// <summary>
/// 某条通知对某个用户的已读 / 已删除状态。
/// </summary>
/// <remarks>
/// <para><b>为什么已读要按用户存</b>：通知是全局事件，但「我读过了」是个人状态。
/// 做成全局已读会让「同事点开铃铛」把所有人的红点都清掉——多人协作时这是错误的事实。</para>
/// <para><b>删除也是每用户状态</b>：删除只是把该用户的这条通知隐藏，不影响别人。
/// 因此没有物理删除入口，只有 <see cref="DismissedAt"/>。</para>
/// </remarks>
public sealed class NotificationUserState
{
    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Create"/>。
    /// </summary>
    private NotificationUserState()
    {
    }

    /// <summary>
    /// 通知主键。
    /// </summary>
    public Guid NotificationId { get; private set; }

    /// <summary>
    /// 用户主键。
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// 已读时刻（UTC）；未读为 <see langword="null"/>。
    /// </summary>
    public DateTime? ReadAt { get; private set; }

    /// <summary>
    /// 删除（对本人隐藏）时刻（UTC）；未删除为 <see langword="null"/>。
    /// </summary>
    public DateTime? DismissedAt { get; private set; }

    /// <summary>
    /// 构造一条用户状态。
    /// </summary>
    public static NotificationUserState Create(Guid notificationId, Guid userId)
    {
        if (notificationId == Guid.Empty || userId == Guid.Empty)
        {
            throw new ArgumentException("通知主键与用户主键均不能为空");
        }

        return new NotificationUserState
        {
            NotificationId = notificationId,
            UserId = userId,
        };
    }
}
