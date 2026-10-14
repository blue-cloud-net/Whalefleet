namespace Whalefleet.Api.Configuration;

/// <summary>
/// CORS 白名单配置。
/// </summary>
/// <remarks>
/// <para>对应配置节 <c>Cors</c>。默认值为空数组，表示<b>完全拒绝跨域</b>——
/// 这是本项目的既定安全基线（见 <c>docs/constraints.md</c> 的「CORS」小节）。</para>
/// <para>生产环境走同源部署（一体化镜像托管前端），开发环境走 Vite 代理
/// （<c>/api</c> → <c>http://localhost:8080</c>），两者都不需要 CORS。
/// 仅当确有外部系统需要跨域调用本 API 时，才显式填写白名单。</para>
/// <para><b>环境变量写法</b>：数组必须使用下标形式，例如
/// <c>CORS__ALLOWEDORIGINS__0=http://a.example</c>、<c>CORS__ALLOWEDORIGINS__1=http://b.example</c>。</para>
/// </remarks>
public sealed class CorsOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Cors";

    /// <summary>
    /// 允许跨域访问的来源白名单。
    /// </summary>
    /// <remarks>
    /// 每一项必须形如 <c>https://a.example</c>：包含 scheme，且<b>不能</b>以 <c>/</c> 结尾
    /// （浏览器与 ASP.NET Core 均要求 origin 不带尾斜杠）。不满足者会在启动时校验失败。
    /// </remarks>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// 校验单个 origin 的语法是否符合 ASP.NET Core CORS 的要求。
    /// </summary>
    /// <param name="origin">待校验的 origin 字符串。</param>
    /// <returns>合法返回 <c>true</c>；否则返回 <c>false</c>。</returns>
    public static bool IsValidOrigin(string origin) =>
        !String.IsNullOrWhiteSpace(origin)
        && !origin.EndsWith('/')
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
