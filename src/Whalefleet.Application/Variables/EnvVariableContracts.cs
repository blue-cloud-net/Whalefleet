namespace Whalefleet.Application.Variables;

/// <summary>
/// 环境变量的作用域。
/// </summary>
/// <remarks>
/// <b>只有两档</b>：全局（对所有 Compose 项目生效）与项目级（只对该项目的文件生效）。
/// 刻意<b>不</b>提供「主机级」——那会让「变量能影响到哪些项目」这个问题多出一个中间层，
/// 而界面上无处安放它的入口。
/// </remarks>
public enum EnvVariableScope
{
    /// <summary>
    /// 全局变量：对所有 Compose 项目生效。
    /// </summary>
    Global,

    /// <summary>
    /// 项目级变量：只对某个 Compose 项目的文件生效。
    /// </summary>
    Project,
}

/// <summary>
/// 环境变量的对外投影。
/// </summary>
/// <param name="Name">变量名。</param>
/// <param name="Scope">作用域。</param>
/// <param name="HostName">所属主机名（项目级变量才有；全局变量为 <see langword="null"/>）。</param>
/// <param name="ComposeName">所属 Compose 项目名（项目级变量才有）。</param>
/// <param name="IsSecret">是否为密钥。</param>
/// <param name="WebhookOverridable">是否允许被 Webhook 的查询串覆盖。</param>
/// <param name="Description">备注。</param>
/// <param name="HasValue">是否已设置值。</param>
/// <param name="ValueMasked">
/// 值的掩码（<b>固定文本</b>，与真实值无关——连长度都不泄漏）。
/// </param>
/// <remarks>
/// <para><b>为什么值永不返回</b>：普通变量与密钥一视同仁。
/// 「有权限的人能看见明文」会让「密钥」这个分类失去意义——
/// 而调用方拿到明文就意味着它会进浏览器、进日志、进备份文件。</para>
/// <para><b>要改值怎么办</b>：编辑时<b>留空表示不修改</b>，显式清空走 <c>clearValue</c>。
/// 不能靠「传空串」表达清空——掩码串会被用户原样提交回库，
/// 那等于把掩码当成了真值。</para>
/// </remarks>
public sealed record EnvVariableDto(
    string Name,
    EnvVariableScope Scope,
    string? HostName,
    string? ComposeName,
    bool IsSecret,
    bool WebhookOverridable,
    string? Description,
    bool HasValue,
    string ValueMasked);

/// <summary>
/// Compose 项目可见的变量清单。
/// </summary>
/// <param name="Project">项目级变量。</param>
/// <param name="Inherited">从全局继承的变量（同名时<b>被项目级覆盖</b>）。</param>
/// <param name="Undefined">文件里引用了但尚未定义的变量名。</param>
/// <remarks>
/// <b>为什么要分开列而不是合成一份</b>：界面需要区分
/// 「这个变量在本项目里被覆盖了」与「这个变量来自全局」——
/// 合成一份会让用户不知道自己改的值到底有没有生效。
/// </remarks>
public sealed record ComposeVariablesResponse(
    IReadOnlyList<EnvVariableDto> Project,
    IReadOnlyList<EnvVariableDto> Inherited,
    IReadOnlyList<string> Undefined);

/// <summary>
/// 新建变量请求。
/// </summary>
public sealed record CreateEnvVariableRequest
{
    /// <summary>
    /// 变量名；必填。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 变量值；必填。
    /// </summary>
    /// <remarks>
    /// <b>空串视为「未设置值」</b>（与「不修改」不同——新建时不存在「不修改」）。
    /// </remarks>
    public string? Value { get; init; }

    /// <summary>
    /// 是否为密钥；省略为 <see langword="false"/>。
    /// </summary>
    public bool? IsSecret { get; init; }

    /// <summary>
    /// 是否允许被 Webhook 的查询串覆盖；省略为 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// <b>密钥恒为否</b>：Webhook 是匿名入口，允许远程改密钥等于开了一条提权通道。
    /// </remarks>
    public bool? WebhookOverridable { get; init; }

    /// <summary>
    /// 备注；可为空。
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// 更新变量请求。
/// </summary>
public sealed record UpdateEnvVariableRequest
{
    /// <summary>
    /// 变量名；必填。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 新值；<b>省略表示不修改</b>。
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// 是否为密钥；省略表示不修改。
    /// </summary>
    public bool? IsSecret { get; init; }

    /// <summary>
    /// 是否允许被 Webhook 覆盖；省略表示不修改。
    /// </summary>
    public bool? WebhookOverridable { get; init; }

    /// <summary>
    /// 备注；省略表示不修改，传空串表示清空备注。
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// 是否显式清空值；省略为 <see langword="false"/>。
    /// </summary>
    public bool? ClearValue { get; init; }
}

/// <summary>
/// 删除变量请求。
/// </summary>
public sealed record RemoveEnvVariableRequest
{
    /// <summary>
    /// 变量名；必填。
    /// </summary>
    public string? Name { get; init; }
}

/// <summary>
/// 列出某个 Compose 项目可见的变量。
/// </summary>
public sealed record ListComposeEnvVariablesRequest
{
    /// <summary>
    /// Compose 项目名；必填。
    /// </summary>
    public string? ComposeName { get; init; }
}

/// <summary>
/// Compose 项目级变量的操作请求（新增 / 修改 / 删除共用）。
/// </summary>
public sealed record ComposeEnvVariableRequest
{
    /// <summary>
    /// Compose 项目名；必填。
    /// </summary>
    public string? ComposeName { get; init; }

    /// <summary>
    /// 变量名；必填。
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// 变量值；新增时必填，修改时省略表示不修改。
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// 是否为密钥；省略表示不修改。
    /// </summary>
    public bool? IsSecret { get; init; }

    /// <summary>
    /// 是否允许被 Webhook 覆盖；省略表示不修改。
    /// </summary>
    public bool? WebhookOverridable { get; init; }

    /// <summary>
    /// 备注；省略表示不修改。
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// 是否显式清空值；省略为 <see langword="false"/>。
    /// </summary>
    public bool? ClearValue { get; init; }
}
