# 开发路线图

> 路线图以**版本里程碑**为单位组织，不绑定具体日期。严格遵循**语义化版本**（SemVer）。
>
> | 版本段 | 含义 | 公开发布？ |
> |--------|------|-----------|
> | `0.0.x` | **未正式发布的开发迭代**——快速演进、API 可能破坏性变更 | ❌ 不发布到 GHCR，仅自测 |
> | `0.1.0-rc.N` | MVP 候选发布——仅修 bug、不加新功能 | ⚠️ 灰度发布到 GHCR（Pre-release） |
> | `0.1.0` | **MVP 首个 semver 稳定版**——公开 API 冻结承诺从这一版开始 | ✅ 正式发布 |
> | `0.2.0` / `0.3.0` / `0.4.0` | 功能里程碑版本 | ✅ 正式发布 |
> | `1.0.0` | **生产就绪版**——经过真实环境压测与安全审计 | ✅ 正式发布 |
>
> **CI 规则**：`v0.0.*` tag 触发构建但不推 GHCR；`v0.1.0-rc.*` / `v0.x.0` / `v1.0.0` tag 触发构建 + 推 GHCR + 创建 GitHub Release。镜像标签策略仅 semver（`v0.1.0` / `0.1.0` / `0.1` / `latest`）。
>
> 完整版本计划：13 个 0.0.x 迭代 + 1 个 RC + 4 个稳定版（0.1.0 / 0.2.0 / 0.3.0 / 0.4.0 / 1.0.0）。
---

## 进度图例与总览

> **图标约定**：版本标题与条目**首列**的图标即为其状态，全文统一。

