# 架构设计

## 系统分层概览

```
┌─────────────────────────────────────────────────────────┐
│                  浏览器 / 客户端                          │
│       Vue 3 + TypeScript + UnoCSS + Element Plus        │
│   Pinia · vue-i18n · @microsoft/signalr · xterm.js      │
└──────────────┬──────────────────────┬───────────────────┘
               │ HTTP (RPC API)       │ WebSocket (SignalR / 独立)
               ▼                      ▼
┌─────────────────────────────────────────────────────────┐
│              ASP.NET Core 10 宿主进程                    │
│                                                         │
│  ┌────────────────┐      ┌──────────────────────────┐  │
│  │  API 层        │      │  SignalR Hub 层           │  │
│  │  /api/v1/...   │      │  ContainerLogHub         │  │
│  │  Controllers   │      │  ContainerStatsHub       │  │
│  └───────┬────────┘      │  NotificationHub         │  │
│          │               └─────────────┬─────────────┘  │
│  ┌───────▼─────────────────────────────▼──────────────┐ │
│  │                 Service 层                          │ │
│  │  ContainerService  ImageService  VolumeService    │ │
│  │  NetworkService    HostService   UserService      │ │
│  │  AuthService       PermissionService              │ │
│  └───────┬─────────────────────────────┬──────────────┘ │
│          │                             │                │
│  ┌───────▼────────┐      ┌─────────────▼────────────┐ │
│  │  Docker 接入层  │      │  数据访问层                │ │
│  │  Core 抽象     │      │  Dapper + SQLite           │ │
│  │  Infra 实现    │      │  + WAL 模式                │ │
│  │  DockerClient  │      │  + Argon2id                │ │
│  │  Builder 风格  │      └────────────────────────────┘ │
│  └───────┬────────┘                                     │
└──────────┼──────────────────────────────────────────────┘
           │ unix:///var/run/docker.sock 或 tcp://host:port
           ▼
┌─────────────────────────┐
│   Docker 守护进程        │
│   多主机（0.0.8 起）    │
└─────────────────────────┘
```

---

## 各层职责

> 0.0.1 起采用 **分层架构 + 充血模型（4 个后端项目）**。本节后续描述保持与原文档兼容，仅关键术语对齐：
> - "API 层" → **接口层（Api / Presentation）**
> - "Service 层" → **应用层（Application）**
> - "Docker 接入层 Core 抽象" → **应用层 Abstractions**
> - "数据访问层" → 持久化接口在 **Core**，实现在 **Infrastructure/Persistence**
>
> **为什么是充血模型而不是 DDD 战术模式**：本项目管理的容器 / 镜像 / Volume / Network 的真相在
> Docker 守护进程，把它们建成带聚合根与领域事件的领域模型只会得到"立刻过期的数据袋"；
> 真正有业务不变量的是 **User / Role / ResourceGrant** 这类自有数据。因此这里采用
> 「实体承载行为与不变量 + 分层隔离依赖」的充血模型，不引入聚合根、领域事件与仓储派发机制。

### 项目依赖方向

项目级依赖图（箭头方向 = 引用方向；箭头尾部依赖箭头头部）：

```
                              ┌──────────────────────────────┐
                              │  Docker.Manager.Core         │
                              │  ──────────────────────────  │
                              │  Entity / ValueObject        │
                              │  枚举与业务常量               │
                              │  仅依赖 BCL，无 NuGet         │
                              └──────────────┬───────────────┘
                                             │ ▲
                  ┌──────────────────────────┘ │ ┌──────────────────────────┐
                  │                            │ │                          │
                  ▼                            │ ▼                          │
       ┌─────────────────────────┐             │       ┌─────────────────────────┐
       │  Application            │             │       │  Infrastructure          │
       │  ─────────────────────  │             │       │  ─────────────────────  │
       │  应用服务（用例编排）    │             │       │  Docker.DotNet 适配      │
       │  外部资源接口抽象       │             │       │  仓储实现                │
       │  (IDockerClientFactory) │             │       │  第三方 SDK 集成         │
       │  DTO 映射              │             │       │                          │
       └──────────┬──────────────┘             │       └──────────┬──────────────┘
                  │                            │                  │
                  │     ┌──────────────────────┘                  │
                  │     │                                          │
                  ▼     ▼                                          ▼
       ┌──────────────────────────────────────────────────────────────────┐
       │                       Docker.Manager.Api                          │
       │  ─────────────────────────────────────────────────────────────  │
       │  ASP.NET Core 10 宿主进程                                          │
       │  Controllers / SignalR Hubs / 静态前端托管                        │
       │  Serilog / Kestrel / 配置 / DI 编排                                │
       └──────────────────────────────────────────────────────────────────┘
```

**依赖方向规则**：

| 项目 | 可依赖 | 不可依赖 |
|------|--------|---------|
| `Core` | BCL | Application、Infrastructure、Api、任何 NuGet |
| `Application` | Core | Infrastructure、Api |
| `Infrastructure` | Core、Application | Api |
| `Api` | Application、Infrastructure | （已是最外层） |
| `UnitTests` | Core、Application | Infrastructure、Api |

**违反依赖方向的检测手段**：

```bash
# 1) Core 项目应无任何 NuGet 依赖
dotnet list src/Docker.Manager.Core/Docker.Manager.Core.csproj package --include-transitive

# 2) Application 应仅依赖 Core（一个 ProjectReference）
dotnet list src/Docker.Manager.Application/Docker.Manager.Application.csproj package
```

### 分层与充血模型约定

> 本节合并自原 `docs/ddd-guide.md`（已于 0.0.2 后废弃）。DDD 战术模式整体移除，保留分层依赖方向与充血模型。

**命名约定**

| 类别 | 约定 | 示例 |
|------|------|------|
| 实体 | 名词单数，继承 `Entity<TId>` | `DockerHost`、`User` |
| 值对象 | 名词，继承 `ValueObject`，**必须不可变** | `HostEndpoint`、`PortMapping`、`ContainerStatus` |
| 枚举 | 名词，位于对应实体的命名空间下 | `DockerHostType`、`UserRole` |
| 外部资源接口 | `IXxxFactory` / `IXxxAccessor`，定义在 `Application/Abstractions/` | `IDockerClientFactory`、`IDockerHostAccessor` |
| 持久化接口 | `IXxxRepository`，定义在 `Core`，实现在 `Infrastructure/Persistence/`。CRUD 共同部分继承 `Common.IRepository<TEntity, TId>` | `IUserRepository`、`IHostRepository` |
| 应用服务 | `XxxService`，位于 `Application/Services/` | `AuthService`、`ContainerService` |
| DTO | `XxxDto` / `XxxRequest` / `XxxResponse` | `LoginRequest`、`ContainerDto` |
| Controller | `XxxController`，继承 `ControllerBase` | `AuthController`、`ContainerController` |

**接口放置原则**

拿到一个接口 `IFoo` 时，按下表判断归属：

