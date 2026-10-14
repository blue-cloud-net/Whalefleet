namespace Whalefleet.Core.Common.Auditing;

/// <summary>
/// 带"创建人"审计的对象：在 <see cref="IHasCreationTime"/> 基础上补充创建者标识。
/// </summary>
/// <typeparam name="TUserKey">创建者主键类型（通常是用户实体的 <c>TId</c>，如 <see cref="Guid"/> 或 <see cref="long"/>）。</typeparam>
/// <remarks>
/// <para><b>类型参数不加约束</b>，以兼容 <see cref="string"/> 等引用类型主键。</para>
/// <para><b>"无创建者"的表达</b>：由于 <c>TUserKey</c> 未加约束，C# 语法 <c>TUserKey?</c> 对值类型
/// 会退化为 <c>TUserKey</c> 本身、无法表达空值。本项目因此统一采用<b>默认值哨兵</b>——
/// <c>default(TUserKey)</c>（<c>null</c> / <c>Guid.Empty</c> / <c>0</c>）表示
/// <b>"系统操作"，即无登录用户</b>。消费方判断时用 <c>EqualityComparer&lt;TUserKey&gt;.Default</c> 比对。</para>
/// </remarks>
public interface ICreationAuditedObject<TUserKey> : IHasCreationTime
{
    /// <summary>
    /// 创建者标识；<c>default(TUserKey)</c> 表示系统操作（无登录用户）。
    /// </summary>
    TUserKey CreatorId { get; }
}
