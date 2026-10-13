namespace Docker.Manager.Api.Security;

/// <summary>
/// 「当前主机」的写入侧契约（Api 层内部）。
/// </summary>
/// <remarks>
/// <para><b>读写为何分开</b>：<see cref="Application.Abstractions.ICurrentHost"/> 保持只读，
/// 业务代码「能读不能改」；只有 <c>CurrentHostMiddleware</c> 需要在请求开始时写入。
/// 若把 setter 放进那个公共接口，任何拿到它的实现类的地方都能改写当前主机，
/// 而「谁有权决定当前是哪台主机」这本该只有一个答案。</para>
/// <para><b>为什么中间件要按方法注入取它</b>：中间件是 Singleton，构造注入 Scoped 服务会构成
/// 捕获依赖；<c>InvokeAsync</c> 的参数由框架从<b>当前请求</b>的作用域解析，因此这样就与控制器
/// 拿到的是同一个实例。</para>
/// </remarks>
internal interface ICurrentHostAccessor
{
    /// <summary>
    /// 记录本次请求的主机标识（仅由 <c>CurrentHostMiddleware</c> 调用）。
    /// </summary>
    /// <param name="hostId">主机标识（主机名）。</param>
    void Set(string hostId);
}
