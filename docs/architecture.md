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
| 持久化接口 | `IXxxRepository`，定义在 `Core`，实现在 `Infrastructure/Persistence/` | `IUserRepository`、`IHostRepository` |
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
| **持久化接口返回活连接** | `IHostRepository.ListAsync()` 返回的对象里带 `DockerClient` | 仓储只负责持久化；客户端获取走 `IDockerClientFactory` |

**代码示例：`DockerHost` 实体骨架（0.0.8 规划示意）**

> 以下代码**仅作示例**，实际 `DockerHost` 在 0.0.8 迭代才落地。

````csharp
// src/Docker.Manager.Core/Hosts/DockerHost.cs
namespace Docker.Manager.Core.Hosts;

public sealed class DockerHost : Entity<Guid>
{
    /// <summary>显示名（唯一）。</summary>
    public string Name { get; private set; } = default!;

    /// <summary>连接描述。</summary>
    public HostEndpoint Endpoint { get; private set; } = default!;

    /// <summary>是否启用；停用后不再接受该主机的请求。</summary>
    public bool Enabled { get; private set; }

    // 私有构造：仅供静态工厂使用，确保对象一诞生就满足全部不变量
    private DockerHost() { }

    /// <summary>注册一台新的 Docker 主机。</summary>
    public static DockerHost Register(Guid id, string name, HostEndpoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);

        return new DockerHost
        {
            Id = id,
            Name = name,
            Endpoint = endpoint,
            Enabled = true,
        };
    }

    /// <summary>停用该主机（幂等）。</summary>
    public void Disable()
    {
        Enabled = false;
    }

    /// <summary>重新启用该主机。</summary>
    public void Enable() => Enabled = true;
}

// src/Docker.Manager.Core/Hosts/HostEndpoint.cs
public sealed class HostEndpoint : ValueObject
{
    public string Url { get; }

    public HostEndpoint(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("HostEndpoint URL 不能为空", nameof(url));
        }

        Url = url;
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Url;
    }
}

