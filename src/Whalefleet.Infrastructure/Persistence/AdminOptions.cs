namespace Whalefleet.Infrastructure.Persistence;

/// <summary>
/// 初始管理员引导配置（配置节 <c>Admin</c>）。
/// </summary>
/// <remarks>
/// <para><b>只在 <c>users</c> 表为空时使用</b>：一旦系统里有了账号，本节的取值就被忽略。
/// 因此它不是一个"管理员密码配置项"，而是<b>一次性引导参数</b>——设置完成后应当从环境里移除，
/// 避免密码长期以明文形式留在部署配置中。</para>
/// <para><b>缺失时自动生成密码</b>：<c>Password</c> 为空时，
/// <c>AdminBootstrapper</c> 会生成一个满足强度策略的随机密码并写入数据目录下的
/// <c>initial-admin.txt</c>（权限 <c>0600</c>，<b>不进日志</b>），因此零配置即可完成部署。
/// 只配了 <c>User</c> 而没配 <c>Password</c> 会被视为笔误：用户名回落为默认值并记 Warning。</para>
/// </remarks>
public sealed class AdminOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Admin";

    /// <summary>
    /// 初始管理员用户名（环境变量 <c>ADMIN__USER</c>）。
    /// </summary>
    public string? User { get; set; }

    /// <summary>
    /// 初始管理员密码（环境变量 <c>ADMIN__PASSWORD</c>）。
    /// </summary>
    /// <remarks>
    /// <b>绝不允许写入日志或持久化到配置文件</b>；本类刻意不覆写 <c>ToString</c> 之外的任何行为，
    /// 也<b>不得</b>在日志里以结构化属性的形式传出本对象。
    /// </remarks>
    public string? Password { get; set; }

    /// <summary>
    /// 是否允许在 <c>users</c> 表为空时自动创建初始管理员。默认为 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// <para>设为 <see langword="false"/>（环境变量 <c>ADMIN__AUTOBOOTSTRAP=false</c>）可彻底关闭自举，
    /// 此时既不会用配置的凭据创建账号，也不会自动生成密码。</para>
    /// <para>适用场景：数据卷由外部预置（账号已随备份恢复、或由运维脚本直接写库），
    /// 此时自举会与既有账号抢 <c>admin</c> 这个用户名而使启动失败。</para>
    /// </remarks>
    public bool AutoBootstrap { get; set; } = true;

    /// <summary>
    /// 是否两项都已提供。
    /// </summary>
    /// <remarks>
    /// 要求<b>两项齐全</b>：只配用户名或只配密码属于配置失误，此时应当视为"未配置"
    /// 并给出 Warning，而不是拿一个空密码去创建账号。
    /// </remarks>
    public bool IsConfigured =>
        !String.IsNullOrWhiteSpace(this.User) && !String.IsNullOrWhiteSpace(this.Password);

    /// <summary>
    /// 返回不含密码的描述，便于安全地写入日志。
    /// </summary>
    /// <returns>形如 <c>AdminOptions { User = admin, Password = *** }</c> 的描述。</returns>
    public override string ToString() =>
        $"{nameof(AdminOptions)} {{ User = {this.User ?? "(未设置)"}, Password = " +
        (String.IsNullOrEmpty(this.Password) ? "(未设置)" : "***") + " }";
}
