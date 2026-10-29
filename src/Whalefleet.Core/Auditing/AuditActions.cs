namespace Whalefleet.Core.Auditing;

/// <summary>
/// 审计动作标识与目标类型的<b>字符串常量表</b>。
/// </summary>
/// <remarks>
/// <para><b>为什么是字符串常量而不是枚举</b>：审计记录是<b>历史事实</b>。
/// 枚举一旦重构（改名、拆分、删项），旧数据就再也解释不出来了——
/// 而审计的全部价值恰恰在「半年后还能读懂当时发生了什么」。
/// 字符串落库后，代码怎么改都不影响已写入的事实。</para>
/// <para><b>代价与对冲</b>：失去编译期的穷举检查，因此界面上用
/// <c>Record&lt;string, string&gt;</c> 做「动作 → i18n key」映射，
/// <b>并且一定带回退</b>（未登记的动作原样显示）——新增动作而忘了加翻译时，
/// 用户看到的是一串英文标识，而不是空白。</para>
/// <para><b>命名口径</b>：<c>域.对象.动作</c>，全小写、点分隔。域与权限目录的前缀保持一致
/// （<c>container.*</c> / <c>role.*</c>），便于按域筛选。</para>
/// </remarks>
public static class AuditActions
{
    // ==================== 认证（category = Authentication） ====================

    /// <summary>登录（成功 / 凭据错误 / 限流三态，由 outcome 区分）。</summary>
    public const string Login = "auth.login";

    /// <summary>登出（撤销当前刷新令牌）。</summary>
    public const string Logout = "auth.logout";

    /// <summary>刷新令牌被重放（旧令牌再次使用 → 该用户全部会话被撤销）。</summary>
    public const string RefreshTokenReplayed = "auth.refresh.replayed";

    // ==================== 容器 ====================

    /// <summary>容器详情（<b>敏感读</b>：响应里含 env，而 env 常含密码与 token）。</summary>
    public const string ContainerInspect = "container.inspect";

    /// <summary>下载容器日志（<b>敏感读</b>：日志可能含内部地址与凭据）。</summary>
    public const string ContainerLogsDownload = "container.logs.download";

    /// <summary>交互式终端会话（<b>敏感读</b>：会话内可直接改动容器）。</summary>
    public const string ContainerExec = "container.exec";

    /// <summary>创建容器。</summary>
    public const string ContainerCreate = "container.create";

    /// <summary>启动容器。</summary>
    public const string ContainerStart = "container.start";

    /// <summary>停止容器。</summary>
    public const string ContainerStop = "container.stop";

    /// <summary>重启容器。</summary>
    public const string ContainerRestart = "container.restart";

    /// <summary>终止容器（发信号）。</summary>
    public const string ContainerKill = "container.kill";

    /// <summary>暂停容器。</summary>
    public const string ContainerPause = "container.pause";

    /// <summary>恢复容器。</summary>
    public const string ContainerResume = "container.resume";

    /// <summary>删除容器。</summary>
    public const string ContainerRemove = "container.remove";

    /// <summary>重建容器（编辑并重建 / 追加挂载）。</summary>
    public const string ContainerRecreate = "container.recreate";

    /// <summary>重命名容器。</summary>
    public const string ContainerRename = "container.rename";

    /// <summary>热更新容器（重启策略 / 资源限制）。</summary>
    public const string ContainerUpdate = "container.update";

    /// <summary>复制容器。</summary>
    public const string ContainerDuplicate = "container.duplicate";

    // ==================== 镜像 ====================

    /// <summary>拉取镜像。</summary>
    public const string ImagePull = "image.pull";

    /// <summary>构建镜像。</summary>
    public const string ImageBuild = "image.build";

    /// <summary>推送镜像。</summary>
    public const string ImagePush = "image.push";

    /// <summary>给镜像打标签。</summary>
    public const string ImageTag = "image.tag";

    /// <summary>删除镜像。</summary>
    public const string ImageRemove = "image.remove";

    /// <summary>清理镜像。</summary>
    public const string ImagePrune = "image.prune";

    // ==================== 数据卷 / 网络 ====================

    /// <summary>创建数据卷。</summary>
    public const string VolumeCreate = "volume.create";

    /// <summary>删除数据卷。</summary>
    public const string VolumeRemove = "volume.remove";

    /// <summary>清理数据卷。</summary>
    public const string VolumePrune = "volume.prune";

    /// <summary>创建网络。</summary>
    public const string NetworkCreate = "network.create";

    /// <summary>删除网络。</summary>
    public const string NetworkRemove = "network.remove";

    /// <summary>容器接入网络。</summary>
    public const string NetworkConnect = "network.connect";

    /// <summary>容器断开网络。</summary>
    public const string NetworkDisconnect = "network.disconnect";

    // ==================== 主机 ====================

    /// <summary>新增主机。</summary>
    public const string HostCreate = "host.create";

    /// <summary>修改主机。</summary>
    public const string HostUpdate = "host.update";

    /// <summary>删除主机。</summary>
    public const string HostRemove = "host.remove";

    // ==================== 用户与角色 ====================

    /// <summary>创建用户。</summary>
    public const string UserCreate = "user.create";

    /// <summary>删除用户。</summary>
    public const string UserRemove = "user.remove";

    /// <summary>修改密码。</summary>
    public const string UserUpdatePassword = "user.update-password";

    /// <summary>创建角色。</summary>
    public const string RoleCreate = "role.create";

    /// <summary>修改角色。</summary>
    public const string RoleUpdate = "role.update";

    /// <summary>删除角色。</summary>
    public const string RoleRemove = "role.remove";

    /// <summary>覆盖角色的权限集。</summary>
    public const string RoleSetPermissions = "role.set-permissions";

    /// <summary>授予角色（带作用域）。</summary>
    public const string RoleAssign = "role.assign";

    /// <summary>撤销角色分配。</summary>
    public const string RoleRevoke = "role.revoke";

    // ==================== 审计自身 ====================

    /// <summary>导出审计日志（<b>敏感读</b>：导出本身会把整份记录带走，必须留痕）。</summary>
    public const string AuditExport = "audit.export";

    /// <summary>服务端错误兜底（中间件记录，无业务动作可归）。</summary>
    public const string HttpError = "http.error";
}

/// <summary>
/// 审计目标的类型标识（<c>target_type</c> 列）。
/// </summary>
/// <remarks>
/// 与 <see cref="AuditActions"/> 同理用字符串：目标类型会随新资源产生，
/// 而历史记录里的类型名必须永远可读。
/// </remarks>
public static class AuditTargetTypes
{
    /// <summary>容器。</summary>
    public const string Container = "container";

    /// <summary>镜像。</summary>
    public const string Image = "image";

    /// <summary>数据卷。</summary>
    public const string Volume = "volume";

    /// <summary>网络。</summary>
    public const string Network = "network";

    /// <summary>主机。</summary>
    public const string Host = "host";

    /// <summary>用户。</summary>
    public const string User = "user";

    /// <summary>角色。</summary>
    public const string Role = "role";

    /// <summary>角色分配（主体是「用户 × 主机」这一对关系）。</summary>
    public const string RoleAssignment = "role-assignment";

    /// <summary>会话 / 令牌。</summary>
    public const string Session = "session";

    /// <summary>审计日志本身。</summary>
    public const string Audit = "audit";
}
