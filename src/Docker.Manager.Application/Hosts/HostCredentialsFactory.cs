using System.Text.Json;
using Docker.Manager.Application.Abstractions;
using Docker.Manager.Application.Common;
using Docker.Manager.Core.Hosts;

namespace Docker.Manager.Application.Hosts;

/// <summary>
/// 把请求里的凭据输入转成 <see cref="HostCredentials"/>（写入 <c>auth_config_json</c> 的形态）。
/// </summary>
/// <remarks>
/// <para><b>校验规则只有一处</b>：这里不重复实现「TLS 必须有三个路径」这类规则，而是把组装好的
/// JSON 交给运行期凭据类型（<c>DockerTlsCredentials.Parse</c> / <c>DockerBasicCredentials.Parse</c>）
/// 试解析一次。规则只有一份，不会出现「保存时说合法、连接时说非法」。</para>
/// <para><b>错误码要转成 <c>1000</c></b>：解析失败在运行期意味着「这台主机的凭据不可用」（<c>1301</c>），
/// 但在创建 / 更新请求里它是**参数校验失败**——同一份数据在不同时点的正确归类不同，
/// 用户看到的应该是「你填错了」而不是「连不上」。</para>
/// </remarks>
internal static class HostCredentialsFactory
{
    /// <summary>
    /// 序列化凭据输入时使用的选项：camelCase，与 <c>auth_config_json</c> 的字段命名一致。
    /// </summary>
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 由请求中的凭据输入构建凭据值对象。
    /// </summary>
    /// <param name="tlsConfig">TLS 输入。</param>
    /// <param name="basicAuth">Basic 输入。</param>
    /// <returns>凭据；两者都未提供时返回 <see cref="HostCredentials.None"/>。</returns>
    /// <exception cref="BusinessException">
    /// 两者同时提供，或凭据字段不完整（错误码 <c>1000</c>）。
    /// </exception>
    public static HostCredentials From(TlsConfigInput? tlsConfig, BasicAuthInput? basicAuth)
    {
        if (tlsConfig is not null && basicAuth is not null)
        {
            throw BusinessException.Validation("tlsConfig 与 basicAuth 互斥，只能提供其中之一");
        }

        if (tlsConfig is not null)
        {
            return Build(DockerHostAuthKind.Tls, tlsConfig, json => DockerTlsCredentials.Parse(json), "tlsConfig");
        }

        if (basicAuth is not null)
        {
            return Build(DockerHostAuthKind.Basic, basicAuth, json => DockerBasicCredentials.Parse(json), "basicAuth");
        }

        return HostCredentials.None;
    }

    private static HostCredentials Build<TInput>(
        DockerHostAuthKind kind,
        TInput input,
        Func<string, object> validate,
        string fieldName)
    {
        var json = JsonSerializer.Serialize(input, _options);

        try
        {
            _ = validate(json);

            return HostCredentials.FromJson(kind, json);
        }
        catch (BusinessException exception)
        {
            // 把「运行期的不可用」翻译成「请求校验失败」，并指明是哪个字段。
            throw BusinessException.Validation($"{fieldName} 非法：{exception.Message}");
        }
    }
}
