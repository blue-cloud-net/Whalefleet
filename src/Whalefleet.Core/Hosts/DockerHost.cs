namespace Whalefleet.Core.Hosts;

/// <summary>
/// 已注册的 Docker 主机。
/// </summary>
/// <remarks>
/// <para><b>充血职责</b>：名称格式、连接方式取值、以及「连接方式 ↔ 端点形态」的粗粒度一致性
/// 都由实体自身保证。</para>
/// <para><b>不做权威端点校验</b>：端点的规范形式与合法性由 <c>DockerEndpointNormalizer</c> 判定
/// （在 Infrastructure），本实体只拒绝明显矛盾的一对（如 <c>Tcp</c> + <c>/var/run/docker.sock</c>），
/// 避免同一套规则在两处漂移。</para>
/// <para><b>连接缓存</b>：客户端的缓存键是「规范化连接串」，<b>不是</b> <see cref="Id"/> 或
/// <see cref="Name"/>。因此两台主机名指向同一守护进程时会共享同一个客户端；
/// 改动端点或凭据后必须调用 <c>IDockerClientFactory.Invalidate</c>（0.0.8 由应用服务负责）。</para>
/// <para><b>名称不可改</b>：<see cref="Name"/> 是业务侧的主机标识（URL 与 <c>X-Host-Id</c> 都用它），
/// 因此<b>不</b>提供改名方法——改名会让已分享的深链与书签失效。需要换名时请新建主机。</para>
/// <para><b>删除语义</b>：软删除（继承 <see cref="FullAuditedEntity{TId, TUserKey}"/>），
/// 保留历史审计；名称唯一索引带 <c>WHERE is_deleted = 0</c>，删除后名称可再次使用。</para>
/// <para><b>持久化</b>：提供非公开无参构造供 Dapper 物化，详见
/// <c>docs/architecture.md</c> 的「持久化实体物化约定（Dapper）」。</para>
/// </remarks>
public sealed class DockerHost : FullAuditedEntity<Guid, Guid>
{
    /// <summary>
    /// 主机显示名最大长度。
    /// </summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// 显示名（去首尾空白后保存，唯一性<b>不区分大小写</b>，且<b>不可改</b>）。
    /// </summary>
    public string Name { get; private set; } = default!;

    /// <summary>
    /// 连接方式。
    /// </summary>
    public DockerHostType Type { get; private set; }

    /// <summary>
    /// 连接端点。
    /// </summary>
    public HostEndpoint Endpoint { get; private set; } = default!;

    /// <summary>
    /// 认证方式（<b>持久化投影</b>）。
    /// </summary>
    /// <remarks>
    /// <para>供 Dapper 物化 <c>auth_kind</c> 列使用，业务代码请改用 <see cref="Credentials"/>。</para>
    /// <para>之所以不能只留一个 <see cref="HostCredentials"/> 属性：Dapper 的物化模型是
    /// 「一列 → 一成员」，无法把两列拼成一个值对象。</para>
    /// </remarks>
    internal DockerHostAuthKind AuthKind { get; private set; }

    /// <summary>
    /// 认证载荷 JSON（<b>持久化投影</b>）。
    /// </summary>
    /// <remarks>
    /// 供 Dapper 物化 <c>auth_config_json</c> 列使用，业务代码请改用 <see cref="Credentials"/>。
    /// 内容属敏感信息，<b>禁止</b>写入日志。
    /// </remarks>
    internal string? AuthConfigJson { get; private set; }

    /// <summary>
    /// 认证凭据（由两个持久化投影组合而成）。
    /// </summary>
    public HostCredentials Credentials => HostCredentials.FromJson(this.AuthKind, this.AuthConfigJson);

    /// <summary>
    /// 所属分组；<see langword="null"/> 表示未分组。
    /// </summary>
    /// <remarks>
    /// 分组是**展示性**概念（总览页按组聚合），不参与任何授权判定——
    /// 「谁能操作哪台主机」由 0.1.0 落地的可见性表与 0.12.0 的容器级权限表回答。见 <see cref="HostGroup"/>。
    /// </remarks>
    public Guid? GroupId { get; private set; }

    /// <summary>
    /// 是否启用。停用后不再接受指向该主机的资源请求（由应用服务判定）。
    /// </summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// 供 Dapper 物化使用的非公开无参构造。业务代码请改用 <see cref="Register"/>。
    /// </summary>
    private DockerHost()
    {
    }