| 类型 | 归属 | 判断标准 | 示例 |
|------|------|---------|------|
| **业务概念**（"管理 X"） | `Core` | 去掉整个外部世界后，概念仍成立 | `IUserRepository`、`User`、`DockerHost` |
| **外部资源抽象**（"操作外部系统"） | `Application/Abstractions` | 抽象的是某种外部系统（Docker、邮件、缓存、消息队列），而非业务概念本身 | `IDockerClientFactory`、`IEmailSender` |
| **第三方 SDK 适配** | `Infrastructure` | 包装 / 适配某个具体 SDK，可直接替换 | `DockerClientFactory`、`SqliteUserRepository` |

**推论——接口的「入参 / 返回类型」同样受约束**：外部资源接口定义在 `Application/Abstractions`，它引用的一切类型也必须能在应用层落脚。因此 `DockerHostType`、`DockerHostAuth`、`DockerHostConnection` 都放在 `Application/Abstractions`，而不是 `Infrastructure`——否则 `Application` 就必须反向引用 `Infrastructure`，依赖方向当场破裂。反之，`DockerEndpointNormalizer`（如何把各种写法拼成规范连接串）是纯技术细节，落在 `Infrastructure/Docker`。

**充血模型要求**

- 实体的**行为与不变量写在实体自身的方法内**（充血），而不是只当数据袋、把所有判断推到应用服务
- 推荐形态：**私有构造 + 静态工厂**，工厂方法在创建时校验不变量
- 不变量违反抛 **BCL 的 `ArgumentException`**（含 `ArgumentNullException`），中间件统一映射为 `1000` / HTTP 400
- 用例执行失败（外部资源不可用、规则不允许）抛 `BusinessException`（`Application/Common/`），自带错误码
- Entity **不直接作为 HTTP 响应返回**：在应用服务里做 Entity → DTO 映射

**核心基类**

| 类型 | 位置 | 职责 |
|------|------|------|
| `Entity<TId>` | `Core/Common/Entity.cs` | 按 `Id` 判定相等性；提供 `Id` 与运算符重载 |
| `ValueObject` | `Core/Common/ValueObject.cs` | 按 `GetEqualityComponents()` 逐项判定相等性；应不可变 |
| `CreationAuditedEntity<TId, TUserKey>` | `Core/Common/Auditing/` | 追加创建审计（`CreationTime` / `CreatorId`） |
| `AuditedEntity<TId, TUserKey>` | `Core/Common/Auditing/` | 追加最后修改审计（`LastModificationTime` / `LastModifierId`） |
| `FullAuditedEntity<TId, TUserKey>` | `Core/Common/Auditing/` | 追加软删除（`IsDeleted` / `DeletionTime` / `DeleterId`）+ `Delete()` / `Restore()` |

