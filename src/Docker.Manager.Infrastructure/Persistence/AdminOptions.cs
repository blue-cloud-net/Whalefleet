namespace Docker.Manager.Infrastructure.Persistence;

/// <summary>
/// 初始管理员引导配置（配置节 <c>Admin</c>）。
/// </summary>
/// <remarks>
/// <para><b>只在 <c>users</c> 表为空时使用</b>：一旦系统里有了账号，本节的取值就被忽略。
/// 因此它不是一个"管理员密码配置项"，而是<b>一次性引导参数</b>——设置完成后应当从环境里移除，
/// 避免密码长期以明文形式留在部署配置中。</para>
/// <para><b>缺失不等于启动失败</b>：表为空且本节未配置时只记 Warning 并继续启动。
/// 若强行要求必须配置，本地开发就得先设两个环境变量才能起服务；而"没有账号"这件事
/// 在登录时会有明确表现为"凭据错误"，不会静默成安全漏洞。</para>
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
