namespace Whalefleet.Infrastructure.Security;

/// <summary>
/// 密钥提供者的内部抽象：把「密钥从哪来」这件事与调用方解耦。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：<see cref="JwtSecretPostConfigure"/> 由 <c>Options</c> 基础设施调用，
/// 它的入参固定为 <see cref="JwtOptions"/>，无法再注入别的依赖；
/// 而它确实需要一个「能拿到密钥」的东西。把这一步抽成接口后，
/// 测试可以用一个返回固定值的假实现，而不必在磁盘上真的写文件。</para>
/// <para><b>归属本层而非 <c>Application/Abstractions</c></b>：这是纯部署期的基础设施关注点
/// （数据目录、文件权限），不是任何业务用例要用到的能力。</para>
/// </remarks>
internal interface ISecretProvider
{
    /// <summary>
    /// 取得 JWT 签名密钥；尚未生成时会生成并持久化。
    /// </summary>
    /// <returns>签名密钥原文。</returns>
    string GetJwtSecret();
}