详见 [实体审计与软删除](#实体审计与软删除) 与 [持久化实体物化约定（Dapper）](#持久化实体物化约定dapper)。

**反模式清单**

| 反模式 | 表现 | 纠正 |
|--------|------|------|
| **贫血模型** | 实体只有属性、没有行为；所有逻辑写在 Service 里 | 把行为与不变量移到实体方法 |
| **Entity 直接返回 DTO** | Controller 直接拿 Entity 序列化返回 | 在应用服务里做 Entity → DTO 映射 |
| **实体跨边界互相持有引用** | `Container` 持有 `User` 对象；`User` 持有 `Host` 对象 | 只保留 ID 引用（`UserId`、`HostId`），按 ID 查询 |
| **`Core` 引用 NuGet 包** | `Core.csproj` 含 `PackageReference` | 移除；确需 BCL 之外的能力就移到 `Application` / `Infrastructure` |
| **`Application` 引用 `Infrastructure`** | 应用服务直接 `new SqliteConnection()` | 在 `Application/Abstractions` 定义接口，`Infrastructure` 实现并注入 |
| **持久化接口返回活连接** | `IHostRepository.QueryAllListAsync()` 返回的对象里带 `DockerClient` | 仓储只负责持久化；客户端获取走 `IDockerClientFactory` |

### 实体清单（0.0.3 / 0.0.8 已落地）

| 实体 | 位置 | 基类 | 业务键与唯一性 | 关键不变量 |
|------|------|------|----------------|-----------|
| `Role` | `Core/Users/Role.cs` | `FullAuditedEntity<Guid, Guid>` | `name`（小写文本，`COLLATE NOCASE` 部分唯一索引） | 标识自动转小写、2–32 字符、仅 `[A-Za-z0-9._-]`；**标识不可改**；内置角色不可删（`Retire` + 仓储 `WHERE is_built_in = 0` 双重保证） |
| `User` | `Core/Users/User.cs` | `FullAuditedEntity<Guid, Guid>` | `username`（`COLLATE NOCASE` 部分唯一索引） | 去首尾空白后 3–32 字符，仅 `[A-Za-z0-9._-]`；密码哈希非空；`RoleId` 非空；**用户名不可改** |
| `DockerHost` | `Core/Hosts/DockerHost.cs` | `FullAuditedEntity<Guid, Guid>` | `name`（同上） | 名称 1–64 且无控制字符、**不可改**（它是对外主机标识）；连接方式与端点形态不得矛盾；连接方式 / 端点 / 凭据必须成组更新 |
| `RefreshToken` | `Core/Auth/RefreshToken.cs` | `CreationAuditedEntity<Guid, Guid>` | `token_hash` 唯一 | 只存哈希（非原文）；归属用户非空；`IsActiveAt` = 未撤销且未过期 |
| `UserHostPermission` | `Core/Permissions/` | `FullAuditedEntity<Guid, Guid>` | `(user_id, host_id)` 部分唯一索引 | 权限位不得含未定义位；`Has` 对组合位要求**全部**具备 |
| `UserContainerPermission` | `Core/Permissions/` | `FullAuditedEntity<Guid, Guid>` | `(user_id, host_id, container_id)` | 容器标识必须与主机成对出现（容器 ID 只在单台守护进程内唯一） |

> 权限实体的**表与仓储实现排到 0.3.0**：本迭代只落地实体与仓储接口，不建表、不注册 DI。

**值对象**：`HostEndpoint`（端点原文，不可变、按值相等）、`HostCredentials`（认证方式 + 不透明 JSON 载荷，
`ToString()` 只输出认证方式、**不泄漏载荷**）。

**枚举归属**：`DockerHostType`、`DockerHostAuthKind` 定义在 `Core/Hosts`——它们同时是 `Core` 的实体与
`Application` 的连接描述（`DockerHostConnection` / `IDockerClientFactory`）的取值域；放最内层可避免
两个同名枚举并互做映射。`HostPermissions` / `ContainerPermissions` 在 `Core/Permissions`
（标志枚举用复数命名，与 `[Flags]` 惯例一致）。

**角色是数据，不是枚举**（0.0.3 修订）：原 `UserRole` 枚举**已删除**，改为 `roles` 表 +
`Core/Users/Role` 实体。理由：枚举把「系统有哪些角色」焊死在编译期，新增角色要改代码 + 发版。
现在管理员可在运行时增删改角色，新增角色无需发版。
枚举只保留给**封闭集合**用（如 `DockerHostType` 这类由外部协议决定、不随部署变化的值）。

**角色的三个正交字段**：

| 字段 | 作用 | 约束 |
|------|------|------|
| `name` + `display_name` + `description` | 面向用户展示（“运维：可操作已授权主机上的容器…”） | `name` 进 JWT claim 与 `[Authorize(Roles = …)]`，字符集受限且**不可改** |
| `is_built_in` | 内置角色随系统发布 | **不可删除**：`Role.Retire()` 抛 `InvalidOperationException`，仓储再以 `WHERE is_built_in = 0` 兜底（绕过实体也删不掉） |
| `is_default` | 创建用户未指定角色时采用 | 全库**至多一个**，由 `IRoleRepository.SetDefaultAsync` 在事务内保证 |

**内置角色**（迁移种入，主键固定，便于运维脚本引用）：

| `name` | 可读名称 | 职责 | 默认 |
|--------|---------|------|:---:|
| `admin` | 管理员 | 可管理用户、主机与全部资源；`[Authorize(Roles = "admin")]` 依赖此角色 | |
| `operator` | 运维 | 对已授权主机上的容器、镜像、数据卷与网络有操作权限 | |
| `viewer` | 只读 | 对已授权资源仅可查看，不可修改 | |
| `auditor` | 审计 | 只读，并可查看操作审计（审计能力于 0.3.0 落地） | ✅ |

**角色不编码资源范围**：角色只是身份标签，“某用户能碰哪些主机 / 容器”由 0.3.0 的资源权限表承担。
因此不会出现“新建一个角色就等于新增一套权限”这种隐性授权。
若日后需要“按角色批量授权”，应在 `user_host_permissions` 上增加角色维度，而非往角色上挂权限。

**为什么这样继承**

- `User` / `DockerHost` 需要**软删除**：用户名与主机名是业务键，删除后必须可复用
  （靠部分唯一索引 `WHERE is_deleted = 0`），因此用 `FullAuditedEntity`。
- `RefreshToken` 是**一次性凭据**：撤销由 `revoked_at` 表达、过期后由维护任务物理删除，
  既不需要修改审计也不需要软删除，因此只用 `CreationAuditedEntity`。
- 未引入 `SoftDeleteEntity`：只需"标记删除"的实体自行实现 `ISoftDelete` 即可。

**审计由谁填**：`Infrastructure/Persistence/RepositoryBase` 持有 `ICurrentUser` 与 `TimeProvider`，
在插入 / 更新 / 软删除前调用 `MarkCreated` / `MarkModified` / `Delete`。
⚠️ 这些 helper 的泛型约束必须是**审计基类**（`CreationAuditedEntity` 一族）——审计*接口*只声明只读契约，
约束到接口就调不到 `Mark*`。

**`hosts` 的认证信息为什么是两列**：`auth_kind`（需要能独立读出与索引）+ `auth_config_json`
（结构尚未定型的载荷）。实体对外只有一个 `Credentials` 值对象，这两列是它的**持久化投影**
（`internal` 属性），因为 Dapper 的物化模型是「一列 → 一成员」，无法把两列拼成一个值对象。

**完整实现**见 `src/Docker.Manager.Core/{Users,Hosts,Auth,Permissions}/`；
仓储契约 `IUserRepository` / `IHostRepository` / `IRoleRepository` / `IRefreshTokenRepository` 定义在 `Core`，
实现在 `Infrastructure/Persistence/`。仓储的「未找到」以 `Task<bool>` 返回，
**不抛**业务异常——是否把 `false` 转成 `1201` / `1300` 由应用服务决定，仓储不承担错误码语义。

#### 仓储抽象：共同契约 + 模板基类

持久化分两层，避免每个仓储都重写一遍相同的控制流：

| 层 | 位置 | 职责 |
|----|------|------|
| `IRepository<TEntity, TId>` | `Core/Common/` | **契约**：声明四个 CRUD（`QueryByIdAsync` / `AddAsync` / `UpdateAsync` / `RemoveByIdAsync`）+ 一个全量列表（`QueryAllListAsync`）。只放真正同构的成员 |
| `RepositoryBase<TEntity, TId>` | `Infrastructure/Persistence/` | **实现**：连接管理、审计填充、`affected == 1` → `bool` 的翻译、软删除默认模板 |

具体仓储接口（`IUserRepository` 等）继承 `IRepository<…>` 后**只保留自己特有的查询**——
按用户名 / 按令牌哈希 / 分页 / 计数 / 改默认角色。把这些硬塞进基接口只会得到一堆
「本仓储不支持」的空实现。

**派生仓储只需提供四个钩子**：

```csharp
protected abstract Task<TEntity?> QueryByIdAsync(DbConnection connection, TId id, CancellationToken ct);
protected abstract string QueryAllSql { get; }
protected abstract Task<int>  ExecuteInsertAsync(DbConnection connection, TEntity entity, CancellationToken ct);
protected abstract Task<bool> ExecuteUpdateAsync(DbConnection connection, TEntity entity, CancellationToken ct);
```

> ⚠️ `QueryAllSql` 交出去的是**整段 SQL**（投影列 + 软删除过滤 + `ORDER BY`），不是由基类拿表名拼装出来的。
> 原因有二：`refresh_tokens` 没有 `is_deleted` 列，基类无从判断该不该追加 `is_deleted = 0`；
> 且各表的合理顺序本就不同（`hosts` 按创建时间、`roles` 按内置优先）——**排序不进基类**。

「开连接 → 填审计 → 执行 → 翻译返回值」这套顺序现在只有一份，因此
**「忘了填审计」「忘了过滤软删除」「忘了判 affected == 1」都不再可能**。

**两处刻意保留的覆写**（默认模板不适用时）：

| 仓储 | 覆写 | 原因 |
|------|------|------|
| `SqliteRoleRepository` | `RemoveAsync` | 内置角色不可删（`WHERE is_built_in = 0`），且删除时必须清 `is_default`（否则默认角色被删后「创建用户取默认角色」悬空） |
| `SqliteRefreshTokenRepository` | `RemoveAsync` + 新增 `QueryByIdAsync` | 令牌**不参与软删除**，删除是物理 `DELETE`，基类的「软删除模板」在这里是错的 |

> ⚠️ 审计填充方法里用的是**运行时类型判断**（`is AuditedEntity<…>`）而非泛型约束：
> `<TEntity>` 只能约束到 `Entity<TId>`（覆盖全部实体），而 `MarkCreated` / `MarkModified` / `Delete`
> 分别定义在不同审计基类上。约束到具体基类会让基类无法服务「只带部分审计」的实体。

### 表结构（0.0.3 已落地）

三张表的权威定义是内嵌迁移脚本
`src/Docker.Manager.Infrastructure/Persistence/Schema/Migrations/000N_*.sql`；
本节只记录**设计要点**，不复制 DDL（避免两处漂移）。

| 表 | 迁移脚本 | 要点 |
|----|----------|------|
| `users` | `0001_users.sql`（`0004` 重建） | 主键与审计人一律 TEXT（规范小写 Guid）；`role_id` 为指向 `roles(id)` 的**外键**（0.0.3 原为 `role` 枚举名文本）；软删除列 `is_deleted`；`ux_users_username` = `UNIQUE (username COLLATE NOCASE) WHERE is_deleted = 0`；`ix_users_role_id` |
| `roles` | `0004_roles.sql` | `name` 唯一（部分索引 + `COLLATE NOCASE`）；`ux_roles_default` = `UNIQUE (is_default) WHERE is_default = 1 AND is_deleted = 0`，**在数据层保证默认角色至多一个**；`is_built_in` 内置标记 |
| `hosts` | `0002_hosts.sql` | `type` = `socket`\|`tcp`（小写文本）；`endpoint` 存**规范化前**原文；`auth_kind` = `none`\|`tls`\|`basic`；`auth_config_json` 在无认证时为 NULL；`ux_hosts_name` 同 users |
| `refresh_tokens` | `0003_refresh_tokens.sql` | 无软删除列；`ux_refresh_tokens_token_hash` 唯一；`ix_*_user_id` / `ix_*_expires_at` 支撑「登出所有设备」与过期清理；**不建** `user_id → users(id)` 外键（用户是软删除的，外键带不来额外保护却会让清理脚本更复杂） |

**迁移机制**：`PRAGMA user_version` 记录已应用到第几版，脚本文件名前缀即目标版本（必须从 1 起**连续**编号，
否则启动失败）；每个脚本与其版本号写入**同一事务**，因此中断只会留下"该脚本整体未应用"。

**改列用重建表**（`0004_roles.sql` 为例）：SQLite 的 `ALTER TABLE` 不能改列类型 / 约束，
因此 `users.role` → `role_id REFERENCES roles(id)` 走官方模式：
建 `users_new` → `INSERT ... SELECT` 搬数据（用 `LEFT JOIN` + `COALESCE` 把旧枚举名映射到角色主键，
无匹配时落到默认角色，**避免 `NOT NULL` 把整个迁移卡死**）→ `DROP TABLE users` → `RENAME` →
重建索引（`DROP TABLE` 会连带删掉旧索引，必须一并补回）。

**启动顺序**：`DatabaseInitializer` 在宿主**开始监听之前**执行「迁移 → 启用 WAL → 种入默认主机」。
初始化失败即启动失败，不会让第一个请求撞上 `no such table`。

**默认主机**（0.0.8）：`hosts` 表为空时种入
`id = 00000000-0000-0000-0000-000000000001`、`name = local`、`type = socket`、`endpoint = /var/run/docker.sock`，
审计人取默认值哨兵（启动引导无登录用户）。
判空依据是**物理行数**而非仓储的业务计数——默认主机被软删除时业务计数为 0，若据此再次种入会与残留行主键冲突。

**Dapper 的两个硬约束**（已踩坑，勿回退）：

1. 必须开启 `MatchNamesWithUnderscores`，否则 `creation_time` 静默喂不进 `CreationTime`；
2. **参数绑定路径会绕过 `ITypeHandler`**（值类型直接装箱）——库中形态有约定的值类型（Guid、枚举）
   必须在写入与查询**两侧**用 `StorageTextExtensions.ToStorageText()` 显式转文本，
   否则 Guid 按驱动默认写成**大写**（与文档 / 运维脚本里的小写字面量不一致，手写 SQL 查不到行）、
   枚举会落成 `0` / `1`。读取侧仍由类型处理器兜底（同时容忍文本与整数）。

---

### API 层（Controllers）

- 接收 HTTP 请求，解析入参，调用 Service 层
- 统一响应格式封装（`ApiResult<T>`）
- JWT 鉴权 + `[Authorize(Roles = "admin")]` 角色检查
- 不包含业务逻辑

### SignalR Hub 层（0.3.0 起）

| Hub | 职责 |
|-----|------|
| `ContainerLogHub` | 客户端订阅指定容器的实时日志流 |
| `ContainerStatsHub` | 客户端订阅指定容器的实时 CPU/内存/网络统计 |
| `NotificationHub` | 推送系统事件（容器异常退出等通知） |

Hub 鉴权：JWT 通过 `?access_token=...` 查询参数传递（WebSocket 不支持自定义 Header）。

### Service 层

- 封装业务逻辑与 Docker.DotNet 调用细节
- 隔离 Controller 与 Docker SDK 的直接耦合
- 封装 Dapper Repository 调用（用户、授权、主机相关数据）
- 通过 `IDockerHostAccessor` 解析当前请求的 Docker 主机客户端

### Docker 接入层

- **`Application/Abstractions` 层定义接口**：`IDockerClientFactory`、`IDockerHostAccessor`、`IContainerOperations`、`IImageOperations` 等
- **`Infrastructure/Docker` 层实现**：基于 [Docker.DotNet.Enhanced](https://github.com/testcontainers/Docker.DotNet)（fork），使用 `DockerClientBuilder` 风格
- **0.0.8 起支持多主机**：主机来源是 `hosts` 表（**不再是配置**），由 Scoped 的 `IDockerHostAccessor` 按**主机名**解析；客户端由 Singleton 的 `IDockerClientPool` 按**规范化连接串**池化（多主机名指向同一守护进程即共享连接），并提供 `Invalidate` 与空闲回收；连接描述 `DockerHostConnection` 定义在 `Application/Abstractions`
- **后续**：TCP + TLS / Basic 认证（`DockerHostAuth` 已带凭据指纹槽位）

### 数据访问层

- **持久化接口**（`IUserRepository`、`IHostRepository`、`IRefreshTokenRepository` 等）定义在 `Docker.Manager.Core/`
- **仓储实现**位于 `Docker.Manager.Infrastructure/Persistence/`：使用 `Dapper` 执行手写 SQL，
  共同继承 `RepositoryBase`（连接工厂 + 审计填充 + 软删除约定）
- 数据库：SQLite + `Microsoft.Data.Sqlite`；连接串、外键约束与忙等待由 `SqliteConnectionFactory` 统一施加
- 文件存储：配置项 `Persistence:DatabasePath`（生产 `/app/data/docker-manager.db`、开发 `data/docker-manager.db`）
- **WAL 模式**启用（`PRAGMA journal_mode=WAL`；文件级属性，设置一次持久生效）
- 应用启动时（开始监听**之前**）自动执行**版本化迁移**（`PRAGMA user_version` + 内嵌 `000N_*.sql`）
- 单次操作一个连接（用完还池），不提供跨仓储事务——0.0.3 的业务写入均为单语句
- 存储内容：用户信息、资源授权关系、Docker 主机配置、刷新令牌哈希、Registry 凭据（加密，0.2.0 起）

---

## 实体审计与软删除

> 位于 `src/Docker.Manager.Core/Common/Auditing/`。设计参照 ABP 的分层审计：把"创建 / 修改 / 删除"
> 拆成**可独立混入的细粒度接口**，再提供三级常用基类作为便利入口。

### 分层接口

| 接口 | 泛型 | 成员 | 语义 |
|------|------|------|------|
| `IHasCreationTime` | 否 | `CreationTime` | 只记创建时间 |
| `IHasModificationTime` | 否 | `LastModificationTime?` | 只记最后修改时间 |
| `ISoftDelete` | 否 | `IsDeleted` | 只记软删除标志 |
| `IHasDeletionTime` | 否 | `: ISoftDelete` + `DeletionTime?` | 软删除 + 删除时间 |
| `ICreationAuditedObject<TUserKey>` | 是 | `: IHasCreationTime` + `CreatorId` | 创建时间 + 创建人 |
| `IModificationAuditedObject<TUserKey>` | 是 | `: IHasModificationTime` + `LastModifierId` | 修改时间 + 修改人 |
| `IAuditedObject<TUserKey>` | 是 | 组合上述两者 | 创建 + 修改审计 |
| `IDeletionAuditedObject<TUserKey>` | 是 | `: IHasDeletionTime` + `DeleterId` | 删除时间 + 删除人 |
| `IFullAuditedObject<TUserKey>` | 是 | `IAuditedObject` + `IDeletionAuditedObject` | 全量审计 + 软删除 |

**所有接口成员都是只读（`get`）契约**，不提供 `set`——它们只声明"这个实体有这些审计信息"，
写入路径统一走基类方法。

### 三级基类

| 基类 | 继承 | 新增属性 | 新增方法 |
|------|------|----------|----------|
| `CreationAuditedEntity<TId, TUserKey>` | `Entity<TId>` | `CreationTime`、`CreatorId` | `MarkCreated(DateTime utcNow, TUserKey creatorId)` |
| `AuditedEntity<TId, TUserKey>` | `CreationAuditedEntity<…>` | `LastModificationTime`、`LastModifierId` | `MarkModified(DateTime utcNow, TUserKey modifierId)` |
| `FullAuditedEntity<TId, TUserKey>` | `AuditedEntity<…>` | `IsDeleted`、`DeletionTime`、`DeleterId` | `Delete(DateTime utcNow, TUserKey deleterId)`、`Restore()` |

未提供 `SoftDeleteEntity`：只需"标记删除"时，让实体自行实现 `ISoftDelete` 即可，不额外增加继承层级。

### 写入路径

审计属性一律 `internal set`，**外部（含应用层）只读**，唯一写入口是上述 `public` 方法：

```
应用服务 / 仓储基类
      │  调 entity.MarkCreated(now, userId)  ← public
      ▼
实体内部  ──►  internal setter 写 CreationTime / CreatorId
```

`Mark*` / `Delete` **显式接收时间与操作人**，而不是在实体内部取当前时间。原因有三：

1. 可测试——时间由调用方注入，单测无需真实时钟；
2. 可复原——同一套方法既用于"新建时写入"，也用于"从库中读出后按原值复原"（覆盖式赋值）；
3. 分层干净——"现在几点"和"当前是谁"属于应用/基础设施的上下文，实体无权获取。

**填充时机（0.0.3 落地）**：`RepositoryBase`（`Infrastructure/Persistence/`）持有
`ICurrentUser` 与 `TimeProvider`，在插入时调 `MarkCreated`、更新时调 `MarkModified`、
软删除时调 `Delete`，取值来自 `TimeProvider.GetUtcNow().UtcDateTime` 与当前用户 ID。

### 列名约定

**实体属性名与数据库列名同名，仅大小写风格不同**（属性 PascalCase，列 snake_case）：

| 属性 | 列 | 类型 |
|------|-----|------|
| `CreationTime` | `creation_time` | `DateTime`（UTC） |
| `CreatorId` | `creator_id` | 审计人主键 |
| `LastModificationTime` | `last_modification_time` | `DateTime?` |
| `LastModifierId` | `last_modifier_id` | 审计人主键 |
| `IsDeleted` | `is_deleted` | 布尔 |
| `DeletionTime` | `deletion_time` | `DateTime?` |
| `DeleterId` | `deleter_id` | 审计人主键 |

### 「无审计人」的表达：默认值哨兵

`TUserKey` **不加类型约束**（以支持 `string` 等引用类型主键），因此 C# 的 `TUserKey?` 语法
对值类型会退化为 `TUserKey` 本身、无法表达空值。本项目统一采用**默认值哨兵**：

| `TUserKey` | 哨兵值 | 含义 |
|------------|--------|------|
| `Guid` | `Guid.Empty` | 系统操作（无登录用户） |
| `long` / `int` | `0` | 系统操作 |
| `string` | `null` | 系统操作 |

消费方用 `EqualityComparer<TUserKey>.Default.Equals(x, default)` 判断。

> ⚠️ **代价**：哨兵值无法区分"未设置"与"系统操作"。若将来需要区分，改用包装结构体
> `AuditUser<TUserKey>`（属破坏性变更）。

### 软删除语义与查询过滤

| 规则 | 说明 |
|------|------|
| **不变量** | `DeletionTime == null` ⇔ `IsDeleted == false`，由 `Delete()` / `Restore()` 成对维护 |
| **幂等** | 重复 `Delete()` 刷新删除时间与删除者；对未删除实体 `Restore()` 无副作用 |
| **查询默认过滤** | 仓储生成的 `SELECT` **默认追加 `WHERE is_deleted = 0`**，软删除数据对业务查询不可见 |
| **逃生舱** | 仅在显式传入"包含已删除"开关（如 `includeDeleted: true`）时才放开，供回收站 / 数据修复类用例使用 |
| **唯一索引** | 软删除表的唯一约束需带 `WHERE is_deleted = 0`（SQLite 部分索引），避免删除后占用业务键 |

> 软删除查询过滤的**代码契约在 0.0.3 随 `RepositoryBase` 一并落地**；本节当前为约定。

---

## 持久化实体物化约定（Dapper）

> 0.0.3 落地。结论基于 Dapper 源码核实，**不需要**中间 Row DTO，**不需要** `InternalsVisibleTo`。

### Dapper 能物化非公开成员

| 环节 | Dapper 实现 | 结论 |
|------|-------------|------|
| 选构造 | `DefaultTypeMap.FindConstructor` 用 `GetConstructors(BindingFlags.Public \| BindingFlags.NonPublic)`，按 `public → internal/protected → private` 排序，再按参数个数升序；**参数个数为 0 立即采用** | `private` / `internal` 无参构造可用 |
| 选属性 | `GetSettableProps` 用 `BindingFlags.NonPublic`，`GetPropertySetter` 用 `GetSetMethod(true)` | `private` / `internal` setter 可写 |
| 生成代码 | `GetTypeDeserializerImpl` 用 `new DynamicMethod(..., type, skipVisibility: true)` | 跨程序集跳过可见性检查 |

官方测试佐证：`tests/Dapper.Tests/ConstructorTests.cs::TestWithNonPublicConstructor`。

### 因此实体须满足

1. 提供**非公开无参构造**（或参数个数与列数恰好匹配且按名映射的构造）；
2. 属性（`Id` 的 `protected set`、审计属性的 `internal set`）**保持非公开即可**，Dapper 会灌值；
3. **无需** `InternalsVisibleTo`——Dapper 靠 `skipVisibility` 访问，不走 C# 编译期访问检查；
   仅当 `Infrastructure` 需要用**强类型代码直接调用**非公开成员时才需另加。

### 命名映射

列名与属性名的对应见 [列名约定](#列名约定)。Dapper 默认忽略大小写并按名称匹配，
`snake_case` 列与 `PascalCase` 属性之间需通过 `SqlMapper.SetTypeMap`（`MatchNamesWithUnderscores = true`）
或 `ITypeMap` 自定义映射实现——**0.0.3 落地时须显式开启 `MatchNamesWithUnderscores`**，
否则 `creation_time` 无法匹配到 `CreationTime`。

### 反模式

| 反模式 | 纠正 |
|--------|------|
| 为图省事把审计属性开成 `public set` | 保持 `internal set`；Dapper 不需要 public setter |
| 为 Dapper 单独建 `XxxRow` DTO 再手工映射 | 实体给非公开无参构造即可，勿引入双份模型 |
| 用 `InternalsVisibleTo` 解决 Dapper 物化 | Dapper 用 `skipVisibility`，IVT 是多余的 |
| 忘记 `MatchNamesWithUnderscores` | snake_case 列映射不到 PascalCase 属性，会静默丢值 |

## 多主机架构（0.0.8 起）

职责按「谁知道什么」切三层——解析主机（Scoped，要查库）、池化客户端（Singleton，要跨请求存活）、创建客户端（无状态）：

```
HTTP Request
─────────────
   │  hostId = 主机名（hosts.name，不区分大小写）
   ▼
┌─────────────────────────────────┐
│ IDockerHostAccessor   (Scoped)  │
│  · 查 hosts 表解析主机          │  未找到 → 1300；已停用 → 1302
│  · 校验 enabled                 │
│  · 探活：临时客户端+Ping+释放   │  不经池，不污染生产连接
└────────────────┬────────────────┘
                 │ DockerHostConnection
                 ▼
┌─────────────────────────────────┐
│ IDockerClientPool     (Singleton)│
│  · 池键 = 规范化连接串 [+凭据指纹]│  同端点的主机名共享客户端
│  · 命中即复用；未命中转交工厂   │
│  · 空闲超时回收 / Invalidate    │
│  · 进程退出统一释放             │
└────────────────┬────────────────┘
                 │ 未命中
                 ▼
┌─────────────────────────────────┐
│ IDockerClientFactory  (无状态)  │
│  · 规范化端点 → DockerClientBuilder│
└────────────────┬────────────────┘
                 ▼
      Docker.DotNet.Enhanced DockerClient
```

### `IDockerHostAccessor`（Scoped 服务）

**主机来源是 `hosts` 表，不再是配置**。`hostId` 即主机名（不区分大小写）；`DockerHost.Id`（Guid）
只用于权限外键与审计，**不**进 URL / Header：

```csharp
public interface IDockerHostAccessor
{
    Task<DockerHost> ResolveAsync(string hostId, CancellationToken ct = default);          // 1300
    Task<IDockerClient> GetClientAsync(string hostId, CancellationToken ct = default);     // 1300 / 1302 / 1301
    Task<bool> TestConnectionAsync(DockerHostConnection connection, CancellationToken ct = default);
}
```

> **Scoped 的原因**：它依赖 `IHostRepository`（其取值来自当前请求）。池是 Singleton，二者生命周期不同，
> 因此「解析主机」与「池化客户端」分属两层。这也让 Singleton 侧**不依赖** Scoped 仓储——不会构成捕获依赖。
>
> **0.3.0 起**再加上基于请求 Header `X-Host-Id` 的「当前主机」解析与可见性校验。

### `IDockerClientFactory` 与 `IDockerClientPool`

**工厂只负责创建、池只负责复用**——原先「创建 + 缓存 + 回收」挤在一个类里，既让工厂承担了池的职责，
也让「连接存活判定」这种只有持有池条目才做得了的事混进了构建逻辑：

```csharp
// 无状态：给一份连接描述，还一个新建的客户端（生命周期归调用方）
public interface IDockerClientFactory
{
    IDockerClient CreateClient(DockerHostConnection connection);
}

// Singleton：复用、空闲回收、失效、释放
public interface IDockerClientPool
{
    IDockerClient GetOrCreate(DockerHostConnection connection);  // 只借不还
    void Invalidate(DockerHostConnection connection);
}
```

> **为什么叫池而不是缓存**：池里装的是操作系统资源（socket + 后台线程 + 底层 HTTP 连接池），
> 而复用它们正是它存在的理由——重建客户端的代价（丢掉已建立的连接与连接池）恰恰是它要避免的。
> 它具备收缩空闲（`ReapIdleClients`）、容量天然有界（= 主机数）与集中释放（`Dispose`）三项「池」的特征。
>
> **契约：只借不还。**调用方借用后**不得**自行释放客户端，生命周期归池管理
> （与 `IHttpClientFactory` 同一套路，因此不构成「泄漏」）。

**为什么按连接而不是按主机池化**：多个主机名可能指向同一个守护进程（如 `local` 与 `local-alias`，或同一台机器的 socket 与 socket-proxy）。以连接串为键可让它们共享同一个客户端与 HTTP 连接池——切换主机时连接不会被重建，这正是「多主机随时切换」体验的前提。`DockerHostConnection`（`Application/Abstractions`）即"连接描述"，由 `DockerHostConnection.From(DockerHost)` 从 `Core` 的 `DockerHost` 实体映射而来（`HostId` 取主机名）。

**端点规范化**（`Infrastructure/Docker/DockerEndpointNormalizer`）把各种写法收敛成唯一形式，否则"同一守护进程"会被误判成多条连接：

| 输入 | 规范形式 |
|------|---------|
| `/var/run/docker.sock`、`unix:///var/run/docker.sock`、`unix://var/run/docker.sock` | `unix:///var/run/docker.sock` |
| `//./pipe/docker_engine`、`\\.\pipe\docker_engine` | `npipe://./pipe/docker_engine` |
| `host:2375`、`http://host` | `tcp://host:2375` |

> ⚠️ 三个斜杠是刻意的：`unix://var/run/docker.sock` 会被 `Uri` 解析成 `Host="var"` + `AbsolutePath="/run/docker.sock"`（指向错误路径）；只有三斜杠形式的 `AbsolutePath` 才是完整 socket 路径。

**池生命周期**：

| 触发 | 行为 |
|------|------|
| 正常取用 | 客户端驻留进程内存，HTTP 连接池持续复用 |
| `Invalidate(connection)` | 主机配置变更 / 删除后调用（传变更**前**的连接描述）：移出池并释放（同端点的其他主机名一并受影响） |
| 空闲超时 | 默认 30 分钟（`Docker:Pool:IdleTimeout`）：取用时惰性判定 + 后台任务周期扫描（`ReapInterval`）。任一设为 0 即禁用回收 |
| 进程退出 | DI 释放全部客户端 |

> **构建失败必定清条目**：`Lazy<T>` 会永久缓存异常，不移除的话一次失败之后就再也无法重试。
>
> **长调用注意**：空闲判定依据是"最近一次取用时间"，若某次调用自身持续时长超过 `IdleTimeout`（如 0.0.9 的日志流式读取），理论上存在被回收打断的风险——调大该值或禁用回收即可。

**实现使用 `DockerClientBuilder` 风格**：

| 主机类型 | 构造方式 |
|---------|---------|
| **socket** | `new DockerClientBuilder().WithEndpoint(new Uri("unix:///var/run/docker.sock")).Build()` |
| **tcp（无认证）** | `new DockerClientBuilder().WithEndpoint(new Uri("tcp://host:2375")).Build()` |
| **tcp + TLS** | `WithEndpoint(uri).WithAuthProvider(new CertificateCredentials(cert))` |
| **tcp + Basic** | `WithEndpoint(uri).WithAuthProvider(new BasicAuthCredentials(user, pass))` |

**多传输支持**（`Docker.DotNet.Enhanced` 提供）：

| 传输 | 用途 |
|------|------|
| `LegacyHttpTransportOptions` | 默认 HTTP 传输 |
| `NativeHttpTransportOptions` | 启用 `.NET Native HTTP Handler`（实验性，性能更好） |
| `UnixSocketTransportOptions` | Linux/macOS Unix domain socket |
| `NPipeTransportOptions` | Windows named pipe |

### 前端集成

- **顶栏 `HostSelector` 组件**（`components/business/HostSelector.vue`）：主机作用域显示当前主机名 + 状态点，全局作用域显示「选择主机」占位；列出可访问主机，**切换后停留在当前资源页**；admin 可见「管理主机」入口
- **切换主机**：写入 Pinia `useHostStore.currentId`，由 `api/index.ts` 的请求拦截器自动带 `X-Host-Id` Header
- **作用域驱动**：`currentId` 不是独立状态，而是路由（`/hosts/:hostId/**`）的镜像，由路由守卫在导航时写入/清空，因此刷新与深链都能正确还原作用域
- **仅主机作用域注入 `X-Host-Id`**：全局作用域（总览 / 主机管理 / 用户管理）的请求不携带该头，否则后端会误判为针对某台机器的资源操作

### 默认主机（0.0.8 启动时）

`hosts` 表为空时（按**物理行数**判断）由 `DatabaseInitializer` 种入一台默认主机：

| 列 | 值 |
|----|-----|
| `id` | `00000000-0000-0000-0000-000000000001`（固定，便于运维脚本引用） |
| `name` | `local` |
| `type` | `socket` |
| `endpoint` | `/var/run/docker.sock`（存原文，取连接时再规范化） |
| `auth_kind` | `none` |
| `auth_config_json` | `NULL` |
| `enabled` | `1` |
| `creator_id` | `00000000-0000-0000-0000-000000000000`（`Guid.Empty` = 系统操作，启动引导无登录用户） |

> 写入走 `IHostRepository`（审计由 `RepositoryBase` 用 `TimeProvider` / `ICurrentUser` 填充），
> 而不是裸 SQL——因此无需在此维护一份必须与 DDL 对齐的插入语句。
> 判空用物理行数的原因见 [表结构](#表结构003-已落地)。

---

## 鉴权架构

### JWT 流程

```
                    ┌──────────────┐
                    │ LoginView    │
                    │ (前端)        │
                    └──────┬───────┘
                           │ POST /api/v1/auth/login
                           │ {username, password}
                           ▼
                    ┌──────────────┐
                    │ AuthController│
                    │ + AuthService│
                    └──────┬───────┘
                           │ Argon2id 校验
                           │ 生成 access (15min) + refresh (7d)
                           ▼
                    ┌──────────────┐
                    │ 数据库        │
                    │ refresh_tokens│
                    │ (可撤销表)    │
                    └──────┬───────┘
                           │
                           ▼ accessToken + refreshToken 返回前端
                    ┌──────────────┐
                    │ Pinia        │
                    │ useUserStore │
                    │ localStorage │
                    └──────────────┘
```

后续请求自动带 `Authorization: Bearer <accessToken>`。

**Access Token 过期时**：前端 axios 拦截器捕获 401 → 调用 `POST /api/v1/auth/refresh` → 用 refresh token 换新 access token → 重发原请求。

### SignalR Hub 鉴权（0.3.0）

WebSocket 不支持自定义 Header，JWT 通过 `?access_token=...` 查询参数传递：

```typescript
const connection = new HubConnectionBuilder()
  .withUrl(`/hubs/container-log?access_token=${accessToken}`)
  .build()
```

### WebSocket Terminal 鉴权（0.4.0）

为最小化 access token 泄漏风险，**独立短时 terminal token**：

```
1. 前端请求 POST /api/v1/auth/terminal-token
   → 返回 5 分钟过期 terminalToken（仅能用于 /ws/container/exec）

2. 前端建立 WebSocket:
   new WebSocket(`ws://host/ws/container/exec?containerId=xxx&cmd=/bin/sh&token=${terminalToken}`)

3. 后端验证 terminalToken 有效性 + 检查用户对 containerId 的权限

4. 建立双向流；Docker.DotNet exec + attach 桥接到 WebSocket
```

---

## SDK 隔离设计

为避免 Docker SDK 类型泄漏到上层，并支持未来切换：

```
┌────────────────────────────────────────────┐
│ 接口层 (Docker.Manager.Api)                │
│   - Controllers 只引用 Application 的接口  │
└──────────────────┬─────────────────────────┘
                   │
┌──────────────────▼─────────────────────────┐
│ 应用层 (Docker.Manager.Application)        │
│   - IDockerClientFactory (接口)            │
│   - IDockerHostAccessor  (接口)            │
│   - DTO (HostDto, ContainerDto, ...)       │
│   - ApiResult<T> / ErrorCodes              │
└──────────────────┬─────────────────────────┘
                   │
┌──────────────────▼─────────────────────────┐
│ 基础设施层 (Docker.Manager.Infrastructure) │
│   - DockerClientFactory (实现)             │
│   - DockerHostAccessor  (实现)             │
│   - XxxRepository (Dapper + SQLite)        │
│   - 引用 Docker.DotNet.Enhanced            │
└────────────────────────────────────────────┘
```

**关键收益**：
- Controller / Service 仅依赖 `Application` 的接口，**与 SDK 解耦**
- 未来切换 SDK（如回退到上游 `Docker.DotNet` 3.x 或迁移到其他 Docker SDK）只需重写 `Infrastructure` 层
- 单测可 Mock `IDockerClientFactory`，无需真实 Docker

---

## 容器镜像构建（一体化）

```dockerfile
# Stage 1: 前端构建
FROM node:20-alpine AS frontend
WORKDIR /src/Docker.Manager.UI
COPY src/Docker.Manager.UI/package*.json ./
RUN npm ci
COPY src/Docker.Manager.UI/ ./
RUN npm run build  # 产物 → dist/

# Stage 2: 后端构建
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish src/Docker.Manager.Api -c Release -o /app/publish

# Stage 3: 运行时镜像
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN useradd -u 10001 -m app
WORKDIR /app
COPY --from=backend --chown=app:app /app/publish ./
COPY --from=frontend --chown=app:app /src/Docker.Manager.UI/dist ./wwwroot
COPY scripts/backup.sh /app/scripts/
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
  CMD wget -qO- http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Docker.Manager.Api.dll"]
```

---

## 前端架构

### 目录结构

```
src/Docker.Manager.UI/src/
├── api/                   # API 封装（RPC 风格方法）
├── assets/                # 静态资源
├── components/
│   ├── common/            # 通用组件（EmptyState、StatusBadge、JsonTree、CommandPalette）
│   └── business/          # 业务组件（ContainerStatusBadge、PortMappingEditor、EnvVarEditor）
├── composables/           # 复用 Composition 函数
│   ├── useTheme.ts
│   └── useShortcuts.ts
│   # X-Host-Id 由 api/index.ts 的请求拦截器按作用域条件注入
├── layouts/
│   ├── AppLayout.vue
│   └── AuthLayout.vue
├── locales/               # i18n 语言文件
├── router/
│   ├── index.ts
│   └── guards.ts
├── stores/                # Pinia
├── styles/
│   ├── tokens.css         # CSS 变量
│   ├── uno.css            # UnoCSS 入口
│   └── overrides.css      # Element Plus 主题覆盖
├── types/                 # TypeScript 类型
├── utils/
└── views/                 # 路由对应的页面组件
```

### 通信方式

| 场景 | 方式 |
|------|------|
| 普通数据请求 | HTTP POST（axios），RPC 风格路径 |
| 实时日志（0.3.0） | SignalR WebSocket，`ContainerLogHub` |
| 实时监控（0.3.0） | SignalR WebSocket，`ContainerStatsHub` |
| 页面内通知（0.3.0） | SignalR WebSocket，`NotificationHub` |
| 容器终端（0.4.0） | 原生 WebSocket `/ws/container/exec` + terminal token |

---

## 统一响应格式

所有 `/api/v1/**` 接口均返回统一结构（`GET /healthz` 为基础设施端点，例外，见下）：

```json
{
  "success": true,
  "code": 0,
  "message": "ok",
  "data": { }
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `success` | bool | 请求是否成功 |
| `code` | int | 业务错误码（0 = 成功） |
| `message` | string | 错误描述（成功时为 "ok"） |
| `data` | object / null | 返回数据主体（失败时显式输出 `null`） |

完整错误码分段、HTTP 状态码映射与响应约定见 [api-spec.md](./api-spec.md#统一响应格式)。

---

## 异常处理与统一响应（0.0.2 起）

### 职责分层

```
错误码数值           语义分类               HTTP 状态码
ErrorCodes    ──►  ErrorCategory   ──►  ErrorHttpStatus
（应用层）           （应用层）             （接口层）
      └── ErrorCodeCatalog 提供分类与默认文案
```

> 刻意分三层：应用层只描述「错误属于哪一类」，不引入 HTTP 概念；接口层负责把分类翻译成协议状态码。两侧都能独立单测。

**为什么不能按错误码数值分段推断状态码**：同一分段内语义并不统一——`1002`（段 1000-1099）是 500、`1103`（段 1100-1199）是 429、`1301`（段 1300-1399）是 502。

### 管道顺序（不可随意调整）

```
1. ExceptionHandlingMiddleware   ← 必须最外层，否则无法捕获注册在它之前的中间件异常
2. SerilogRequestLogging         ← 异常请求降级为 Debug（详情已由第 1 层记录，避免重复告警）
3. UseStatusCodePages            ← 包装「无响应体」的 4xx/5xx
4. UseCors                       ← 白名单为空时不注册任何策略 = 默认拒绝跨域
5. MapControllers / MapHealthChecks /（仅 Development）dev 故障注入端点
```

### 异常 → 错误码 → HTTP 映射

| 异常类型 | 业务错误码 | HTTP |
|---------|-----------|------|
| `BusinessException` | 异常自带（`Code`） | 由错误码映射 |
| `ArgumentException` / `JsonException` / `BadHttpRequestException` | `1000` | 400 |
| `KeyNotFoundException` | `1001` | 404 |
| `TaskCanceledException`（未被「客户端断开」过滤器捕获） | `1003` | 408 |
| 客户端主动断开（`RequestAborted` 已取消） | — | 不写响应体，记 `Debug` |
| 其他（兜底） | `9999` | 500 |

**安全约束**：错误响应**绝不**包含堆栈、内部异常消息、异常类型名或源码位置；`9999` 的消息一律替换为固定文案。完整异常（含堆栈）与 `TraceId` 只写入服务端日志。

**基础设施端点例外**：`GET /healthz` 成功响应为裸 JSON（不含 `success` 字段），便于外部探针（Docker `HEALTHCHECK`）直接消费；仅当它抛出异常时才走统一结构。

---

## 部署架构

### Docker Socket Proxy 模式（推荐）

```yaml
services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
    restart: unless-stopped

  docker-manager:
    image: ghcr.io/blue-cloud-net/docker-manager:latest
    depends_on: [socket-proxy]
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - JWT__SECRET=<your-strong-secret-≥32-bytes>
      - ADMIN__USER=admin
      - ADMIN__PASSWORD=<initial-strong-password>
      # 主机不再是配置项（0.0.8 起为 hosts 表）：默认只有 local（/var/run/docker.sock），
      # 指向 socket-proxy 等其他主机请在启动后通过管理界面添加。
    volumes:
      - ./data:/app/data
      - ./scripts:/app/scripts
    restart: unless-stopped
```

### 独立进程部署

```
dotnet Docker.Manager.Api.dll --urls http://0.0.0.0:8080
```

要求宿主机当前用户对 `/var/run/docker.sock` 有读写权限（通常需要加入 `docker` 用户组）。
