# 技术约束

> 本文档定义项目所采用的**技术栈版本基线**、**安全约束**、**CI/CD 约束**、**部署约束**与**边界条件**。
> 版本号策略严格遵循 SemVer，详细路线图见 [roadmap.md](./roadmap.md)。

---

## 技术栈选型

### 后端

| 组件 | 选型 | 版本约束 | 说明 |
|------|------|---------|------|
| 运行时 | .NET | **10 LTS**（支持到 2028-11） | C# 14 |
| 架构风格 | **分层架构 + 充血模型** | 4 个后端项目 | `Core` / `Application` / `Infrastructure` / `Api`，详细见 [architecture.md](./architecture.md#项目依赖方向) |
| Web 框架 | ASP.NET Core | 10 | WebAPI + Minimal API + SignalR Hub |
| Docker 交互 | [`Docker.DotNet.Enhanced`](https://github.com/testcontainers/Docker.DotNet) | **4.3.3** | fork 自 `dotnet/Docker.DotNet`；面向 Docker Engine API **v29.4.1**（实际版本与守护进程协商）；AOT 兼容 |
| Docker TLS 认证 | `Docker.DotNet.Enhanced.X509` | 最新稳定 | 远程 TCP 可选 TLS |
| Docker Basic 认证 | `Docker.DotNet.Enhanced.BasicAuth` | 最新稳定 | 远程 TCP 可选 Basic |
| 数据访问 | Dapper | **2.1.89** | 手写 SQL，不引 ORM |
| 数据库 | SQLite + `Microsoft.Data.Sqlite` | **10.0.12**（跟随 .NET） | 单节点嵌入式，**WAL 模式**启用 |
| 密码哈希 | `Konscious.Security.Cryptography.Argon2` | **1.3.1** | Argon2id。选它而非 `Isopoh.Cryptography.Argon2`：Isopoh 采用 **CC-BY-4.0**（Creative Commons 内容许可，官方不建议用于软件），与本项目 MIT 许可不契合；Konscious 为 **MIT** 且生态用量最大。⚠️ 它只输出原始字节，PHC 编码串（`$argon2id$v=19$m=…,t=…,p=…$salt$hash`）由本项目 `Core/Auth/PasswordHash` 值对象自建 |
| JWT | `Microsoft.AspNetCore.Authentication.JwtBearer` | 跟随 .NET | 内置 |
| 实时通信 | ASP.NET Core SignalR | 10 | 原生集成 |
| SignalR 客户端 | `@microsoft/signalr` | **9+** | 跟随 .NET 主版本 |
| MCP 服务器（0.18.0） | [`ModelContextProtocol.AspNetCore`](https://github.com/modelcontextprotocol/csharp-sdk) | 最新稳定 | 官方 C# SDK（Microsoft + Anthropic 共同维护）；Streamable HTTP 承载于 `/mcp`，工具用 `[McpServerTool]` 标注 |
| 日志 | Serilog | 最新稳定 | 控制台 + 滚动文件 |
| OpenAPI（1.0.0） | `Microsoft.AspNetCore.OpenApi` + **Scalar** | 最新稳定 | 替代 Swagger UI |

> **SDK 选型说明**：选用 `Docker.DotNet.Enhanced` 是因为上游 `Docker.DotNet` 3.x 已停摆多年，而 fork 持续维护、同步 Docker Engine API。应用层通过 `IDockerClientFactory` / `IDockerHostAccessor` 抽象隔离 SDK，未来可平滑回退到上游 3.x。详见 [architecture.md](./architecture.md)。
>
> **4.x 迁移注记（0.0.2）**：`Docker.DotNet.Enhanced` 4.x 移除了 `DockerClientConfiguration` 与 `IDockerClient`，客户端构建统一改为 `DockerClientBuilder.WithEndpoint(...).Build()`。请勿再引用旧类型；该回归由 `tests/Whalefleet.IntegrationTests/Infrastructure/DockerClientFactoryTests.cs` 守住（构建客户端本身不建立连接，因此测试无需真实 Docker）。

### 前端

| 组件 | 选型 | 版本约束 | 说明 |
|------|------|---------|------|
| 框架 | Vue | **3.5+** | Composition API + `<script setup>` |
| 语言 | TypeScript | **5.7+** | 强类型；5.x 范围下限固定 |
| CSS 框架 | **UnoCSS** | 最新稳定 | utility-first / 按需原子类 |
| UI 组件库 | Element Plus | **2.14+** | 与 UnoCSS 组合：UnoCSS 负责布局，Element Plus 负责复杂交互组件 |
| UnoCSS 预设 | `presetUno` + `presetTypography` + `presetIcons` | — | Iconify 按需加载 |
| 构建工具 | Vite | **7+** | 开发期快速构建 |
| 状态管理 | Pinia | **4** | Vue 3.5 适配 |
| 终端组件 | xterm.js | **5.5+** | 容器 exec 终端（0.8.0） |
| 国际化 | vue-i18n | **11** | Vue 3.5 适配；中文 / 英文双语 |
| 包管理 | pnpm | 最新稳定 | 比 npm/yarn 快、磁盘省 |

### 配套工具

| 组件 | 选型 | 说明 |
|------|------|------|
| NuGet 包管理 | Central Package Management（`Directory.Packages.props`） | .NET 10 原生支持；所有 NuGet 版本集中管理 |
| 代码格式化（后端） | `dotnet format` | CI 中执行 |
| 代码格式化（前端） | Prettier + ESLint | CI 中执行 |
| 测试（后端） | xUnit + FluentAssertions（**7.2.2，勿升 8.x**：8.x 起改为 Xceed 商业授权）+ `Microsoft.AspNetCore.Mvc.Testing`（集成测试宿主） | 单元 + 集成 |
| 测试（前端） | Vitest + Vue Test Utils | 组件测试 |

### 分层与充血模型约定（0.0.1 起强制）

| 项 | 约定 |
|----|------|
| 实体命名 | 名词单数，继承 `Whalefleet.Core.Common.Entity<TId>` |
| 值对象命名 | 名词，继承 `Whalefleet.Core.Common.ValueObject`，**必须不可变**（init-only 或 readonly） |
| 枚举命名 | 名词（如 `DockerHostType`、`HostPermissions`），与对应实体同命名空间。**仅用于封闭集合**；可运行时扩展的概念（如角色）必须建模为表 + 实体，**不得**用枚举 |
| 持久化接口 | `IXxxRepository`，定义在 `Core`，实现在 `Infrastructure/Persistence/`。**不允许反向依赖**（`Core` 不得引用 Dapper / SQLite 等）。CRUD 共同部分继承 `Core/Common/IRepository<TEntity, TId>`，具体接口只写自己特有的查询 |
| 外部资源接口 | `IXxxFactory` / `IXxxAccessor` 等，定义在 `Application/Abstractions/`，实现在 `Infrastructure/`。典型：`IDockerClientFactory`、`IDockerHostAccessor` |
| 充血要求 | 实体的行为与不变量写在实体方法内（推荐「私有构造 + 静态工厂」），不得只当数据袋 |
| 审计接口 | `Core/Common/Auditing/`，**全部只读**（`get` 无 `set`）。细粒度：`IHasCreationTime` / `IHasModificationTime` / `ISoftDelete` / `IHasDeletionTime` / `ICreationAuditedObject<TUserKey>` / `IModificationAuditedObject<TUserKey>` / `IAuditedObject<TUserKey>` / `IDeletionAuditedObject<TUserKey>` / `IFullAuditedObject<TUserKey>` |
| 审计基类 | 三级：`CreationAuditedEntity<TId, TUserKey>` → `AuditedEntity<TId, TUserKey>` → `FullAuditedEntity<TId, TUserKey>`。**不提供** `SoftDeleteEntity`（纯软删除自行实现 `ISoftDelete`） |
| 审计属性 | 一律 `internal set` + `public` `MarkCreated` / `MarkModified` / `Delete` / `Restore` 写入；**不得**开 `public set` 绕过 |
| 审计时间 | 一律 `DateTime` **UTC**；由仓储基类从 `TimeProvider.GetUtcNow().UtcDateTime` 取值，**实体不自行取当前时间** |
| 审计人 ID | 泛型 `TUserKey` **不加类型约束**（支持 `Guid` / `long` / `string`）；「无用户」用**默认值哨兵** `default(TUserKey)` |
| 审计列名 | 属性名与列名**同名、仅大小写风格不同**：`creation_time` / `creator_id` / `last_modification_time` / `last_modifier_id` / `is_deleted` / `deletion_time` / `deleter_id` |
| Dapper 物化 | 实体给**非公开无参构造**即可被 Dapper 直接物化（`skipVisibility`）；**不需要** Row DTO，**不需要** `InternalsVisibleTo`。须显式开启 `MatchNamesWithUnderscores` |
| 软删除 | `DeletionTime == null` ⇔ `IsDeleted == false`，由 `Delete()` / `Restore()` 成对维护；`Delete` / `Restore` 均**幂等** |
| 不变量违反 | 抛 BCL 的 `ArgumentException`（含 `ArgumentNullException`），中间件映射为 `1000` / 400；不定义领域异常类型 |
| 用例失败 | 抛 `BusinessException`（`Application/Common/`），自带错误码 |
| 响应模型 | Entity 不直接作为 HTTP 响应返回；在应用服务里做 Entity → DTO 映射 |
| 反模式 | 贫血模型、Entity 直接返回 DTO、实体跨边界互相持有对象引用、`Core` 引用 NuGet 包（仅 BCL）、审计属性开 `public set`、为 Dapper 另建 Row DTO、忘记 `MatchNamesWithUnderscores` |

> DDD 战术模式（聚合根、领域事件、领域服务）已整体移除；`Core` 的职责是**业务实体与值对象**。

详细示例与反模式清单见 [architecture.md](./architecture.md#分层与充血模型约定)。

---

## 软删除查询过滤约定

> 适用于所有实现 `ISoftDelete` 的实体。**代码契约在 0.0.3 随 `RepositoryBase` 落地**；本节当前为约定。

| 场景 | 行为 |
|------|------|
| 仓储生成的 `SELECT` | **默认追加 `WHERE is_deleted = 0`**，软删除数据对业务查询不可见 |
| 按主键查询 | 命中软删除行时按「未找到」处理（返回 `null` / 抛 `KeyNotFoundException` → `1001`），**不暴露**行已删除这一事实 |
| 计数 / 存在性判断 | 一并过滤，避免统计口径把回收站数据算进去 |
| 唯一约束 | 须用 SQLite **部分索引** `UNIQUE (username) WHERE is_deleted = 0`，否则软删除行会永久占用业务键 |
| 显式包含已删除 | 仅回收站 / 数据修复 / 审计类用例可传 `includeDeleted: true` 放开；**禁止**在常规业务路径使用 |
| 物理清理 | 超过保留期由离线维护脚本执行 `DELETE ... WHERE is_deleted = 1 AND deletion_time < ?`；应用运行期不物理删除 |

**为什么默认过滤而不是"查出来再判"**：过滤下沉到 SQL 才能走索引，且避免漏判——应用层过滤一旦有
某个 `Query` 忘了加 `if (x.IsDeleted) continue`，软删除数据就会泄漏到业务结果里。

**反模式**：

| 反模式 | 后果 |
|--------|------|
| 在应用服务里遍历结果手工过滤 `IsDeleted` | 列表接口易漏；数据量大了才暴露性能问题 |
| 仓储提供无过滤的 `ListAll()` 供业务直接调用 | 软删除语义被绕过 |
| 部分索引缺失 | 软删除的用户名 / 主机名无法被再次使用 |

---

## 配置系统

### 配置源与优先级

后加载的来源覆盖先加载的：

| 顺序 | 来源 | 说明 |
|------|------|------|
| 1 | `appsettings.json` | 基线默认值 |
| 2 | `appsettings.{Environment}.json` | 环境覆盖，由 `ASPNETCORE_ENVIRONMENT` 决定加载哪一个 |
| 3 | 环境变量 | 容器部署的主要配置手段 |
| 4 | 命令行参数 | 优先级最高 |

### 环境变量命名规则

| 规则 | 说明 | 示例 |
|------|------|------|
| 层级分隔 | 用 **`__`（双下划线）** 表达配置键中的 `:` | `Docker:Pool:IdleTimeout` → `DOCKER__POOL__IDLETIMEOUT` |
| 数组 | 用**数字下标**表达索引 | `Cors:AllowedOrigins:0` → `CORS__ALLOWEDORIGINS__0` |
| 大小写 | 键比较**不区分大小写**（`ConfigurationKeyComparer` 与各 provider 的字典均为 OrdinalIgnoreCase），故 `JWT__Secret` 与 `JWT__SECRET` 等价。**新增配置项统一采用全大写下划线风格** | — |

### 关键环境变量对照表

| 配置键 | 环境变量 | 默认值 | 引入版本 |
|--------|---------|--------|---------|
| `Docker:Pool:IdleTimeout` | `DOCKER__POOL__IDLETIMEOUT` | `00:30:00` | 0.0.8 |
| `Docker:Pool:ReapInterval` | `DOCKER__POOL__REAPINTERVAL` | `00:05:00` | 0.0.8 |
| `Persistence:DatabasePath` | `PERSISTENCE__DATABASEPATH` | 生产 `/app/data/whalefleet.db`、开发 `data/whalefleet.db` | 0.0.3 |
| `Persistence:CommandTimeoutSeconds` | `PERSISTENCE__COMMANDTIMEOUTSECONDS` | `30` | 0.0.3 |
| `Persistence:Pooling` | `PERSISTENCE__POOLING` | `true` | 0.0.5 |
| `Cors:AllowedOrigins:0` | `CORS__ALLOWEDORIGINS__0` | （空，拒绝全部跨域） | 0.0.2 |
| `Urls` | `URLS` | `http://+:8080` | 0.0.2 |
| `Security:Argon2:MemorySizeKiB` | `SECURITY__ARGON2__MEMORYSIZEKIB` | `65536`（64 MiB） | 0.0.3 |
| `Security:Argon2:Iterations` | `SECURITY__ARGON2__ITERATIONS` | `3` | 0.0.3 |
| `Security:Argon2:Parallelism` | `SECURITY__ARGON2__PARALLELISM` | `4` | 0.0.3 |
| `Security:Argon2:SaltSizeBytes` | `SECURITY__ARGON2__SALTSIZEBYTES` | `16` | 0.0.3 |
| `Security:Argon2:HashSizeBytes` | `SECURITY__ARGON2__HASHSIZEBYTES` | `32` | 0.0.3 |
| `Jwt:Secret` | `JWT__SECRET` | （无；**缺失时自动生成到 `/app/data/jwt.key`**） | 0.0.4 |
| `Jwt:Issuer` / `Jwt:Audience` | `JWT__ISSUER` / `JWT__AUDIENCE` | `whalefleet` | 0.0.4 |
| `Jwt:AccessTokenLifetime` | `JWT__ACCESSTOKENLIFETIME` | `00:15:00` | 0.0.4 |
| `Jwt:RefreshTokenLifetime` | `JWT__REFRESHTOKENLIFETIME` | `7.00:00:00` | 0.0.4 |
| `Admin:User` | `ADMIN__USER` | `admin`（仅 `users` 表为空时用于引导） | 0.0.4 |
| `Admin:Password` | `ADMIN__PASSWORD` | （无；**缺失时自动生成并写入 `/app/data/initial-admin.txt`**） | 0.0.4 |
| `Admin:AutoBootstrap` | `ADMIN__AUTOBOOTSTRAP` | `true`（账号由外部预置时置 `false`） | 0.0.15 |

> **主机不是配置项**（0.0.8 起）：`Docker:Hosts` 已删除。主机是 `hosts` 表里的数据；首次启动时若表为空，
> 宿主会种入一台默认主机（`name=local`、`type=socket`、`endpoint=/var/run/docker.sock`）。
> 其余主机待 0.0.8 的主机管理 API（`POST /api/v1/admin/hosts/create`）落地后通过界面添加。

> ⚠️ **`Urls` 与 `ASPNETCORE_URLS` 的关系**（0.0.2 实测确认）：`appsettings.json` 中的 `Urls` 优先级**高于** `ASPNETCORE_URLS` 环境变量与 `launchSettings.json` 的 `applicationUrl`——因为 `WebApplicationBuilder` 把应用配置层叠加在含 `ASPNETCORE_` 前缀环境变量的宿主配置之上。
>
> 要更换监听端口，请使用（优先级自高至低）：
> 1. 命令行参数：`dotnet Whalefleet.Api.dll --urls http://0.0.0.0:9090`
> 2. 无前缀环境变量：`URLS=http://0.0.0.0:9090`

> **配置校验**：不合法的配置在**启动时**即失败（`ValidateOnStart`），不会拖到第一次请求。例如 `Cors:AllowedOrigins` 含尾斜杠会直接导致启动失败。

> **`Persistence:Pooling` 为什么可关**（0.0.5 起）：驱动默认开连接池，池会一直占住数据库文件。
> 生产环境应当保持开启（省掉每次操作的文件打开）；但**集成测试必须关闭**——
> 测试之间要靠删除临时目录来清理，池化连接会让文件被占住，
> 而清池的唯一手段 `SqliteConnection.ClearAllPools()` 是**进程级全局操作**，
> 会连带释放并行运行的其他测试正在使用的连接句柄（实测表现为偶发
> `ObjectDisposedException: SQLitePCL.sqlite3`）。关掉池化后每个连接用完即释放，
> 测试之间自然隔离，不再需要任何全局清理。

---

## 安全约束

### 鉴权与令牌

| 项 | 策略 |
|----|------|
| 密码哈希 | **Argon2id**（`Konscious.Security.Cryptography.Argon2` 1.3.1，MIT），禁止明文落库与日志 |
| 密码强度 | ≥10 字符，含字母 + 数字或符号 |
| JWT 算法 | HS256 |
| JWT access token 有效期 | **15 分钟** |
| JWT refresh token 有效期 | **7 天**，写入数据库可撤销表 |
| 初始管理员引导 | 启动时若 `users` 表为空则创建：凭据取自 `ADMIN__USER` + `ADMIN__PASSWORD`；**密码未配置时自动生成**，写入 `/app/data/initial-admin.txt`（`0600`，**不进日志**）。可用 `ADMIN__AUTOBOOTSTRAP=false` 彻底关闭 |
| JWT Secret 来源 | **优先**环境变量 `JWT__SECRET`（需 ≥ 32 字节，显式给得过短则启动失败）；**缺失时**自动生成到 `/app/data/jwt.key`（`0600`）并于后续启动复用。开发环境例外：`appsettings.Development.json` 提供公开的占位密钥 |
| 0.8.0 终端短时 token | `POST /api/v1/auth/terminal-token` 返回 5 分钟过期 token，**仅能用于 WebSocket `/ws/hosts/{hostId}/containers/exec`** |
| 0.18.0 个人访问令牌（PAT） | MCP 客户无法交互登录，改用 PAT：库里只存 **Argon2id 哈希**，明文仅创建时返回一次；可撤销、可设过期；**不走 `auth/refresh` 语义** |

### 0.9.0 起新增的配置键

| 配置键 | 用途 |
|--------|------|
| `Alerts:*` | 告警规则阈值与冷却期（三级严重度各一组） |
| `Notifications:*` | 通知渠道（Webhook URL / 邮件 SMTP） |
| `Webhooks:*` | 资源 Webhook 的令牌密钥 —— **必须随数据库一起备份**，否则恢复后全部 Webhook 静默失效 |
| `Mcp:*` | `AllowWrite`（默认 `false`）、`AllowedHosts`（DNS-rebinding 白名单，不匹配返回 421） |

### 速率限制

| 接口 | 限制 |
|------|------|
| `POST /api/v1/auth/login` | 按 IP + 用户名，**5 次/分钟 → 429**（错误码 `1103`） |

**实现方式**：进程内 `MemoryCache` 计数器，key = `客户端 IP + 归一化用户名`，绝对过期 60 秒；
计数在**登录失败后**递增，登录成功则清零。

> **为什么不用 ASP.NET Core 的 `AddRateLimiter`**：限流器的分区键取自请求元数据
> （路径 / Header / `Connection.RemoteIpAddress`），而「用户名」在**请求体**里——读请求体需提前缓冲，
> 代价与副作用都不小。本项目的登录频率极低（自托管管理面板），一个内存计数器足够，
> 且能在应用服务里直接给出 `1103` 业务错误码与统一文案。
>
> **已知边界**：计数器按**进程**隔离，多实例部署时限流会被按实例数放大。多实例是 1.0.0 之后的目标，
> 届时需改为共享存储。

### HTTP 安全响应头（0.0.5 起强制）

| Header | 值 |
|--------|----|
| X-Content-Type-Options | `nosniff` |
| X-Frame-Options | `DENY` |
| Referrer-Policy | `no-referrer` |

### CORS（0.0.2 起）

| 项 | 策略 |
|----|------|
| 默认值 | `Cors:AllowedOrigins` 为空数组 → **不发出任何 `Access-Control-*` 响应头**，等价于完全拒绝跨域 |
| 白名单格式 | 每项必须是绝对 `http`/`https` URI 且**不带尾斜杠**；不合规**启动即失败** |
| 配置方式 | `CORS__ALLOWEDORIGINS__0`、`CORS__ALLOWEDORIGINS__1` …（数组用数字下标） |
| 放行范围 | `AllowAnyHeader` + `AllowAnyMethod` + `AllowCredentials`（因白名单是显式枚举的具体 origin，而非 `AllowAnyOrigin`） |
| 生产环境 | 默认同源，**禁用 CORS**（一体化镜像托管前端） |
| 开发环境 | 不开放跨域，统一走 Vite 代理（`/api` → `http://localhost:8080`，`/hubs` → 同上） |

### Docker Socket 挂载（⚠️ 高危）

> **Whalefleet 容器挂载 `/var/run/docker.sock` 等于具备宿主机 Docker 的 root 权限**，单容器漏洞 = 宿主机沦陷。

**推荐部署模式（socket-proxy）**：

```yaml
# docker-compose.yml 推荐写法
services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy:v0.5.0
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
      POST: 1       # ⚠️ 必须为 1：写操作的总闸，关掉则整个 API 退化为只读
      SWARM: 0
      INFO: 1
      PING: 1
      VERSION: 1
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro

  whalefleet:
    image: ghcr.io/blue-cloud-net/whalefleet:latest
    depends_on: [socket-proxy]
    environment:
      # 主机不再是配置项（0.0.8 起为 hosts 表）：默认只有 local（/var/run/docker.sock），
      # 指向 socket-proxy 等其他主机请在启动后通过管理界面添加。
      - PERSISTENCE__DATABASEPATH=/app/data/whalefleet.db
    volumes:
      - ./data:/app/data
```

> **⚠️ `POST` 与子集开关不是互斥关系**。上游文档：「When disabled, only `GET` and `HEAD`
> operations are allowed, meaning any section of the API is read-only.」
> 子集开关（`CONTAINERS` 等）限制的是**能碰哪些 API 分区**，
> `POST` 限制的是**这些分区里能不能写**——两层都要开才完整可用。
> 只开子集不开 `POST` 的症状极具误导性：列表页一切正常，但创建 / 启停 / 删除容器
> 与拉取镜像一律 `403`，而开关列表看上去全是开的。
>
> 另：`INFO` 不能省（主机启动时间靠 `GetSystemInfoAsync`），`PING` 是连通性探测，
> `VERSION` 供概览页展示版本。仓库根 [docker-compose.yml](../docker-compose.yml) 是权威副本。

详细的安全声明与部署模式见仓库根 [README.zh-CN.md](../README.zh-CN.md#️-安全声明docker-socket-挂载)（[English](../README.md#️-security-notice-docker-socket-mounting)）。
socket-proxy 的 `POST` 总闸必须为 `1`，否则只放行 `GET` / `HEAD`，表现为「列表正常、启停全 403」。
仓库根 [docker-compose.yml](../docker-compose.yml) 是权威副本；修改时四处示例需同步
（`docs/overview.md`、`docs/architecture.md`、两份 `README`）。

### 日志输出基线（0.0.2 起）

| 项 | 策略 |
|----|------|
| 输出目标 | 控制台 + 滚动文件（`Serilog.Sinks.File`） |
| 文件路径 | 生产 `/app/data/logs/app-.log`；开发环境相对路径 `logs/app-.log`（非容器环境不存在 `/app/data`） |
| 滚动周期 | 按天（`rollingInterval: Day`） |
| 保留数量 | 14 个文件（`retainedFileCountLimit: 14`） |
| 文件编码 | UTF-8 无 BOM（中文日志可直接阅读） |
| 异常记录 | 5xx 记 `Error` 且含完整堆栈；4xx 记 `Warning` 且**不含堆栈**（避免噪声与被误报为告警） |
| 请求日志 | `Serilog` 请求日志在「发生异常」的请求上降级为 `Debug`——异常详情统一由全局异常中间件记录，避免同一次失败产生两条 Error 日志以及重复告警计数 |

### 日志脱敏（0.0.14 起强制）

- 启动时执行 grep 审计：确认无明文密码、access token、refresh token 写入日志
- Serilog 配置 `.MinimumLevel.Information()` + `.Filter.ByExcluding(...)` 过滤敏感字段
- 禁止在结构化日志的 `MessageTemplate` 中直接拼接密码/token

---

## CI/CD 约束

### Git 工作流

| 项 | 策略 |
|----|------|
| 主分支 | `main`（仅稳定发布）+ `dev`（集成分支） |
| 分支保护 | 1 review + CI 通过 |
| 合并策略 | Squash merge |
| PR 标题 | 走 Conventional Commits（squash merge 后成为主 commit） |
| DCO | **不要求** Signed-off-by |

### Conventional Commits 严格规范

- 允许 `feat!:`、`fix!:` 等破坏性变更前缀
- scope **可选**，但改动落在某个模块或项目级关注点上时**必须带**；
  取值与组合写法见 [AGENTS.md §提交规范](../AGENTS.md#提交规范)
- Type 限定：`feat` / `fix` / `docs` / `style` / `refactor` / `perf` / `test` / `build` / `ci` / `chore`
- **subject ≤ 30 字**，中文描述、动词开头、不加句号、单一 scope（禁逗号并列）
- **一次提交只做一件事**：不「顺手重构」、不把无关格式化混进功能提交
- 禁 `/`、`+`、`§x`、`Phase N`、`RM-` / `TASK-` 式编号；
  **版本号 `0.0.x` 不禁**（它是交付批次，不是工作流编号）

### Release Please（0.0.15 启用）

- 读 Conventional Commits 推导版本号，开一个**版本号 PR**，合并该 PR 即**自动打 tag + 建 GitHub Release**
- **不生成 CHANGELOG**（配 `skip-changelog: true`）：`CHANGELOG.md` 与 `CHANGELOG.zh-CN.md`
  全部由人工维护。小节写在**版本号 PR 内**，因为合并的那一瞬间 tag 就产生了，
  没有「先合并、后补写」的窗口
- 配置：`.github/release-please-config.json`；状态文件：`.github/release-please-manifest.json`
  （两者路径在 `release-please.yml` 里显式传给 action，不依赖默认值——改路径时不必同时改代码）
- 触发分支**仅 `main`**：`dev` 上的提交随时可变，在那里开 PR 会让版本号 PR 反复长大，
  也让 tag 有可能指向尚未合并的提交
- `bootstrap-sha` 指向版本策略重构的那次提交：仓库没有任何历史 tag，
  不设它则首次运行的提交范围会从仓库起点开始算
- **版本策略**：`bump-minor-pre-major: true`（`feat!` 在 1.0.0 之前只算小版本）
- **不启用** `bump-patch-for-minor-pre-major`（它会让 `feat` 只 bump patch，永远长不出 `0.x.0`）
- **不启用** `skip-github-release`：它的文档明写会**连 tag 一起关掉**
  （*Release-Please still requires releases to be tagged*）
- 指定版本（`1.0.0`、`1.0.0-rc.1`）在提交信息里写一行 `Release-As: <version>`

> ⚠️ Release Please 用 `GITHUB_TOKEN` 开的 PR **不触发** `ci.yml`，
> 且仓库需在 Settings → Actions → General 开启
> 「Allow GitHub Actions to create and approve pull requests」。
> 真正的 CI 门是前一次 `dev → main` 的 PR。

### 镜像发布（0.0.15 起）

| 项 | 策略 |
|----|------|
| 镜像仓库 | `ghcr.io/blue-cloud-net/whalefleet` |
| 触发点 | **仅 tag**（`v*.*.*`）。tag 是「版本已定」的不可变事实，而合并 PR 到 tag 之间还有一次人工确认 |
| 标签策略 | 正式版推四个：`v1.2.3` / `1.2.3` / `1.2` / `latest`；候选版只推两个精确版本：`v1.0.0-rc.1` / `1.0.0-rc.1` |
| `0.x` | **只构建做 sanity test，不推 GHCR**；且 0.x 实际不打 tag |
| `1.0.0-rc.*` | 推 GHCR 作为 Pre-release，**不推 `latest`**——按 `latest` 部署的人不该吃到未验证版本 |
| 推送前 | 先在 runner 上起容器冒烟（`/healthz` + 前端首页 + 非 root）。tag 推送后即已被引用，此时才发现起不来只能制造混乱 |
| Release 创建 | **由 Release Please 独占**，本 workflow 不重复创建（两者都写同一 tag 的 Release 时，后跑的会用自动 notes 覆盖掉手写正文） |

### 集成测试门槛

- **0.0.8 起强制**：每个新 API 必须配集成测试，CI 卡
- 需要真实 Docker 的用例直接使用宿主 `/var/run/docker.sock`（以 `DockerFactAttribute` 在 socket 不可达时 `Skip`），**不引入 Testcontainers**——本项目的被测依赖就是宿主 Docker daemon 本身，而非需要装进容器的第三方服务
- **不设行覆盖率门槛**（避免过度追求数字）

---

## API 约定

- 风格：**RPC 风格**（非 RESTful）+ **主机作用域路径**
- 路径前缀：`/api/v1/`
- 两条作用域：主机作用域 `/api/v1/hosts/{hostId}/{resource}/{action}`；全局作用域 `/api/v1/auth/...` 与 `/api/v1/admin/...`
- 示例：`POST /api/v1/hosts/local/containers/start`，而非 `PUT /api/v1/hosts/local/containers/{id}`
- **不变式**：`hostId` 只出现在路径中，任何请求 Body 都不得含 `hostId` 字段
- 完整错误码分段与接口清单见 [api-spec.md](./api-spec.md)

---

## 部署约束

### Docker 容器部署

- 镜像内监听 **`:8080` HTTP**（不内置 HTTPS）
- **生产环境必须前置 HTTPS 反代**（Caddy / Traefik / nginx），详细文档化在 `README.md`
- 必须挂载 `/var/run/docker.sock` 或使用 socket-proxy
- 数据卷 `./data:/app/data`（SQLite 数据库与日志）

### 独立进程部署

```
dotnet Whalefleet.Api.dll --urls http://0.0.0.0:8080
```

要求宿主机当前用户对 `/var/run/docker.sock` 有读写权限（通常需要加入 `docker` 用户组）。

### 运行时用户

- 容器内以非 root 用户 `app`（UID 10001）运行
- 根文件系统**只读**，可写路径仅 `/app/data` 卷

---

## 多主机连接约束（分阶段）

| 阶段 | 支持的连接方式 |
|------|--------------|
| 0.0.8 ~ 1.0.0 | 本地 Unix Socket（`/var/run/docker.sock`）+ 远程 TCP（可选 TLS / Basic 认证） |
| 1.0.0 之后 | Agent 模式（目标主机部署轻量 Agent，可选） |

---

## SQLite 部署细节

| 项 | 策略 |
|----|------|
| 文件路径 | `/app/data/whalefleet.db` |
| Journal 模式 | **WAL**（PRAGMA journal_mode=WAL）提高并发读性能 |
| 备份脚本 | `/app/scripts/backup.sh`（基于 `sqlite3 .backup`） |
| 备份策略 | README 文档化 cron 示例（每日 03:00 → `./data/backups/`，保留 7 天） |

---

## 边界条件

### 单实例限制

- **SQLite 适用于单节点部署，不支持多实例水平扩展**
- 仅 docs/constraints.md 提及，不在 README 显眼位置
- 1.0.0 之后考虑多实例 + 外部数据库（PostgreSQL）支持

### 监控数据

- **宿主机级实时 CPU / 内存 / 网络 / 负载不做**：Docker Engine API 没有宿主机级指标端点
  （`/info` 只给 `NCPU` / `MemTotal` 这样的静态上限，`docker stats` 只针对容器）。
  用容器聚合值冒充宿主机指标会让同一页面出现两套口径，比不给更糟
- 主机级实时指标需目标主机上的 Agent，属 1.0.0 之后（见 [backlog.md](./backlog.md)）
- 实时推送（SignalR），**不持久化历史指标**（v1.0 范围）

### 镜像构建

- 0.1.0 仅支持从 Docker Hub pull
- 镜像构建（Dockerfile 上传）→ 0.4.0
- 私有 Registry login → 0.4.0

### 容器终端

- 0.8.0 仅 WebSocket 长连接交互式终端（xterm.js）
- **不做一次性 exec API**（`POST /api/v1/hosts/{hostId}/containers/exec`）

### 容器 stats

- 0.5.0 仅 SignalR 实时流
- **不做一次性 stats API**（`POST /api/v1/hosts/{hostId}/containers/stats` 单次查询）

### Prune / 清理

- **资源级 prune 在 0.7.0 交付**：镜像 / 卷两个资源级清理
- **一键全清（`system/prune`，一次清四类）不做**——把「删多少」的决定权完全交给
  守护进程，而本项目的界面强调「先看到再确认」

### 公开 demo

- v1.0 之后再考虑
- 0.x 与 1.0.0 不提供 demo 实例

### 浏览器支持

- 仅支持现代浏览器（Chrome、Firefox、Edge 最近两个主要版本）
- 完整响应式：手机可用，所有页面提供独立移动 UI

### 国际化

- key 用 dotted 命名（如 `container.list.title`）
- 缺失 fallback 英文
- 翻译走手动 PR
- 仅中文 / 英文双语
