namespace Whalefleet.Api.Security;

/// <summary>
/// 标记「凭短时票据下载文件」的端点：它<b>不</b>走主机作用域的常规鉴权，
/// 票据本身就是凭证。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：大文件下载走的是<b>浏览器原生导航</b>
/// （<c>&lt;a download&gt;</c> / <c>window.open</c>），
/// 而原生导航<b>带不了 <c>Authorization</c> 头</b>。
/// 把 access token 拼进 URL 又会把它写进浏览历史、反代访问日志与 <c>Referer</c> 头。</para>
/// <para><b>因此凭证是「票据」</b>：一张一次性的短时串，只能取走那一个文件。
/// 它取代了「端点自己认票据」与「中间件认 Bearer」两套鉴权——
/// 下载端点走的是<b>第三条</b>路：<b>匿名端点 + 端点自己认票据</b>。</para>
/// <para><b>为什么按端点元数据跳过而不是按路径形状</b>：按路径跳过（像 <c>/ws</c> 那样）
/// 意味着「以后再加一个带 token 的路径就得记得改这里」——
/// 漏改的后果是那个端点仍然要求 Bearer，于是浏览器导航拿到 401，
/// 症状是「点下载按钮就报错」，极难联想到是中间件的锅。
/// 而按端点元数据跳过，写端点的人必须显式标注才会被跳过——
/// <b>漏标注的后果是「端点仍然受保护」</b>（fail-closed），而不是「意外裸奔」。</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DownloadTicketAttribute : Attribute;
