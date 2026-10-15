using Microsoft.Extensions.Options;

namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 在 <see cref="JwtOptions"/> 绑定之后、校验之前补齐 JWT 签名密钥。
/// </summary>
/// <remarks>
/// <para><b>为什么用后置配置而不是「让调用方到处读密钥」</b>：<see cref="JwtTokenService"/> 在构造函数里
/// 就把密钥算成 <c>SigningCredentials</c>，而 <c>Api</c> 层的 JWT 中间件也在配置阶段读同一个
/// <see cref="JwtOptions"/>。若让它们各自去读文件，密钥来源就会散落在多处，
/// 迟早出现「签发用一个密钥、校验用另一个密钥」这种只在生产才暴露的错位。</para>
/// <para><b>为什么放在后置配置里而不是注册期同步读文件</b>：<see cref="IServiceProvider"/> 在
/// <c>AddInfrastructure</c> 阶段尚不可用，而文件读写需要日志与目录创建。
/// 后置配置由 <c>IOptions</c> 基础设施在首次 <c>.Value</c> 访问时调用，此时依赖齐备。</para>
/// <para><b>取值优先级</b>：环境变量 / 配置文件里的 <c>Jwt:Secret</c> 仍然最高优先——
/// 保留「多实例共用同一密钥」「密钥轮换」「测试注入」这三个口子。
/// 只有在它缺失时才回落到数据目录下的密钥文件。</para>
/// <para><b>长度校验放在这里而不是校验器里</b>：校验器（<c>Validate</c>）在密钥缺失时应当<b>通过</b>
/// ——因为本类会把它补上；若校验器仍要求非空，自动生成路径会被它自己挡死。</para>
/// </remarks>
internal sealed class JwtSecretPostConfigure : IPostConfigureOptions<JwtOptions>
{
    private readonly ISecretProvider _secretProvider;

    /// <summary>
    /// 初始化后置配置。
    /// </summary>
    /// <param name="secretProvider">密钥提供者。</param>
    /// <exception cref="ArgumentNullException"><paramref name="secretProvider"/> 为 <see langword="null"/>。</exception>
    public JwtSecretPostConfigure(ISecretProvider secretProvider)
    {
        ArgumentNullException.ThrowIfNull(secretProvider);

        _secretProvider = secretProvider;
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, JwtOptions options)
    {
        // 已有显式配置（环境变量 / appsettings）时不介入：
        // 部署方显式给出的密钥必须原样生效，哪怕数据目录里已存在自动生成的那份。
        if (!String.IsNullOrWhiteSpace(options.Secret))
        {
            return;
        }

        options.Secret = _secretProvider.GetJwtSecret();
    }
}