// src/Docker.Manager.Core/Hosts/IHostRepository.cs
public interface IHostRepository
{
    Task<DockerHost?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DockerHost>> ListAsync(CancellationToken ct = default);
    Task AddAsync(DockerHost host, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
}
````

要点回顾：

- `DockerHost` 继承 `Entity<Guid>`，行为（`Disable` / `Enable`）在实体自身
- `HostEndpoint` 是不可变值对象，构造时校验不变量
- `IHostRepository` 定义在 `Core`，实现在 `Infrastructure/Persistence`
- 私有构造 + 静态工厂确保不变量在创建时即成立

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
- **0.0.8 起支持多主机**：按**规范化连接串**缓存客户端（多主机名指向同一守护进程即共享连接），并提供 `Invalidate` 与空闲回收；连接描述 `DockerHostConnection` 定义在 `Application/Abstractions`
- **后续**：TCP + TLS / Basic 认证（`DockerHostAuth` 槽位已预留），主机来源由配置切换为 `hosts` 表

### 数据访问层

- **持久化接口**（`IUserRepository`、`IHostRepository` 等）定义在 `Docker.Manager.Core/`
- **仓储实现**位于 `Docker.Manager.Infrastructure/Persistence/`：使用 `Dapper` 执行手写 SQL
- 数据库：SQLite + `Microsoft.Data.Sqlite`，**WAL 模式**启用（PRAGMA journal_mode=WAL）
- 文件存储：`/app/data/docker-manager.db`
- 应用启动时自动执行建表 SQL（若表不存在）
- 存储内容：用户信息、资源授权关系、Docker 主机配置、Registry 凭据（加密）

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

```
HTTP Request                ASP.NET Core               Docker.DotNet
─────────────              ─────────────              ────────────
                          ┌──────────────────┐
X-Host-Id: prod-host-1 →  │ IDockerHost     │  →  ┌──────────────────┐
                          │ Accessor (Scoped)│     │ IDockerClient   │
                          │                  │     │ Factory         │
                          │ 解析 hostId      │     │                  │
                          │ 查 hosts 表      │     │ 缓存 / 按需创建  │
                          │ 校验可见性       │     │ DockerClient    │
                          └────────┬─────────┘     └────────┬─────────┘
                                   │                        │
                                   ▼                        ▼
                          ┌──────────────────┐     ┌──────────────────┐
                          │ IDockerClient    │ →  │ Docker.DotNet    │
                          │ (hostId 对应的   │     │ .Enhanced        │
                          │  缓存实例)       │     │ DockerClient     │
                          └──────────────────┘     └──────────────────┘
```

### `IDockerHostAccessor`（Scoped 服务）

从请求 Header `X-Host-Id`（缺省 `"local"`）解析当前 host：

```csharp
public interface IDockerHostAccessor
{
    string HostId { get; }              // 当前请求的 host id
    Task<Host> GetCurrentAsync();       // 从 hosts 表查当前 host 配置
    Task<bool> CanAccessAsync(string hostId);  // 检查当前用户是否有权限访问
}
```

### `IDockerClientFactory`（Singleton 服务）

按**规范化连接串**返回缓存的客户端。`hostId` 只是"配置（0.0.8 起为数据库）里的一个名字"，**不参与缓存键**：

```csharp
public interface IDockerClientFactory
{
    Task<IDockerClient> GetClientAsync(string hostId, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(DockerHostConnection connection, CancellationToken cancellationToken = default);
    void Invalidate(string hostId);
}
```

**为什么按连接而不是按主机缓存**：多个主机名可能指向同一个守护进程（如 `local` 与 `local-alias`，或同一台机器的 socket 与 socket-proxy）。以连接串为键可让它们共享同一个客户端与 HTTP 连接池——切换主机时连接不会被重建，这正是「多主机随时切换」体验的前提。`DockerHostConnection`（`Application/Abstractions`）即"连接描述"，0.0.8 由 `Core` 中的 `DockerHost` 实体映射而来。

**端点规范化**（`Infrastructure/Docker/DockerEndpointNormalizer`）把各种写法收敛成唯一形式，否则"同一守护进程"会被误判成多条连接：

| 输入 | 规范形式 |
|------|---------|
| `/var/run/docker.sock`、`unix:///var/run/docker.sock`、`unix://var/run/docker.sock` | `unix:///var/run/docker.sock` |
| `//./pipe/docker_engine`、`\\.\pipe\docker_engine` | `npipe://./pipe/docker_engine` |
| `host:2375`、`http://host` | `tcp://host:2375` |

> ⚠️ 三个斜杠是刻意的：`unix://var/run/docker.sock` 会被 `Uri` 解析成 `Host="var"` + `AbsolutePath="/run/docker.sock"`（指向错误路径）；只有三斜杠形式的 `AbsolutePath` 才是完整 socket 路径。

**缓存生命周期**：

| 触发 | 行为 |
|------|------|
| 正常取用 | 客户端驻留进程内存，HTTP 连接池持续复用 |
| `Invalidate(hostId)` | 主机配置变更 / 删除后调用：移出缓存并释放（同端点的其他主机名一并受影响） |
| 空闲超时 | 默认 30 分钟（`Docker:ClientCache:IdleTimeout`）：取用时惰性判定 + 后台任务周期扫描（`ReapInterval`）。任一设为 0 即禁用回收 |
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

- **顶栏 `HostSelector` 组件**：显示当前主机 + 在线状态点；列出可访问主机；admin 可见「管理主机」入口
- **切换主机**：写入 Pinia `useHostStore`，所有后续 axios 请求通过 `composables/useDockerRequest.ts` 拦截器自动带 `X-Host-Id` Header

### 默认主机（0.0.8 启动时）

启动时若 `hosts` 表为空，自动插入默认主机：

```sql
INSERT INTO hosts (id, name, type, endpoint, enabled, creation_time, creator_id)
VALUES ('00000000-0000-0000-0000-000000000001', 'local', 'socket', '/var/run/docker.sock', 1, datetime('now'), '00000000-0000-0000-0000-000000000000');
```

> 列名遵循「实体属性 ↔ 数据库列同名（仅大小写风格不同）」的约定，详见
> [实体审计与软删除](#实体审计与软删除)。`creator_id` 取 `Guid.Empty` 表示**系统操作**（启动引导无登录用户）。

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
WORKDIR /src/web
COPY src/web/package*.json ./
RUN npm ci
COPY src/web/ ./
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
COPY --from=frontend --chown=app:app /src/web/dist ./wwwroot
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
src/web/src/
├── api/                   # API 封装（RPC 风格方法）
├── assets/                # 静态资源
├── components/
│   ├── common/            # 通用组件（EmptyState、StatusBadge、JsonTree、CommandPalette）
│   └── business/          # 业务组件（ContainerStatusBadge、PortMappingEditor、EnvVarEditor）
├── composables/           # 复用 Composition 函数
│   ├── useTheme.ts
│   ├── useShortcuts.ts
│   └── useDockerRequest.ts  # 自动注入 X-Host-Id
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
      - DOCKER__HOSTS__0__ENDPOINT=tcp://socket-proxy:2375
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