    /// <summary>
    /// 注册一台新的 Docker 主机（默认启用）。
    /// </summary>
    /// <param name="id">主机主键。</param>
    /// <param name="name">显示名；首尾空白会被去除，长度 1-64，不得含控制字符。</param>
    /// <param name="type">连接方式。</param>
    /// <param name="endpoint">连接端点，须与 <paramref name="type"/> 的形态一致。</param>
    /// <param name="credentials">认证凭据；为 <see langword="null"/> 时视为无认证。</param>
    /// <returns>新建的主机实体。</returns>
    /// <exception cref="ArgumentException">名称非法、连接方式未知，或端点与连接方式矛盾。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> 为 <see langword="null"/>。</exception>
    public static DockerHost Register(
        Guid id,
        string name,
        DockerHostType type,
        HostEndpoint endpoint,
        HostCredentials? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EnsureKnownType(type);
        EnsureEndpointMatchesType(type, endpoint);

        var host = new DockerHost
        {
            Id = id,
            Name = NormalizeName(name),
            Type = type,
            Endpoint = endpoint,
            Enabled = true,
        };

        host.SetCredentials(credentials ?? HostCredentials.None);

        return host;
    }

    /// <summary>
    /// 更新连接方式、端点与凭据（三者是一组，必须一起改）。
    /// </summary>
    /// <param name="type">新的连接方式。</param>
    /// <param name="endpoint">新的端点。</param>
    /// <param name="credentials">新的凭据；为 <see langword="null"/> 时视为无认证。</param>
    /// <exception cref="ArgumentException">连接方式未知，或端点与连接方式矛盾。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> 为 <see langword="null"/>。</exception>
    /// <remarks>调用方在保存后应让 <c>IDockerClientFactory</c> 中该主机的缓存客户端失效。</remarks>
    public void UpdateConnection(
        DockerHostType type,
        HostEndpoint endpoint,
        HostCredentials? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EnsureKnownType(type);
        EnsureEndpointMatchesType(type, endpoint);

        this.Type = type;
        this.Endpoint = endpoint;
        this.SetCredentials(credentials ?? HostCredentials.None);
    }

    /// <summary>
    /// 启用主机（幂等）。
    /// </summary>
    public void Enable() => this.Enabled = true;

    /// <summary>
    /// 调整分组归属。
    /// </summary>
    /// <param name="groupId">目标分组主键；传 <see langword="null"/> 表示移出分组。</param>
    /// <remarks>
    /// 只做「把归属写进实体」这一件事：**目标分组是否存在由应用服务校验**。
    /// 实体不注入仓储（那会把持久化依赖带进 Core），而外键约束只能拦住「指向不存在的行」，
    /// 拦不住「指向一个已软删除的分组」——后者会表现为「主机莫名出现在未分组」。
    /// </remarks>
    public void AssignGroup(Guid? groupId) => this.GroupId = groupId;

    /// <summary>
    /// 停用主机（幂等）。
    /// </summary>
    public void Disable() => this.Enabled = false;

    private void SetCredentials(HostCredentials credentials)
    {
        this.AuthKind = credentials.Kind;
        this.AuthConfigJson = credentials.ConfigJson;
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"主机名长度不得超过 {MaxNameLength} 个字符", nameof(name));
        }

        if (trimmed.Any(Char.IsControl))
        {
            throw new ArgumentException("主机名不得包含控制字符", nameof(name));
        }

        return trimmed;
    }

    private static void EnsureKnownType(DockerHostType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentException($"未知的连接方式：{(int)type}", nameof(type));
        }
    }

    private static void EnsureEndpointMatchesType(DockerHostType type, HostEndpoint endpoint)
    {
        var value = endpoint.Value;

        var hasTcpScheme = value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var hasSocketScheme = value.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase);

        var looksLikeUnixPath = value.StartsWith('/');

        switch (type)
        {
            case DockerHostType.Socket when hasTcpScheme:
                throw new ArgumentException(
                    $"连接方式为 socket，但端点看起来是 TCP 地址：{value}",
                    nameof(endpoint));
            case DockerHostType.Tcp when hasSocketScheme || looksLikeUnixPath:
                throw new ArgumentException(
                    $"连接方式为 TCP，但端点看起来是本地套接字：{value}",
                    nameof(endpoint));
            default:
                break;
        }
    }
}
