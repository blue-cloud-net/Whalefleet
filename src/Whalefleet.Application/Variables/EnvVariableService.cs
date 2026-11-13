using Microsoft.Extensions.Logging;

using Whalefleet.Application.Abstractions;
using Whalefleet.Application.Abstractions.Auditing;
using Whalefleet.Application.Common;
using Whalefleet.Core.Auditing;
using Whalefleet.Core.Variables;

namespace Whalefleet.Application.Variables;

/// <summary>
/// 环境变量用例：全局变量与 Compose 项目级变量的增删改查，以及「注入 Compose 时取值」。
/// </summary>
/// <remarks>
/// <para><b>值永不离开这一层</b>：对外的投影只给掩码，
/// 明文只在 <see cref="ResolveAsync"/> 里解开并交给调用方注入 Compose。
/// 审计里也只有变量名，没有值——审计日志会被采集器上报到集中式系统。</para>
/// <para><b>取值优先级</b>：<b>Webhook 覆盖 &gt; 项目级变量 &gt; 全局变量</b>。
/// 同名时项目级覆盖全局级，这是 Compose 里「项目 .env 覆盖全局环境」的同一口径。</para>
/// <para><b>值的加密</b>：复用 <see cref="ISecretProtector"/>（与仓库口令同一把密钥）。
/// 密钥文件在数据卷上，必须随数据库一起备份——否则恢复后解不开。</para>
/// </remarks>
public sealed class EnvVariableService
{
    /// <summary>
    /// 值的固定掩码（<b>与真实值无关</b>，连长度都不泄漏）。
    /// </summary>
    public const string ValueMask = "••••••";

    private readonly IEnvVariableRepository _repository;
    private readonly ISecretProtector _protector;
    private readonly IAuditLogger _audit;
    private readonly ILogger<EnvVariableService> _logger;

