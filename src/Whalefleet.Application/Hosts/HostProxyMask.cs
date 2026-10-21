namespace Whalefleet.Application.Hosts;

/// <summary>
/// 代理地址的凭据遮蔽。
/// </summary>
/// <remarks>
/// <para><b>为什么单独一个类型</b>：它是「响应里绝不出现口令」这条规则的<b>唯一</b>实现点，
/// 而被遮蔽的字段有三个（HTTP / HTTPS 代理，以及将来可能出现的镜像仓库代理）。
/// 埋在 <c>HostQueryService</c> 的私有方法里既测不到，也容易被下一个调用方复制一份走样。</para>
/// <para><b>为什么必须遮</b>：<c>http://user:pass@proxy:3128</c> 是代理的标准写法，而它会原样
/// 出现在 <c>/info</c> 里。一旦进入 API 响应，就会随浏览器缓存、日志采集器与用户截图一起扩散——
/// 与「密码永不进日志」同源。</para>
/// </remarks>
public static class HostProxyMask
{
    /// <summary>
    /// 遮蔽 URL 中的 userinfo（用户名 / 口令）。
    /// </summary>
    /// <param name="value">原始代理地址。</param>
    /// <returns>遮蔽后的地址；未设置时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para><b>解析失败时原样返回</b>：<c>noProxy</c> 之类的值本就不一定是 URL，
    /// 而一台主机的概览不该因为一个解析不了的代理字符串而失败。
    /// 原样返回也不会泄密：连 URI 都解析不出来的字符串里不可能有被正确识别的 userinfo。</para>
    /// <para><b>没有 userinfo 时也原样返回</b>：重建 URL 会给 <c>http://proxy:3128</c>
    /// 补上一个尾部斜杠，而那个斜杠对排查问题毫无帮助。</para>
    /// </remarks>
    public static string? Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
        {
            return value;
        }

        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }
            .Uri
            .GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }
}
