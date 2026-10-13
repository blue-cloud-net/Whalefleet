namespace Docker.Manager.Application.Auth;

/// <summary>
/// 登录请求。
/// </summary>
/// <remarks>
/// <para>字段可空是刻意的：请求体由客户端决定，缺字段是<b>常态</b>而非异常。
/// 若把类型声明为非空 <see cref="string"/>，则"客户端没传 password"会表现为运行时
/// <see cref="NullReferenceException"/>（500），而正确的语义是参数校验失败（1000）。
/// 因此这里如实声明为可空，由 <see cref="AuthService.LoginAsync"/> 显式校验并给出可读消息。</para>
/// <para>刻意<b>不</b>在 DTO 上加 <c>[Required]</c> 数据注解：那会把校验分散到两处
/// （注解 + 服务），且注解产生的消息无法与业务文案统一。校验只在一个地方做。</para>
/// </remarks>
public sealed record LoginRequest
{
    /// <summary>
    /// 用户名。
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// 明文密码（仅在本次请求内存中存在，绝不落库或写日志）。
    /// </summary>
    public string? Password { get; init; }
}