| 图标 | 状态 | 判定口径 |
|:---:|------|----------|
| ✅ | **已完成** | 代码已合入主干，且该条目的验收标准全部通过 |
| 🚧 | **进行中** | 已开工但未闭环——本节至少一个条目尚未完成 |
| ⬜ | **未开始** | 尚未开工，条目与设计处于最终态 |
| ❌ | **明确不做** | 已判定为非目标（清单见 [features.md](./features.md#v10-不包含明确非目标)） |

> **维护规则**
>
> 1. 条目级图标**只出现在「已完成」与「进行中」的版本内**；未开始的版本仅在标题标 ⬜，条目保持干净。
> 2. 子条目默认**继承父条目的图标**；只有明确标注状态时才单独覆盖。
> 3. 一个版本内**不再有任何 ⬜ 条目**时，才能把标题从 🚧 改成 ✅。
> 4. 「验收」小节是**该版本的收口清单**，只有它整体打 ✅ 才算真正交付。

### 当前进度（截至 2026-10-13）

| 版本 | 主题 | 状态 | 备注 |
|------|------|:---:|------|
| `0.0.1` | 项目骨架 + CI 骨架 | ✅ | 4 个分层项目 + Central Package Management + `/healthz` |
| `0.0.2` | 后端基础设施 | ✅ | 异常管道 / 统一响应 / 错误码分层 / CORS / 集成测试载体 |
| `0.0.3` | 数据层与密码安全 | ✅ | SQLite / Dapper / 迁移 `0001–0004` / 四个仓储 / 审计填充 / **Argon2id 密码哈希**全部落地 |
| `0.0.4` | 认证 API（核心） | ✅ | 登录 / 当前用户 / JWT 中间件 / 限流 / 防枚举 / 初始管理员引导（原属 0.0.6）；refresh 与 logout 属 0.0.5 |
| `0.0.5` | 认证完善 + 前端基础设施 | 🚧 | 前端工作区、代理、axios 层已就位；后端 refresh / logout / 安全头 / 密码强度未开工 |
| `0.0.6` | 部署底座与初始管理员引导 | 🚧 | **初始管理员引导已随 0.0.4 落地**；Dockerfile / compose / CI 增强 / 社区文档未开工 |
| `0.0.7` | 前端登录页与基础布局 | 🚧 | 与后端**并行**推进；布局 / 主题 / 国际化 / 两段式导航已落地；登录页 UI 完成但未接后端 |
| `0.0.8` | Docker 主机抽象 | 🚧 | `hosts` 表 + 默认主机种子 + 工厂/池/访问器三层已落地；缺 API 入口 / TLS-Basic / 当前主机解析 |
| `0.0.9` | 容器 API 后端 | ⬜ | 仅占位视图 |
| `0.0.10` | 镜像 API 后端 | ⬜ | 仅占位视图 |
| `0.0.11` | 前端：容器 UI | ⬜ | 仅 `HostSelector` 等通用件已提前落地 |
| `0.0.12` | 前端：镜像 UI | ⬜ | 仅占位视图 |
| `0.0.13` | 前端：管理 UI + MVP 打磨 | ⬜ | 仅 `EmptyState` 等通用件已提前落地 |
| `0.1.0-rc.1` | MVP 候选发布 | ⬜ | |
| `0.1.0` | MVP 首个 semver 稳定版 | ⬜ | |
| `0.2.0` | Volume / Network / 镜像构建 | ⬜ | |
| `0.3.0` | 实时推送与资源粒度 RBAC | ⬜ | |
| `0.4.0` | exec 终端 / Compose / Registry | ⬜ | |
| `1.0.0` | 生产就绪版 | ⬜ | |

---

## 阶段 0：开发迭代期（0.0.x）

> 每个 0.0.x 迭代 = 1 个可独立验收的小型里程碑。允许破坏性变更，仅自测，不对外发布。

### 0.0.1 ✅ — 项目骨架 + CI 骨架

**目标**：可构建、可运行最小后端 + CI 流水线雏形。

> **0.0.1 实施决策（分层拆分）**：原规划的"三项目（Api / Core / Infrastructure）"经评审后调整为 **4 个分层项目（Core / Application / Infrastructure / Api）** + 1 个测试项目骨架。0.0.2 后废弃 DDD 战术模式：`Domain` 更名为 `Core`，移除聚合根、领域事件与 `DomainException`，保留分层依赖方向与充血模型。详细规范见 [docs/architecture.md](./architecture.md#项目依赖方向)。

后端：
- ✅ **4 个分层项目解决方案**：
  - ✅ `src/Docker.Manager.Core/`——核心层。业务实体、值对象、枚举。**仅依赖 BCL，不引用任何 NuGet 包**
  - ✅ `src/Docker.Manager.Application/`——应用层。用例编排、DTO、外部资源接口抽象（如 `IDockerClientFactory`）。依赖 Core
  - ✅ `src/Docker.Manager.Infrastructure/`——基础设施层。第三方 SDK 适配（`Docker.DotNet.Enhanced`）、仓储实现、外部服务集成。依赖 Core + Application
  - ✅ `src/Docker.Manager.Api/`——接口层。ASP.NET Core 10 宿主进程，托管 Controllers / SignalR Hubs / 静态前端。依赖 Application + Infrastructure
- ✅ `Directory.Packages.props` 启用 **Central Package Management**（`<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`），所有 NuGet 包版本号集中此文件
- ✅ `Infrastructure` 项目引入 `Docker.DotNet.Enhanced` 包（采用最新稳定版），**在 `Application/Abstractions` 中定义 `IDockerClientFactory` 接口抽象**（外部资源接口放在应用层，非领域层）
- ✅ `Docker.Manager.Api` 启动监听 `:8080`，返回 `GET /healthz` → 200 OK（Controller 风格，区别于后续 RPC 风格的 `POST /api/v1/...` 端点）
- ✅ `Directory.Build.props`：TargetFramework=net10.0、LangVersion=latest、可空引用类型开启、警告即错误
- ✅ Serilog 控制台输出（JSON 配置 + `ReadFrom.Configuration`）

测试：
- ✅ `tests/Docker.Manager.UnitTests/` 引入 xUnit + FluentAssertions + coverlet 骨架（占位烟雾用例验证测试链路）。详细覆盖节奏：0.0.3 Argon2id 哈希、0.0.4 登录、0.0.8 起集成测试使用宿主 Docker socket（`DockerFactAttribute`）

CI：
- ✅ `.github/workflows/ci.yml`：push / pull_request 到 `main` / `dev` 触发。步骤：`dotnet restore` → `dotnet build -c Release`（warn-as-error）→ `dotnet test -c Release`。NuGet 包缓存（`~/.nuget/packages`，key 基于 `*.csproj` + `Directory.Packages.props`）

仓库：
- ✅ `README.md` 占位（详细完善在 0.0.13）

**验收**：
- ✅ 本地 `dotnet build` 通过（零警告，因 `TreatWarningsAsErrors=true`）
- ✅ `dotnet test` 通过（占位烟雾用例至少 1 个）
- ✅ `/healthz` 返回 200 OK
- ✅ NuGet 还原成功拉取 `Docker.DotNet.Enhanced`
- ✅ 依赖方向校验：`Core` 项目无任何上游 NuGet 依赖（仅 BCL）

---

### 0.0.2 ✅ — 后端基础设施

**目标**：所有后续接口的统一基线。

> **0.0.2 实施决策**：
> 1. **集成测试载体提前**：原计划 0.0.8 才引入的 `tests/Docker.Manager.IntegrationTests/` **在本迭代落地**（骨架 + `WebApplicationFactory`），用于验证 HTTP 管道级契约；0.0.8 只需追加集成测试辅助设施（`DockerFactAttribute`，探测宿主 `/var/run/docker.sock`）。
> 2. **错误码分层**：错误码常量、语义分类与默认文案放 `Application/Common`（`ErrorCodes` / `ErrorCategory` / `ErrorCodeCatalog`）；「语义分类 → HTTP 状态码」放 `Api/Errors`（`ErrorHttpStatus`）。既避免应用层依赖 Web 概念，又让两侧都能被单测。
> 3. **CORS 策略延迟注册**：白名单策略通过 `IConfigureOptions<CorsOptions>` 在**选项解析期**注册，而非 DI 注册期直接读配置——后者会读到尚未定型的值（集成测试注入的内存配置即属此列），导致静默地不注册任何策略。
> 4. **`UseUrls` 改为配置**：硬编码的 `UseUrls("http://+:8080")` 改为 `appsettings.json` 的 `"Urls"` 键，使 `TestServer` 能接管监听。注意 `Urls` 优先级**高于** `ASPNETCORE_URLS`，改端口需用 `--urls` 或 `URLS`（详见 [constraints.md](./constraints.md#配置系统)）。
> 5. **顺带消除 `.editorconfig` 隐患**：统一 LF（与 `.gitattributes` 的 `* text=auto` 一致）、末尾换行统一、清除 6 个重复键、为 `[*.sh]` 显式声明 LF 以保护 0.0.6 的 `scripts/backup.sh`。`dotnet format` 的 CI 门槛仍留到 0.0.6。
> 6. **环境变量命名规范化**：失效的 `DEFAULT_HOST__ENDPOINT` → `DOCKER__DEFAULTHOST__ENDPOINT`；`JWT__Secret` → `JWT__SECRET`。
> 7. **依赖统一升到最新**：`Microsoft.Extensions.*` → 10.0.12（net10.0 对齐）；Serilog 全家桶 → `AspNetCore` 10.0.0 / `Settings.Configuration` 10.0.1 / `Sinks.File` 7.0.0；测试栈 → `Microsoft.NET.Test.Sdk` 18.10.0 / `xunit` 2.9.3 / `xunit.runner.visualstudio` 4.0.0 / `coverlet.collector` 10.0.1。
>    **例外——`FluentAssertions` 停留在 7.2.2**：8.x 起改为 Xceed 商业授权，7.x 是最后一个 Apache-2.0 版本；本项目为 MIT 开源且不引入商业授权依赖，故不跟进 8.x。
> 8. **`Docker.DotNet.Enhanced` 3.x → 4.3.3（破坏性）**：4.x 移除了 `DockerClientConfiguration` 与 `IDockerClient`，`DockerClientFactory` 改为 `DockerClientBuilder.WithEndpoint(...).Build()`；并新增 `DockerClientFactoryTests` 把「能编译」升级为「能构造出可用客户端」。
> 9. **JSON 编码策略**：中文原样输出（`JavaScriptEncoder.Create(UnicodeRanges.All)`）而非 `\uXXXX` 转义；MVC 与错误响应共用 `ApiJsonDefaults` 以防两处漂移。

后端：
- ✅ **全局异常处理中间件**（`Api/Middleware/ExceptionHandlingMiddleware`，注册为管道第一个中间件）：
  - ✅ `BusinessException` → 用其自带错误码；`ArgumentException` / `JsonException` / `BadHttpRequestException` → `1000`（400）；`KeyNotFoundException` → `1001`（404）；`TaskCanceledException` → `1003`（408）；兜底 → `9999`（500）
  - ✅ 客户端主动断开（`RequestAborted`）不写响应体，记 `Debug`；响应已开始写出时记 `Critical` 后重抛
  - ✅ 4xx 记 `Warning`（不含堆栈），5xx 记 `Error`（含完整堆栈 + `TraceId`）
- ✅ **统一响应模型**：`ApiResult` / `ApiResult<T>` + `ApiResults` 工厂（工厂放在非泛型静态类，规避 CA1000）；`BusinessException` 体系（`Validation` / `NotFound` / `Unauthenticated` / `Forbidden` / `Timeout` / `HostUnreachable` / `Internal`）
- ✅ **无响应体状态码包装**：`UseStatusCodePages` 把未知路由 404、405、415 等统一包装为 `ApiResult`；`ApiController` 的 ModelState 失败改写为 `1000`
- ✅ **日志去重**：`Serilog` 请求日志在异常请求上降级为 `Debug`，异常详情只由中间件记一次 `Error`
- ✅ **配置系统**：`appsettings.json` → `appsettings.{Environment}.json` → 环境变量 → 命令行；key 用 `__` 分隔嵌套、数组用数字下标；不合法的配置通过 `ValidateOnStart` 在**启动时**失败
- ✅ **Serilog 滚动文件输出**：生产 `/app/data/logs/app-.log`、开发 `logs/app-.log`；按天滚动，保留 14 天；UTF-8 无 BOM
- ✅ **CORS 白名单**：`Cors:AllowedOrigins` 默认空数组 = 不发出任何 `Access-Control-*` 响应头（默认拒绝跨域）；非法 origin（非绝对 URI / 带尾斜杠）启动即失败
- ✅ **开发专用故障注入端点** `POST /api/v1/dev/throw?kind=internal|business|validation|notfound|timeout`，仅 `IsDevelopment()` 时注册（生产不存在该路由）

测试：
- ✅ `tests/Docker.Manager.IntegrationTests/` 新建（xUnit + FluentAssertions + `Microsoft.AspNetCore.Mvc.Testing`），覆盖异常映射、状态码包装、CORS 与配置系统
- ✅ `UnitTests` 补充错误码表一致性、语义分类、`ApiResult` 工厂与序列化契约

**验收**：
- ✅ `GET /healthz` 成功返回裸 JSON（不含 `success` 字段）；抛异常时返回标准 `ApiResult` JSON
- ✅ `POST /api/v1/dev/throw` → 500 + `{"success":false,"code":9999,"message":"服务器内部错误","data":null}`，且响应体无堆栈 / 异常类型名 / 源码位置
- ✅ 未知路由 → 404 + `code 1001`
- ✅ `Origin: http://evil.example` → 无 `Access-Control-Allow-Origin`；配置白名单后仅命中项回显
- ✅ 环境变量 `DOCKER__POOL__IDLETIMEOUT` 可覆盖 `IOptions<DockerClientFactoryOptions>` 的 `Pool.IdleTimeout`
- ✅ 日志文件按天生成于预期路径，且同一次 5xx 只产生一条 `Error` 日志
- ✅ `dotnet test` 同时跑通 UnitTests 与 IntegrationTests

---

### 0.0.3 ✅ — 数据层与密码安全

**目标**：用户表可创建，密码可哈希校验。

> **完成（2026-10-13）**：SQLite 初始化（含外键 / `busy_timeout`）、Dapper 接入、架构迁移 `0001–0004`、
> 四个仓储（`users` / `hosts` / `roles` / `refresh_tokens`）、实体审计填充与 **Argon2id 密码哈希**均已落地。
> 设计细节见 [architecture.md](./architecture.md) 的「实体审计与软删除」「持久化实体物化约定」。

后端：
- ✅ SQLite 初始化（`/app/data/docker-manager.db`，目录不存在自动创建）
- ✅ Dapper 接入，仓储层抽象 `IUserRepository`（另含 `IHostRepository` / `IRoleRepository` / `IRefreshTokenRepository`）
- ✅ 启动时建 `users` 表（id、username、password_hash、role_id、审计列）
  - ✅ 列名遵循「属性名 ↔ 列名同名、仅大小写风格不同」的审计列约定（`docs/architecture.md` 实体审计与软删除）
  - ✅ Dapper 直接物化充血实体：实体给**非公开无参构造**即可，**不建 Row DTO、不加 InternalsVisibleTo**；
    须开启 `MatchNamesWithUnderscores` 才能把 `creation_time` 映射到 `CreationTime`
  - ✅ `IUserRepository` 实现继承 `Infrastructure/Persistence/RepositoryBase`，插入 / 更新时调用
    `MarkCreated` / `MarkModified` 写入审计（值取自 `TimeProvider` 与 `ICurrentUser`）
- ✅ `ICurrentUser`（`Application/Abstractions/`）抽象当前登录用户；`RepositoryBase` 注入
  `ICurrentUser` + `TimeProvider` 完成审计填充（0.0.4 认证落地后由 Api 层实现替换占位实现）
- ✅ `User` 实体继承 `FullAuditedEntity<TId, TUserKey>`（创建 + 修改 + **软删除**审计）
- ✅ 角色从 `UserRole` 枚举改为 `roles` 表 + `IRoleRepository`（`users.role_id` 外键），内置 admin / operator / viewer / auditor 四角色
- ✅ Argon2id 密码哈希（`Konscious.Security.Cryptography.Argon2`，MIT），禁止明文落库与日志
  - ✅ PHC 编码串（`$argon2id$v=19$m=…,t=…,p=…$salt$hash`）由 `Core/Auth/PasswordHash` 值对象负责编解码；
    算法参数随哈希一同持久化，因此日后调参不会让旧哈希失效
  - ✅ `IPasswordHasher`（`Application/Abstractions`）抽象 + `Argon2PasswordHasher`（`Infrastructure/Security`）实现；
    摘要比较一律走 `CryptographicOperations.FixedTimeEquals`
  - ✅ 参数经 `Security:Argon2` 配置，越界在**启动时**失败（`ValidateOnStart`，直接复用 `PasswordHash` 的校验规则避免两处漂移）
- ✅ 单元测试覆盖哈希/校验（编码/解析/边界 + 真实 Argon2id 的哈希、校验、篡改与参数升级）

**验收**：
- ✅ `dotnet test -c Release` 全绿（本迭代新增 69 个用例，全量 469 个）
- ✅ 依赖方向守卫：`Docker.Manager.Core` 仍**无任何 NuGet 依赖**

---

### 0.0.4 ✅ — 认证 API（核心）

**目标**：可通过 API 登录拿到 JWT。

> **完成（2026-10-13）**：除 `refresh` / `logout` / 安全响应头 / 密码强度校验（属 0.0.5）外均已落地。
> 设计细节见 [architecture.md](./architecture.md) 的「鉴权架构」。

后端：
- ✅ JWT access token（HS256，15 分钟）+ refresh token（随机 32 字节 base64url，7 天）
  - ✅ 声明：`sub` / `unique_name` / `role` / `jti`——集中定义于 `Application/Abstractions/JwtClaimNames`
    （签发方在 Infrastructure、验证方在 Api，声明名必须有一处共同定义）
  - ✅ 密钥经 `Jwt:Secret` 配置，**缺失或不足 32 字节则启动失败**（`ValidateOnStart`）
  - ✅ 刷新令牌落库的是 **SHA-256 哈希**（base64url，属持久化契约）；原文只在签发那一刻返回给客户端
- ✅ `POST /api/v1/auth/login`：校验用户名/密码，返回 access + refresh
  - ✅ **防用户名枚举（两层）**：用户名不存在与密码错误返回**完全相同的错误码与文案**；
    且用户不存在时也执行一次等价成本的**哑元校验**，使响应耗时不可区分
  - ✅ **失败计数**：按 IP + 用户名 **5 次/分钟 → 429**；窗口自首次失败起算，登录成功则清零
- ✅ `POST /api/v1/auth/me`：需认证；返回当前用户摘要（前端刷新页面时校验持久化的 token）
- ✅ `[Authorize]` 中间件 + 角色检查 `[Authorize(Roles = "admin")]`
  - ✅ `ICurrentUser` 的 Api 层实现接管 Infrastructure 的占位实现（`services.Replace`）
  - ✅ 鉴权失败同样返回统一 `ApiResult`，并区分 `1100`（未认证）/ `1101`（**已过期**）/ `1500`（无权限）
- ✅ 初始管理员引导（**原属 0.0.6，因鸡生蛋问题提前到本版**）
- ✅ 集成测试覆盖：登录成功、凭据错误、枚举不可区分、限流锁定与窗口过期、令牌过期 / 篡改 / 异密钥 / 格式非法

**验收**：
- ✅ `curl` 登录成功拿到双令牌 → 用 access token 调 `POST /api/v1/auth/me` 成功 → 不带 token 返回 401 + `1100`
- ✅ 过期 token 返回 401 + `1101`（与 `1100` 可区分——0.0.5 的静默刷新依赖这一区分）
- ✅ `dotnet test -c Release` 全绿（本迭代新增 **63** 个用例，全量 532）
- ✅ 手工：`ADMIN__USER=admin ADMIN__PASSWORD=StrongP@ss123 dotnet run` → 用 admin 登录成功

> ⚠️ **已知缺口（留给 0.0.6）**：反代后 `Connection.RemoteIpAddress` 是代理 IP，
> 而本项目尚未启用 `ForwardedHeaders`（需配合可信代理白名单）——
> 不加会将所有用户的登录失败算进同一个限流桶。

---

### 0.0.5 🚧 — 认证完善 + 前端基础设施

**目标**：完整认证闭环 + 前端可对接后端。

> **进度（2026-10-10）**：🚧 前端 `src/Docker.Manager.UI/` 工作区**提前开工**（与后端并行）：Vite 代理、axios 拦截器、Pinia 持久化、路由守卫均已就位；后端 `refresh` / `logout` / 安全响应头 / 密码强度校验尚未开工。

后端：
- ⬜ `POST /api/v1/auth/refresh`：用 refresh token 换新 access
- ⬜ `POST /api/v1/auth/logout`：撤销当前 refresh token（写入黑名单表）
- ⬜ 安全响应头中间件：X-Content-Type-Options=nosniff、X-Frame-Options=DENY、Referrer-Policy=no-referrer
- ⬜ 密码强度校验工具类（≥10 字符、含字母+数字或符号）

前端：
- ✅ `src/Docker.Manager.UI/` 工作区，Vite + Vue 3 + TypeScript + Element Plus 骨架
- ✅ `vite.config.ts` 配置 `/api` 与 `/hubs` 代理到 `http://localhost:8080`
- 🚧 Pinia `useUserStore`（user、accessToken、refreshToken 持久化到 localStorage）（落地为 `stores/auth.ts` + persistedstate）
- 🚧 Axios 实例 + 拦截器：自动注入 Authorization、`401` 时尝试 refresh 并重发原请求（注入 ✅；`401` → refresh 重发待后端就绪后补）
- ✅ Vue Router 全局守卫：未登录跳 `/login`（另含 `requiresAdmin` 拦截与 hostId 同步）

**验收**：登录页表单提交 → 拿到 token 跳首页；token 过期自动刷新。

---

### 0.0.6 ⬜ — 部署底座与初始管理员引导

**目标**：`docker run` 一行启动可登录。

后端：
- ✅ （**已随 0.0.4 落地**）启动时检测 `users` 表是否为空：若空，按环境变量 `ADMIN__USER` + `ADMIN__PASSWORD` 创建 admin（密码强度校验）
  - ✅ 判空用**物理行数**（裸 `SELECT COUNT(1)`）而非仓储计数——软删除会让业务计数为 0，从而导致重复引导并撞用户名唯一索引
  - ✅ 密码强度不满足 → **启动失败**；凭据未配置 → 只记 Warning 并正常启动
- ⬜ 健康检查端点 `GET /healthz` 增强：返回 SQLite 连通性
- Dockerfile 多阶段构建（builder 阶段：Node + .NET SDK；runtime 阶段：仅 .NET runtime + dist）
- 非 root 用户 `app`（UID 10001）运行，根文件系统只读，data 卷可写
- `/healthz` 健康检查命令
- **SQLite 启用 WAL 模式**（PRAGMA journal_mode=WAL），提高并发读性能

仓库：
- **`docker-compose.yml` 两种部署模式**：
  - 直挂 socket：`/var/run/docker.sock:/var/run/docker.sock`
  - **socket-proxy 模式**（推荐）：`tecnativa/docker-socket-proxy` 作为中介，仅授予 `CONTAINERS` / `IMAGES` / `NETWORK` / `VOLUME` 子集权限
- `scripts/backup.sh`：基于 `sqlite3 .backup` 的 SQLite 备份脚本；README 文档化 cron 示例
- `.github/release-please-config.json`：启用 Release Please（读 Conventional Commits 自动 PR + 自动 tag）
- `CHANGELOG.md` / `CONTRIBUTING.md` / `SECURITY.md` / `CODE_OF_CONDUCT.md` 出初始版本
- `.dockerignore` 扩充

CI：
- `.github/workflows/ci.yml` 增强：build 镜像 + sanity test
- `.github/workflows/release.yml`：仅 `v*.*.*` tag 触发构建 + 推 GHCR

**验收**：`docker run -e ADMIN__USER=admin -e ADMIN__PASSWORD=StrongP@ss123 ...` → 看到登录页 → 用 admin 登录成功 → 控制台/日志无密码明文；`bash /app/scripts/backup.sh` 产生备份文件。

---

### 0.0.7 🚧 — 前端登录页与基础布局

**目标**：可视化的登录体验 + 全局导航骨架 + UnoCSS/主题/快捷键基建。

> **进度（2026-10-10）**：🚧 前端**先行推进**，不阻塞后端主线。UnoCSS、主题系统、两段式导航、总览页、国际化、路由守卫已完成；登录页与后端尚未接通。

前端（详细见 `docs/ui-guidelines.md`）：
- ✅ **UnoCSS 接入**：`unocss` + `presetUno` + `presetTypography` + `presetIcons`（Iconify 按需）
- ✅ **主题系统**：`composables/useTheme.ts`（亮色 / 暗色 / 跟随系统 + 蓝/绿/紫主题色）
- 🚧 `LoginView.vue`：居中卡片 + Element Plus 表单 + 主题切换按钮（UI 完成，未接后端登录 API）
- ✅ **主布局** `layouts/AppLayout.vue`：顶栏 64px + 左侧 200px 可折叠侧边栏 + 内容区；移动端侧边栏变抽屉
  - ✅ **两段式信息架构**：全局作用域（`meta.scope='global'`：总览 / 全局设置 / 主机管理 / 用户管理）与主机作用域（`meta.scope='host'`：`/hosts/:hostId/{dashboard,containers,images,volumes,networks}`）使用**不同侧边栏菜单**
  - ✅ **主机作用域进入 URL**（`/hosts/:hostId/**`），可深链 / 可分享；路由守卫负责把 `hostId` 同步到 `useHostStore`，未知 hostId 重定向 `/overview`
  - ✅ 主机作用域进入 API 路径（`/api/v1/hosts/{hostId}/...`），全局作用域端点不含该段
  - ✅ 侧边栏菜单**从路由表自动派生**（按 `meta.scope` / `showInSidebar` / `requiresAdmin` 过滤），新增路由无需改布局
- **总览页 `OverviewView.vue`**：登录后的默认落地页。**长条形**列出所有可见主机（名称 / 类型 / endpoint / 版本与运行时长 / 四类资源计数 / 状态 + 行内按钮）+ 总数与在线数摘要；**整行可点**进入主机作用域
  - **主机分组**：按 `order` 聚合展示（生产 / 测试 / 本机 / 未分组），无成员的分组不渲染
  - **行内操作**：编辑（admin，占位）/ 查看详情 / 状态图，均 `@click.stop` 防止触发整行导航
- **主机状态页 `HostStatusView.vue` + `HostStatusPanel.vue`**：运行状态（在线/离线）、运行时长、四类资源计数与 CPU/内存占位（真实数据待 SignalR 0.3.0）。总览卡片的「详情 / 状态图」按钮与该页**复用同一组件**
- **主机侧边栏菜单（7 项）**：状态 / 编排 / 容器 / 镜像 / 数据卷 / 网络 / 事件。其中**编排**（Compose，排 0.4.0）与**事件**（操作审计，排 1.0.0 之后）本次**仅建占位菜单与占位页**，不调整 roadmap 排期
- **`useHostStore` 不做持久化**：hosts / groups 来自后端，快照必然陈旧；`currentId` 由 URL 驱动，无需 localStorage（旧版本的持久化键在启动时一次性清理）
- 🚧 **顶栏组件**：`Breadcrumb.vue`、`UserMenu.vue`（无独立组件文件，功能内联在 `AppLayout.vue`）
- 🚧 **侧边栏组件**：`Sidebar.vue`（按 `route.meta.showInSidebar` 与 `requiresAdmin` 过滤）（同上，内联实现）
- ✅ 国际化：vue-i18n，`locales/zh-CN.json` / `locales/en-US.json`；key 用 dotted 命名，缺失 fallback 英文
- 🚧 占位首页 `DashboardView.vue`（落地为按主机作用域的 `HostDashboardView.vue`）
- ✅ 路由守卫实现未登录跳 `/login` 与 `requiresAdmin` 拦截

**验收**：登录页英文/中文切换正常；切换主题颜色变化；侧边栏可折叠且状态持久化；移动端（375px）侧边栏变抽屉可正常开关。

---

### 0.0.8 🚧 — Docker 主机抽象

**目标**：可注册多个 Docker 主机，按主机切换操作。

> **进度（2026-10-13）**：🚧 后端「数据层 → 客户端池 → 主机解析」已打通：`hosts` 表与默认主机种子已落地；
> 客户端按 **工厂（无状态创建）/ 池（Singleton 复用与回收）/ 访问器（Scoped 查库解析）** 三层拆分，主机来源从配置切到数据库。
> **仍缺 API 入口**：主机管理 API、用户管理 API、当前主机（路由值 `hostId`）解析与可见性校验。

后端：
- ✅ 新建 `hosts` 表（id、name、type=socket|tcp、endpoint、auth_kind、auth_config_json、enabled、审计列）
  - ✅ `name` 不区分大小写唯一（`WHERE is_deleted = 0` 部分索引），且**不可改**（它是对外主机标识）
- ✅ 启动时若 hosts 表为空，插入默认主机：`name="local"`、`type=socket`、`endpoint="/var/run/docker.sock"`
- ✅ **主机来源改为数据库**：`Docker:Hosts` 配置节删除；`Docker:ClientCache` 更名为 `Docker:Pool`
- ✅ `IDockerClientPool`（Singleton）：按**规范化连接串**池化客户端，同端点的主机名共享连接
  - ✅ 空闲超时回收（`ReapIdleClients` + 后台任务）、`Invalidate`、进程退出统一释放
  - ✅ 构建失败必定清条目（`Lazy<T>` 会永久缓存异常，不然一次失败后就再也无法重试）
- ✅ `IDockerHostAccessor`（Scoped）：`hostId`（主机名）→ `hosts` 表 → 客户端；`Enabled=false` → `1302`
  - ✅ 连通性测试（临时客户端 + Ping，**不经池**）
  - ⬜ 由 Api 中间件从**路由值** `hostId` 解析当前主机（写入 Scoped `ICurrentHost`）；路径形如 `/api/v1/hosts/{hostId}/...`，**无缺省值**（与 0.3.0 的可见性校验一起做）
- ✅ `IDockerClientFactory`（无状态）：只用 `DockerClientBuilder` 创建客户端
  - ✅ socket：`WithEndpoint(new Uri("unix:///var/run/docker.sock"))`
  - ✅ TCP（无认证）：`WithEndpoint(new Uri("tcp://host:2375"))`
  - ⬜ `Infrastructure` 项目额外引入 `Docker.DotNet.Enhanced.X509` + `Docker.DotNet.Enhanced.BasicAuth`
  - ⬜ TCP + TLS：`WithEndpoint(...)` + `WithAuthProvider(new CertificateCredentials(cert))`
  - ⬜ TCP + Basic：`WithEndpoint(...)` + `WithAuthProvider(new BasicAuthCredentials(user, pass))`
- ⬜ 主机管理 API（admin 专用）：
  - ⬜ `POST /api/v1/admin/hosts/list` / `create` / `{hostId}/update` / `{hostId}/delete` / `test`
- ⬜ 用户管理 API（admin 专用）：
  - ⬜ `POST /api/v1/admin/users/list` / `create` / `delete` / `update-password`
- ⬜ `IResourcePermissionService` 接口定义（0.1.0 仅实现 host 可见性）

测试（从本迭代起为强制要求）：
- ⬜ `tests/Docker.Manager.IntegrationTests/`（**项目骨架已于 0.0.2 落地**）新增集成测试辅助设施：`DockerFactAttribute` 探测宿主 `/var/run/docker.sock`，不可达时跳过用例（**不引入 Testcontainers**，见 [constraints.md](./constraints.md#集成测试门槛)）
- ⬜ 容器/镜像/Volume/Network 等新 API 必须配集成测试，CI 卡
- ✅ 不设行覆盖率门槛

---

### 0.0.9 ⬜ — 容器 API 后端

**目标**：所有容器 CRUD 操作可通过 API 完成。

后端：
- 容器 API：
  - **`POST /api/v1/hosts/{hostId}/containers/create`**（`CreateContainerParameters` 完整字段映射）
  - `POST /api/v1/hosts/{hostId}/containers/list`（支持 `all` + 分页）
  - `POST /api/v1/hosts/{hostId}/containers/start` / `stop` / `restart` / `remove` / `logs` / `inspect`
- 所有接口走 `IDockerHostAccessor` 获取客户端，按 host 粒度做可见性校验
- `Docker.DotNet.Enhanced` 异常统一映射：
  - `DockerApiException` → 通用 Docker 错误（code 1402）
  - `DockerContainerNotFoundException` → code 1401（资源不存在）
  - `DockerImageNotFoundException` → code 1401
  - `TaskCanceledException` → code 1403（操作超时）

**验收**：curl 完整跑通容器 create/list/start/stop/restart/remove/logs/inspect；非授权 host 返回 code 1501。

---

### 0.0.10 ⬜ — 镜像 API 后端

**目标**：镜像 CRUD + 拉取（含进度回传）。

后端：
- 镜像 API：
  - `POST /api/v1/hosts/{hostId}/images/list`（分页）
  - `POST /api/v1/hosts/{hostId}/images/pull`（`image` + `tag`）
  - `POST /api/v1/hosts/{hostId}/images/remove`（`imageId` + `force`）
  - `POST /api/v1/hosts/{hostId}/images/inspect`（`imageId`）
- Pull 进度：使用 `IProgress<JSONMessage>` 在服务端聚合，**0.0.10 不接入 SignalR**，仅在响应 `data` 一次性返回最终结果；进度信息写到 Serilog 日志。**SignalR 实时进度推迟到 0.3.0**

**验收**：pull `nginx:latest` 完成后返回镜像 ID 与大小。

---

### 0.0.11 ⬜ — 前端：容器 UI

**目标**：可视化容器管理。

前端：
- API 封装层 `src/Docker.Manager.UI/src/api/container.ts`：RPC 风格方法封装
- `src/Docker.Manager.UI/src/stores/host.ts`：Pinia 当前主机选择
- **顶栏 `HostSelector` 下拉组件**（`components/business/HostSelector.vue`）：主机作用域显示当前主机名 + 状态点，全局作用域显示「选择主机」占位；列出可访问主机，**切换后停留在当前资源页**；写入 Pinia `useHostStore` → 路由跳转到 `/hosts/{新hostId}/{当前资源页}`；admin 可见「管理主机」入口
- `ContainerListView.vue`：表格（名称、镜像、状态、端口、创建时间）+ 操作按钮（启动/停止/重启/删除）+ 状态自动刷新（轮询 5 秒）
- **`CreateContainerDialog.vue`**：可视化创建表单（镜像下拉 + 名称 + 端口映射 + 环境变量键值对 + Volume 挂载 + 重启策略选择 + 高级选项折叠面板「网络模式 / 资源限制 / capabilities」）
- `ContainerLogsView.vue`：行数选择 + tail 加载 + 时间过滤 + 自动滚动开关
- `ContainerInspectView.vue`：JSON 树形展示 + 基本信息卡片

---

### 0.0.12 ⬜ — 前端：镜像 UI

**目标**：可视化镜像管理。

前端：
- `ImageListView.vue`：表格（仓库、标签、镜像 ID、创建时间、大小）+ 删除按钮
- `ImagePullDialog.vue`：镜像名 + 标签输入 + 进度展示（轮询 0.0.10 的 pull 结果，**不接入 SignalR**）+ 完成后自动刷新列表
- `ImageInspectView.vue`：JSON 树形展示

---

### 0.0.13 ⬜ — 前端：管理 UI + MVP 打磨

**目标**：完成所有 0.1.0 必备 UI，准备进入 RC。

前端：
- **命令面板** `components/common/CommandPalette.vue`：Ctrl+K / Cmd+K 唤起；搜索所有路由 + 常用操作（跳 Containers / 跳 Images / 创建容器 / 拉取镜像）；Fuse.js 模糊匹配
- **全局快捷键** `composables/useShortcuts.ts`：`g+c` / `g+i` / `g+v` / `g+n` / `g+d` / `?` 跳转与帮助弹窗
- `UserManagementView.vue`（admin 可见）：用户列表 + 创建对话框 + 删除确认 + 改密对话框
- `HostManagementView.vue`（admin 可见）：主机列表 + 创建/编辑对话框 + test 连通性按钮 + 删除确认
- `ChangePasswordDialog.vue`：任意用户可改自己密码（从顶栏用户菜单进入）
- 路由守卫增强：按 `route.meta.requiresAdmin` 隐藏菜单项 + 拦截直接访问
- 通用组件库扩充：`EmptyState`、`StatusBadge`、`JsonTree`、`CopyableText`
- 业务组件：`ContainerStatusBadge`、`ImageSize`、`PortMappingEditor`、`EnvVarEditor`、`VolumeMountEditor`

仓库：
- `README.md` 完善：徽章（CI status / GHCR pulls）、功能截图占位、文档链接矩阵、`docker run` 一键启动命令、**Docker Socket 挂载安全风险说明 + 推荐 socket-proxy 模式**
- `docs/overview.md` / `docs/constraints.md` / `docs/features.md` / `docs/roadmap.md` / `docs/architecture.md` / `docs/api-spec.md` / `docs/ui-guidelines.md` 完成第一版完整内容

后端：
- 全量日志脱敏审计：grep 检查确认无明文密码
- 性能基线：容器 list 100 个容器场景 P95 < 500ms

**验收**：完整走查全部 UI 流程，无 console error；切换语言、主题、主机不丢登录态；容器创建对话框可成功创建 nginx 容器并访问。

---

### 0.1.0-rc.1 ⬜ — MVP 候选发布

**目标**：0.0.13 后冻结功能，仅修 bug。

- 不引入新功能
- 集中修复 QA 反馈
- 文档审阅与中英文一致性校对
- CI 自动构建 `v0.1.0-rc.1` tag → 推 GHCR → 创建 Pre-release

**验收**：3 名外部用户在自托管环境跑通完整流程。

---

## 0.1.0 ⬜ — MVP 首个 semver 稳定版 🎉

**对外承诺**：
- 公开 API 路径与请求/响应结构从 0.1.0 起冻结（向后兼容到 1.0.0）
- **Docker SDK 抽象层不变**：`Core` 中的 `IDockerClientFactory` / `IDockerHostAccessor` 接口稳定；`Infrastructure` 中的 SDK 版本可平滑升级，不影响 Controller / Service 层
- SQLite schema 版本化管理（启动时检查 + 自动迁移）

**包含**：
- 登录认证（JWT + refresh）
- 容器完整生命周期管理（含创建）
- 镜像列表 / 拉取 / 删除 / 详情
- 多主机管理（本地 socket + 远程 TCP，可选 TLS / Basic 认证）
- 用户管理 + admin/user 二角色
- 主机粒度资源可见性
- UnoCSS + Element Plus UI 框架
- 命令面板 + 全局快捷键

**不包含**（按路线图后续）：
- 实时日志 / 监控 / 通知 → 0.3.0
- 容器 exec 终端（仅 WebSocket 长连接交互式）→ 0.4.0
- Volume / Network → 0.2.0
- 镜像构建 → 0.2.0
- Docker Compose / Stack → 0.4.0
- 私有 Registry → 0.4.0
- Agent 模式 → 1.0.0 后
- 资源粒度 RBAC（容器级） → 0.3.0
- 一次性 stats 查询 → 0.3.0 仅提供实时流
- 系统 prune（容器/镜像/网络/卷清理） → 不在 v1.0 范围
- 容器镜像 push 到 Registry → 0.4.0
- 批量删除 → 0.2.0

---

## 0.2.0 ⬜ — Volume / Network / 镜像构建

**目标**：把 Docker 主要资源类型管理补齐；支持上传 Dockerfile 构建镜像；增加批量删除能力。

后端：
- Volume API：`POST /api/v1/hosts/{hostId}/volumes/{list,create,remove,batch-remove,inspect}`
- Network API：`POST /api/v1/hosts/{hostId}/networks/{list,create,remove,inspect}`
- 镜像构建：`POST /api/v1/hosts/{hostId}/images/build`（multipart：Dockerfile + context tar），通过 `IProgress<JSONMessage>` 推送进度到 SignalR 临时频道（**仅本请求客户端可见**）
- 容器批量删除：`POST /api/v1/hosts/{hostId}/containers/batch-remove`（接受 `containerIds[]`，并行删除，返回每个的成功/失败状态）
- 镜像批量删除：`POST /api/v1/hosts/{hostId}/images/batch-remove`（接受 `imageIds[]`）
- 私有 Registry 凭据**存储**（暂不暴露 pull/push 入口，凭据加密落 SQLite）

前端：
- Volume / Network 列表/创建/详情页
- 镜像构建对话框（文件上传 + 实时进度条）
- 容器/镜像列表页增加**多选 checkbox** + 「批量删除」操作按钮（带二次确认）

---

## 0.3.0 ⬜ — 实时推送与资源粒度 RBAC

**目标**：上线实时日志 / 监控 / 异常通知；落地按 host/container 粒度的资源授权。

后端：
- SignalR Hub：`/hubs/hosts/{hostId}/container-log`（订阅日志）、`/hubs/hosts/{hostId}/container-stats`（订阅统计）、`/hubs/notification`（全局通知，不含 hostId）
- Hub 鉴权：JWT 通过 `?access_token=...` 查询参数传递（WebSocket 不支持自定义 Header）
- `ContainerEventWatcher`（后台 `Docker.Events` 监听）→ 推到 `NotificationHub`
- 资源权限表：`user_host_permissions`（user_id × host_id，权限位 read/write/admin）、`user_container_permissions`（细粒度补充）
- `ResourcePermissionMiddleware`：在容器/镜像/Volume/Network 操作前校验用户对 hostId（必要时 containerId）有 read/write 权限
- 授权管理 API：`POST /api/v1/admin/hosts/{hostId}/permissions/{grant,revoke,list}`
- 镜像 pull 进度改用 SignalR 推送

前端：
- 容器日志页改用 SignalR 实时流（保留历史 tail 加载）
- 容器详情页增加实时 CPU/内存/网络图（轻量图表：自实现 sparkline 或 echarts）
- 全局通知条（容器 died → toast）
- 用户管理页增加「分配主机权限」操作

---

## 0.4.0 ⬜ — 生态：exec 终端 / Compose / Registry

**目标**：覆盖 Portainer CE 90% 用户场景。

后端：
- **新增 `POST /api/v1/auth/terminal-token`**（admin/user 专用）：返回 5 分钟过期的独立短时 token，仅能用于 WebSocket 连接 `/ws/hosts/{hostId}/containers/exec`；不继承 access token 的全 API 权限
- WebSocket 端点 `/ws/hosts/{hostId}/containers/exec?containerId=...&cmd=/bin/sh&token=<terminalToken>`（独立于 SignalR，原始双向流，**鉴权专用 terminal token**，不接受 access token）
- Stack 管理：`POST /api/v1/hosts/{hostId}/stacks/{list,deploy,update,stop,remove}`，内部用 Docker.DotNet 调用 + label 过滤（`com.docker.compose.project=<name>`）
- 私有 Registry 登录：`POST /api/v1/registry/{login,list,remove}` + 凭据安全存储（DPAPI / OS keyring）
- 镜像 push：`POST /api/v1/hosts/{hostId}/images/push`（使用已配置的 Registry 凭据）

前端：
- 容器详情页内嵌 xterm.js 终端面板（AttachAddon + FitAddon）；打开终端时先请求 terminal-token，再用其连接 WebSocket
- Stack 管理页（上传 YAML、查看服务列表）
- Registry 配置页（添加、测试连接、删除）
- 镜像列表增加「push 到 Registry」按钮（仅当目标镜像已配置 Registry 时可用）

---

## 1.0.0 ⬜ — 生产就绪版

**目标**：经过真实环境压测、安全审计、API 稳定性承诺生效。

- 真实生产环境 ≥30 天稳定运行（0.4.0 基础上）
- 安全审计：依赖漏洞扫描、容器逃逸风险评估、CSP/SRI 配置
- **API 文档完整化**：OpenAPI 自动生成（`Microsoft.AspNetCore.OpenApi`）+ **Scalar UI**（替代 Swagger UI，可在 `/scalar/v1` 访问）
- 压测报告：1000 容器主机 list P95 / SignalR 并发 / SQLite 写吞吐
- 国际化文案完整校对
- 升级指南文档（从 0.x 升级到 1.0.0 的注意事项）
- v1.0.0 起承诺 API 向后兼容到 2.0.0

可推迟到 1.0.0 之后：
- Agent 模式（目标主机部署轻量 Agent）
- OAuth / OIDC 登录
- 审计日志
- 多实例 + 外部数据库（PostgreSQL）支持
- 公开 demo 实例