    /// <summary>
    /// 初始化环境变量用例。
    /// </summary>
    /// <param name="repository">变量仓储。</param>
    /// <param name="protector">敏感文本加解密。</param>
    /// <param name="audit">审计写入。</param>
    /// <param name="logger">日志。</param>
    /// <exception cref="ArgumentNullException">任一依赖为 <see langword="null"/>。</exception>
    public EnvVariableService(
        IEnvVariableRepository repository,
        ISecretProtector protector,
        IAuditLogger audit,
        ILogger<EnvVariableService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _protector = protector;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// 列出全部全局变量。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全局变量列表（按名称升序）。</returns>
    public async Task<IReadOnlyList<EnvVariableDto>> ListGlobalAsync(
        CancellationToken cancellationToken = default)
    {
        var variables = await _repository.ListGlobalAsync(cancellationToken).ConfigureAwait(false);

        return [.. variables.Select(variable => ToDto(variable, hostName: null))];
    }

    /// <summary>
    /// 新建全局变量。
    /// </summary>
    /// <param name="request">请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的变量投影。</returns>
    /// <exception cref="BusinessException">
    /// 变量名非法（<c>1909</c>）、名称已占用（<c>1914</c>）或值非法（<c>1000</c>）。
    /// </exception>
    public async Task<EnvVariableDto> CreateGlobalAsync(
        CreateEnvVariableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireName(request.Name);
        var isSecret = request.IsSecret ?? false;

        await this.EnsureNameAvailableAsync(null, null, name, cancellationToken).ConfigureAwait(false);

        var variable = EnvVariable.CreateGlobal(
            Guid.NewGuid(),
            name,
            this.Protect(request.Value),
            isSecret,
            request.WebhookOverridable ?? false,
            request.Description);

        await _repository.AddAsync(variable, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(variable, hostName: null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("新建全局变量 {Name}（密钥={IsSecret}）", name, isSecret);

        return ToDto(variable, hostName: null);
    }

    /// <summary>
    /// 更新全局变量。
    /// </summary>
    /// <param name="request">请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新后的变量投影。</returns>
    /// <exception cref="BusinessException">
    /// 变量不存在（<c>1913</c>）或值非法（<c>1000</c>）。
    /// </exception>
    public async Task<EnvVariableDto> UpdateGlobalAsync(
        UpdateEnvVariableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireName(request.Name);
        var variable = await this.RequireVariableAsync(null, null, name, cancellationToken).ConfigureAwait(false);

        this.ApplyUpdate(
            variable,
            request.Value,
            request.IsSecret,
            request.WebhookOverridable,
            request.Description,
            request.ClearValue);

        await _repository.UpdateAsync(variable, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(variable, hostName: null, cancellationToken).ConfigureAwait(false);

        return ToDto(variable, hostName: null);
    }

    /// <summary>
    /// 删除全局变量（软删除）。
    /// </summary>
    /// <param name="request">请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="BusinessException">变量不存在（<c>1913</c>）。</exception>
    public async Task RemoveGlobalAsync(
        RemoveEnvVariableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = RequireName(request.Name);
        var variable = await this.RequireVariableAsync(null, null, name, cancellationToken).ConfigureAwait(false);

        await _repository.RemoveByIdAsync(variable.Id, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(variable, hostName: null, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("删除全局变量 {Name}", name);
    }

    /// <summary>
    /// 列出某个 Compose 项目可见的变量。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="hostName">所属主机名（用于投影与审计快照）。</param>
    /// <param name="composeName">Compose 项目名。</param>
    /// <param name="referencedVariables">文件里引用到的变量名；用于算出「未定义」清单。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>项目级变量 + 继承的全局变量 + 未定义变量名。</returns>
    public async Task<ComposeVariablesResponse> ListForComposeAsync(
        Guid hostId,
        string hostName,
        string composeName,
        IReadOnlyCollection<string>? referencedVariables = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(composeName);

        var visible = await _repository
            .ListVisibleAsync(hostId, composeName, cancellationToken)
            .ConfigureAwait(false);

        var project = visible.Where(variable => !variable.IsGlobal).ToList();
        var inherited = visible.Where(variable => variable.IsGlobal).ToList();

        var projectNames = project.Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);
        var declared = projectNames.Union(
            inherited.Select(variable => variable.Name),
            StringComparer.Ordinal);

        var undefined = (referencedVariables ?? [])
            .Where(name => !declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return new ComposeVariablesResponse(
            [.. project.Select(variable => ToDto(variable, hostName))],
            [.. inherited.Select(variable => ToDto(variable, hostName: null))],
            undefined);
    }

    /// <summary>
    /// 新建某个 Compose 项目自己的变量。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="hostName">所属主机名。</param>
    /// <param name="request">请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新建的变量投影。</returns>
    /// <exception cref="BusinessException">
    /// 变量名非法（<c>1909</c>）、名称已占用（<c>1914</c>）或值非法（<c>1000</c>）。
    /// </exception>
    public async Task<EnvVariableDto> CreateForComposeAsync(
        Guid hostId,
        string hostName,
        ComposeEnvVariableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);

        var composeName = RequireComposeName(request.ComposeName);
        var name = RequireName(request.Name);
        var isSecret = request.IsSecret ?? false;

        await this.EnsureNameAvailableAsync(hostId, composeName, name, cancellationToken).ConfigureAwait(false);

        var variable = EnvVariable.CreateForProject(
            Guid.NewGuid(),
            hostId,
            composeName,
            name,
            this.Protect(request.Value),
            isSecret,
            request.WebhookOverridable ?? false,
            request.Description);

        await _repository.AddAsync(variable, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(variable, hostName, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("新建主机 {HostId} 项目 {ComposeName} 的变量 {Name}", hostName, composeName, name);

        return ToDto(variable, hostName);
    }

    /// <summary>
    /// 更新某个 Compose 项目自己的变量。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="hostName">所属主机名。</param>
    /// <param name="request">请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>更新后的变量投影。</returns>
    /// <exception cref="BusinessException">变量不存在（<c>1913</c>）或值非法（<c>1000</c>）。</exception>
    public async Task<EnvVariableDto> UpdateForComposeAsync(
        Guid hostId,
        string hostName,
        ComposeEnvVariableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);

        var composeName = RequireComposeName(request.ComposeName);
        var name = RequireName(request.Name);

        var variable = await this
            .RequireVariableAsync(hostId, composeName, name, cancellationToken)
            .ConfigureAwait(false);

        this.ApplyUpdate(
            variable,
            request.Value,
            request.IsSecret,
            request.WebhookOverridable,
            request.Description,
            request.ClearValue);

        await _repository.UpdateAsync(variable, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(variable, hostName, cancellationToken).ConfigureAwait(false);

        return ToDto(variable, hostName);
    }

    /// <summary>
    /// 删除某个 Compose 项目自己的变量（软删除）。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="hostName">所属主机名。</param>
    /// <param name="request">请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="BusinessException">变量不存在（<c>1913</c>）。</exception>
    public async Task RemoveForComposeAsync(
        Guid hostId,
        string hostName,
        ComposeEnvVariableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);

        var composeName = RequireComposeName(request.ComposeName);
        var name = RequireName(request.Name);

        var variable = await this
            .RequireVariableAsync(hostId, composeName, name, cancellationToken)
            .ConfigureAwait(false);

        await _repository.RemoveByIdAsync(variable.Id, cancellationToken).ConfigureAwait(false);

        await this.RecordAuditAsync(variable, hostName, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("删除主机 {HostId} 项目 {ComposeName} 的变量 {Name}", hostName, composeName, name);
    }

    /// <summary>
    /// 解析出注入 Compose 用的变量表。
    /// </summary>
    /// <param name="hostId">所属主机主键（<c>hosts.id</c>）。</param>
    /// <param name="composeName">Compose 项目名。</param>
    /// <param name="overrides">
    /// 本次的覆盖项（通常来自 Webhook 的查询串）；可为空。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 变量名 → 值。<b>同名时项目级覆盖全局级</b>，覆盖项再覆盖两者。
    /// </returns>
    /// <exception cref="BusinessException">
    /// 覆盖项指向一个密钥（<c>1910</c>）——密钥<b>永远</b>不可被远程覆盖。
    /// </exception>
    /// <remarks>
    /// <b>为什么覆盖密钥要报错而不是忽略</b>：静默忽略会让 CI 以为口令已经改了，
    /// 而实际跑的还是旧值——那是最糟的组合（看起来成功，实际没生效）。
    /// 而「未声明的键」与「未勾选可覆盖的键」只是被忽略并回带名字，那不会造成误解。
    /// </remarks>
    public async Task<ResolvedEnvVariables> ResolveAsync(
        Guid hostId,
        string composeName,
        IReadOnlyDictionary<string, string>? overrides = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composeName);

        var visible = await _repository
            .ListVisibleAsync(hostId, composeName, cancellationToken)
            .ConfigureAwait(false);

        // 先铺全局、再铺项目级——项目级同名时自然覆盖全局级。
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var overridable = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var variable in visible.OrderBy(variable => variable.IsGlobal ? 0 : 1))
        {
            if (!variable.HasValue)
            {
                continue;
            }

            values[variable.Name] = this.Unprotect(variable.EncryptedValue!);
            overridable[variable.Name] = variable.WebhookOverridable && !variable.IsSecret;
        }

        var applied = new List<string>();
        var ignored = new List<string>();

        foreach (var (name, value) in overrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!overridable.TryGetValue(name, out var allowed))
            {
                // 未声明的变量：忽略。
                ignored.Add(name);
                continue;
            }

            if (!allowed)
            {
                // 声明了但不允许覆盖：分两种情况区别对待——
                // 密钥直接报错（静默忽略会造成「以为改了口令」的误解），
                // 普通变量只是没勾选可覆盖，忽略即可。
                if (visible.Any(variable =>
                        String.Equals(variable.Name, name, StringComparison.Ordinal) && variable.IsSecret))
                {
                    throw new BusinessException(
                        ErrorCodes.VariableNotOverridable,
                        $"变量「{name}」是密钥，不能被 Webhook 覆盖");
                }

                ignored.Add(name);
                continue;
            }

            values[name] = value;
            applied.Add(name);
        }

        applied.Sort(StringComparer.Ordinal);
        ignored.Sort(StringComparer.Ordinal);

        return new ResolvedEnvVariables(values, applied, ignored);
    }

    /// <summary>
    /// 应用一次更新请求。
    /// </summary>
    /// <param name="variable">变量实体。</param>
    /// <param name="value">新值；<see langword="null"/> 表示不修改。</param>
    /// <param name="isSecret">是否改为密钥；<see langword="null"/> 表示不修改。</param>
    /// <param name="webhookOverridable">是否改为可被 Webhook 覆盖；<see langword="null"/> 表示不修改。</param>
    /// <param name="description">备注；<see langword="null"/> 表示不修改。</param>
    /// <param name="clearValue">是否显式清空值。</param>
    private void ApplyUpdate(
        EnvVariable variable,
        string? value,
        bool? isSecret,
        bool? webhookOverridable,
        string? description,
        bool? clearValue)
    {
        if (clearValue is true)
        {
            variable.ClearValue();
        }
        else if (value is not null)
        {
            variable.SetValue(this.Protect(value));
        }

        if (isSecret is not null || webhookOverridable is not null || description is not null)
        {
            variable.UpdateMetadata(
                isSecret ?? variable.IsSecret,
                webhookOverridable ?? variable.WebhookOverridable,
                description ?? variable.Description);
        }
    }

    /// <summary>
    /// 把值加密；空串视为「未设置值」。
    /// </summary>
    /// <param name="value">明文。</param>
    /// <returns>密文；未设置值时为 <see langword="null"/>。</returns>
    private string? Protect(string? value)
    {
        if (String.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > EnvVariable.MaxValueLength)
        {
            throw new BusinessException(
                ErrorCodes.ValidationFailed,
                $"变量值过长（不得超过 {EnvVariable.MaxValueLength} 个字符）");
        }

        return _protector.Protect(value);
    }

    /// <summary>
    /// 解密变量值。
    /// </summary>
    /// <param name="encryptedValue">密文。</param>
    /// <returns>明文。</returns>
    private string Unprotect(string encryptedValue) => _protector.Unprotect(encryptedValue);

    /// <summary>
    /// 取变量实体，不存在则报 <c>1913</c>。
    /// </summary>
    /// <param name="hostId">所属主机主键；<see langword="null"/> 表示全局。</param>
    /// <param name="composeName">Compose 项目名；<see langword="null"/> 表示全局。</param>
    /// <param name="name">变量名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>变量实体。</returns>
    private async Task<EnvVariable> RequireVariableAsync(
        Guid? hostId,
        string? composeName,
        string name,
        CancellationToken cancellationToken)
    {
        var variable = await _repository
            .FindByScopeAsync(hostId, composeName, name, cancellationToken)
            .ConfigureAwait(false);

        if (variable is null)
        {
            throw new BusinessException(
                ErrorCodes.VariableNotFound,
                $"变量「{name}」不存在");
        }

        return variable;
    }

    /// <summary>
    /// 确保同一作用域内变量名未被占用。
    /// </summary>
    /// <param name="hostId">所属主机主键；<see langword="null"/> 表示全局。</param>
    /// <param name="composeName">Compose 项目名；<see langword="null"/> 表示全局。</param>
    /// <param name="name">变量名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task EnsureNameAvailableAsync(
        Guid? hostId,
        string? composeName,
        string name,
        CancellationToken cancellationToken)
    {
        var existing = await _repository
            .FindByScopeAsync(hostId, composeName, name, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            throw new BusinessException(
                ErrorCodes.VariableNameAlreadyExists,
                $"变量「{name}」已存在");
        }
    }

    /// <summary>
    /// 写一条审计（<b>只有变量名，没有值</b>）。
    /// </summary>
    /// <param name="variable">变量实体。</param>
    /// <param name="hostName">所属主机名；全局变量为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task RecordAuditAsync(
        EnvVariable variable,
        string? hostName,
        CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(
            new AuditLogDraft
            {
                Category = AuditCategory.Activity,
                Action = AuditActions.VariableUpdate,
                Outcome = AuditOutcome.Success,
                TargetType = AuditTargetTypes.Variable,
                TargetId = variable.Id.ToString("D"),
                TargetName = variable.Name,
                HostName = hostName,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 实体 → 投影。
    /// </summary>
    /// <param name="variable">变量实体。</param>
    /// <param name="hostName">所属主机名（全局变量为 <see langword="null"/>）。</param>
    /// <returns>投影（<b>不含明文</b>）。</returns>
    private static EnvVariableDto ToDto(EnvVariable variable, string? hostName) =>
        new(
            variable.Name,
            variable.IsGlobal ? EnvVariableScope.Global : EnvVariableScope.Project,
            variable.IsGlobal ? null : hostName,
            variable.ComposeName,
            variable.IsSecret,
            variable.WebhookOverridable,
            variable.Description,
            variable.HasValue,
            ValueMask);

    /// <summary>
    /// 校验并规范化变量名。
    /// </summary>
    /// <param name="name">原始变量名。</param>
    /// <returns>规范化后的变量名。</returns>
    /// <exception cref="BusinessException">变量名非法（<c>1909</c>）。</exception>
    private static string RequireName(string? name)
    {
        if (!EnvVariableName.TryParse(name, out var parsed))
        {
            throw new BusinessException(
                ErrorCodes.InvalidVariableName,
                $"变量名「{name}」非法——只能由字母、数字与下划线组成（且不得以数字开头），不得超过 {EnvVariableName.MaxLength} 个字符");
        }

        return parsed.Value;
    }

    /// <summary>
    /// 校验并规范化 Compose 项目名。
    /// </summary>
    /// <param name="composeName">原始项目名。</param>
    /// <returns>规范化后的项目名。</returns>
    /// <exception cref="BusinessException">项目名为空（<c>1000</c>）。</exception>
    private static string RequireComposeName(string? composeName)
    {
        if (String.IsNullOrWhiteSpace(composeName))
        {
            throw BusinessException.Validation("Compose 项目名不能为空");
        }

        return composeName.Trim();
    }
}

/// <summary>
/// 解析出来的变量表。
/// </summary>
/// <param name="Values">变量名 → 值。</param>
/// <param name="AppliedOverrides">本次生效的覆盖项（变量名）。</param>
/// <param name="IgnoredOverrides">本次被忽略的覆盖项（变量名）。</param>
/// <remarks>
/// <b>只回名字，不回值</b>：响应会进反代的 access log 与前端的界面，
/// 而值是密钥或配置。
/// </remarks>
public sealed record ResolvedEnvVariables(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> AppliedOverrides,
    IReadOnlyList<string> IgnoredOverrides);
