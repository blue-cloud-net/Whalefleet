using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Docker.Manager.Api.Configuration;

/// <summary>
/// 单页应用（SPA）静态资源托管选项（配置节 <c>Spa</c>）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：0.0.15 交付的镜像要把前端产物一并打进去，
/// 由 API 进程同源托管——这样部署只有<b>一个</b>容器、一个端口，
/// 不必再让用户自己解决「前端 dist 放哪、谁来做反向代理、CORS 怎么配」。</para>
/// <para><b>为什么默认关闭</b>：开发期前端由 Vite 提供（5173 端口），
/// API 只提供接口。开发环境误开会让 Vite 的页面被 API 的 wwwroot 顶掉，
/// 表现为「改了前端却没生效」这类极难定位的问题。</para>
/// </remarks>
public sealed class SpaOptions
{
    /// <summary>
    /// 配置节名。
    /// </summary>
    public const string SectionName = "Spa";

    /// <summary>
    /// 是否托管前端产物。
    /// </summary>
    /// <remarks>
    /// 生产镜像内为 <see langword="true"/>；开发环境保持关闭。
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// 前端产物目录（相对内容根目录，或绝对路径）。
    /// </summary>
    /// <remarks>
    /// 默认指向 <c>wwwroot</c>——发布时把 <c>dist</c> 复制进去即可，
    /// 不必在配置里写死容器内路径。
    /// </remarks>
    public string RootPath { get; set; } = "wwwroot";

    /// <summary>
    /// SPA 入口文件名。
    /// </summary>
    public string IndexFile { get; set; } = "index.html";
}