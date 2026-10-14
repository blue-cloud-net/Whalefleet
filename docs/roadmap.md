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
> 完整版本计划：14 个 0.0.x 迭代（0.0.1–0.0.5、0.0.7–0.0.15，**0.0.6 为空号**，见下方「维护规则」）+ 1 个 RC + 5 个稳定版本（0.1.0 / 0.2.0 / 0.3.0 / 0.4.0 / 1.0.0）。
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
> 5. 版本编号即**执行顺序**。原 `0.0.6`（部署底座）空有编号却排在功能迭代中间，已改编号为 `0.0.15` 并移到 0.0.x 末位；`0.0.6` 自此为**空号**，不再代表任何里程碑。

### 当前进度（截至 2026-10-13）

| 版本 | 主题 | 状态 | 备注 |
|------|------|:---:|------|
| `0.0.1` | 项目骨架 + CI 骨架 | ✅ | 4 个分层项目 + Central Package Management + `/healthz` |
| `0.0.2` | 后端基础设施 | ✅ | 异常管道 / 统一响应 / 错误码分层 / CORS / 集成测试载体 |
| `0.0.3` | 数据层与密码安全 | ✅ | SQLite / Dapper / 迁移 `0001–0004` / 四个仓储 / 审计填充 / **Argon2id 密码哈希**全部落地 |
| `0.0.4` | 认证 API（核心） | ✅ | 登录 / 当前用户 / JWT 中间件 / 限流 / 防枚举 / 初始管理员引导（原属部署迭代）；refresh 与 logout 属 0.0.5 |
| `0.0.5` | 认证完善 + 前端基础设施 | ✅ | refresh / logout / 安全响应头 / 密码强度；前端接入真实登录并实现 401 静默续期 |
| `0.0.7` | 前端登录页与基础布局 | ✅ | 布局 / 主题 / 国际化 / 两段式导航；登录页接真实 API；顶栏与侧边栏拆成独立组件（面包屑由纯函数派生） |
| `0.0.8` | Docker 主机抽象 | ✅ | 数据层 / 客户端池 / 访问器三层 + 当前主机中间件 + TLS·Basic 认证 + 主机与用户管理 API；管理界面已随 0.0.13 落地 |
| `0.0.9` | 容器 API 后端 | ✅ | 查询（list / inspect / logs）与变更（create / start / stop / restart / remove）全部落地；11 + 19 条端点集成测试（含真实守护进程上的容器生命周期） |
| `0.0.10` | 镜像 API 后端 | ✅ | 列表（含搜索）/ 详情 / 拉取 / 删除四个端点全部落地；23 条端点集成测试 |
| `0.0.11` | 前端：容器 UI | ✅ | 列表 / 生命周期操作 / 创建表单 / 日志 / 详情全部落地；5 秒轮询、页面隐藏时暂停；`HostSelector` 与主机 store 由 0.0.7 / 0.0.8 提前落地 |
| `0.0.12` | 前端：镜像 UI | ✅ | 列表（服务端搜索）/ 拉取 / 详情全部落地；删除区分「只解绑标签」与「真的删掉」；悬挂镜像占位 |
| `0.0.13` | 前端：管理 UI | ✅ | 用户 / 主机 / 分组管理界面与改密入口；后端随本版补 `host_groups` 表与三个分组端点 |
| `0.0.14` | 前端：MVP 打磨 | ✅ | 命令面板 / 快捷键 / 通用与业务组件 / README 与文档定稿 / 端点日志脱敏 + 列表性能基线 |
| `0.0.15` | 部署底座与发布流水线 | ⬜ | **原 `0.0.6`**，改排到 0.0.x 末位；初始管理员引导已随 0.0.4 落地，本版只剩 Dockerfile / compose / CI 增强 / 社区文档 |
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
- ✅ `README.md` 占位（详细完善在 0.0.14）

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
> 5. **顺带消除 `.editorconfig` 隐患**：统一 LF（与 `.gitattributes` 的 `* text=auto` 一致）、末尾换行统一、清除 6 个重复键、为 `[*.sh]` 显式声明 LF 以保护部署迭代的 `scripts/backup.sh`。`dotnet format` 的 CI 门槛留到 0.0.15（有部署流水线时一并接入）。
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
- ✅ 初始管理员引导（**原属部署迭代，因鸡生蛋问题提前到本版**）
- ✅ 集成测试覆盖：登录成功、凭据错误、枚举不可区分、限流锁定与窗口过期、令牌过期 / 篡改 / 异密钥 / 格式非法

**验收**：
- ✅ `curl` 登录成功拿到双令牌 → 用 access token 调 `POST /api/v1/auth/me` 成功 → 不带 token 返回 401 + `1100`
- ✅ 过期 token 返回 401 + `1101`（与 `1100` 可区分——0.0.5 的静默刷新依赖这一区分）
- ✅ `dotnet test -c Release` 全绿（本迭代新增 **63** 个用例，全量 532）
- ✅ 手工：`ADMIN__USER=admin ADMIN__PASSWORD=StrongP@ss123 dotnet run` → 用 admin 登录成功

> ⚠️ **已知缺口（留给 0.0.15）**：反代后 `Connection.RemoteIpAddress` 是代理 IP，
> 而本项目尚未启用 `ForwardedHeaders`（需配合可信代理白名单）——
> 不加会将所有用户的登录失败算进同一个限流桶。

---

### 0.0.5 ✅ — 认证完善 + 前端基础设施

**目标**：完整认证闭环 + 前端可对接后端。

> **完成（2026-10-13）**：后端与前端全部落地。作为附带的稳定性修复，
> 集成测试宿主改用**非池化连接**（`Persistence:Pooling=false`）——原先清理时调用的
> `SqliteConnection.ClearAllPools()` 是进程级操作，会释放并行运行的其他测试正在用的连接句柄，
> 实测造成约 1/14 概率的偶发失败。

后端：
- ✅ `POST /api/v1/auth/refresh`：轮换刷新令牌（旧令牌立即作废，并用 `replaced_by_token_hash` 标记令牌族）
- ✅ `POST /api/v1/auth/logout`：撤销当前 refresh token（复用令牌表的 `revoked_at`，**不**另建黑名单表）
- ✅ 安全响应头中间件：`X-Content-Type-Options=nosniff`、`X-Frame-Options=DENY`、`Referrer-Policy=no-referrer`
  （用 `Response.OnStarting` 挂载——异常处理中间件的 `Response.Clear()` 会清掉直接赋值的响应头）
- ✅ 密码强度校验工具类（≥10 字符、含字母+数字或符号）——落地为 `PasswordPolicy`，登录与初始管理员引导共用

前端：
- ✅ `src/Docker.Manager.UI/` 工作区，Vite + Vue 3 + TypeScript + Element Plus 骨架
- ✅ `vite.config.ts` 配置 `/api` 与 `/hubs` 代理到 `http://localhost:8080`
- ✅ Pinia `useAuthStore`（user、accessToken、refreshToken 持久化到 localStorage）
- ✅ Axios 实例 + 拦截器：注入 Authorization、`401` 时静默刷新一次并重发原请求
  （**并发 401 只刷一次**——刷新令牌是一次性的，多刷会被服务端判定为重放）
- ✅ Vue Router 全局守卫：未登录跳 `/login`（另含 `requiresAdmin` 拦截与 hostId 同步）
- ✅ 登录页接 `POST /api/v1/auth/login`；失败文案按**错误码**映射 i18n（不透出后端中文 message）

**验收**：登录页表单提交 → 拿到 token 跳首页；token 过期自动刷新。✅

**轮换语义**（完整表见 [api-spec.md](./api-spec.md)）：已被轮换的旧刷新令牌再次使用 → 判定为重放 →
撤销该用户**全部**刷新令牌；登出后的令牌再次使用 → 仅拒绝，不牵连其他会话。

---

### 0.0.7 ✅ — 前端登录页与基础布局

**目标**：可视化的登录体验 + 全局导航骨架 + UnoCSS/主题/快捷键基建。

> **完成（2026-10-13）**：登录页已于 0.0.5 接通后端；本次把内联在 `AppLayout.vue` 里的
> 顶栏与侧边栏拆成独立组件（`layouts/`），并补上面包屑。快捷键属 0.0.14，不在本版范围。
>
> **界面打磨（2026-10-13 追加）**：本版的页面骨架已就位，但走查发现几处结构性问题，
> 已一并修掉，不另开版本号：
> - ✅ **语义颜色令牌收口**：`main.css` 增表面 / 文本 / 状态 / 层级四类令牌，
>   `uno.config.ts` 映射为 `surface-*` / `content-*` / `line-*` / `status-*`；
>   组件不再写 `gray-500`、`bg-white dark:bg-gray-800` 这类亮暗双写
>   （此前 `app-card` 写死的 `dark:bg-gray-800` 与早已存在的 `--surface-card` 就是漂移源头）
> - ✅ **缺失态重新设计**：主机落地页原先在数据缺失时整块不渲染、只剩四个裸「—」，
>   看起来像加载失败。现改为信息卡恒显 + 弱化占位 + 原因说明 + 离线提示条
> - ✅ **概览 / 状态两页职责切分**：此前逐字重复，现概览=身份与技术信息、状态=实时指标与事件
> - ✅ **主机侧边栏菜单顺序**调整为「状态 → 容器 → 镜像 → 数据卷 → 网络 → 编排 → 事件」，
>   与能力交付节奏对齐（编排依赖容器/镜像且排 0.4.0）
> - ✅ **占位页改为「功能预告」**（`PlaceholderSection`）：图标 + 版本徽章 + 能力清单 + 排期依据，
>   不再是「空白卡片 + 一句话」
> - ✅ 清理：删除重复的「详情」按钮（与「状态图」绑同一函数）、删除死代码 `HostStatusPanel.vue`、
>   侧栏主机身份卡改分组标题（主机名从 4 处收敛为 2 处）、分组描述由硬编码中文改为 i18n

前端（详细见 `docs/ui-guidelines.md`）：
- ✅ **UnoCSS 接入**：`unocss` + `presetUno` + `presetTypography` + `presetIcons`（Iconify 按需）
- ✅ **主题系统**：`composables/useTheme.ts`（亮色 / 暗色 / 跟随系统 + 蓝/绿/紫主题色）
- ✅ `LoginView.vue`：居中卡片 + Element Plus 表单 + 主题切换按钮（已接 `POST /api/v1/auth/login`，失败按错误码映射 i18n）
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
- ✅ **顶栏组件**：`AppBreadcrumb.vue`（层级由 `utils/breadcrumbs.ts` 纯函数派生，已被单测覆盖）、
  `UserMenu.vue`（用户名 / 角色标签 / 登出）。主题与语言仍是顶栏独立控件（切换频繁，
  藏进下拉多一次点击），修改密码随 0.0.13 落在同一个下拉里
- ✅ **侧边栏组件**：`AppSidebar.vue`（桌面侧栏 + 移动端抽屉，只负责容器与断点）
  + `SidebarNav.vue`（菜单内容，两处复用；按 `route.meta.showInSidebar` / `scope` / `requiresAdmin` 过滤）
- ✅ 国际化：vue-i18n，`locales/zh-CN.json` / `locales/en-US.json`；key 用 dotted 命名，缺失 fallback 英文
- ✅ 占位首页 `HostDashboardView.vue`（按主机作用域，作为 `/hosts/:hostId` 的落地页）
- ✅ 路由守卫实现未登录跳 `/login` 与 `requiresAdmin` 拦截

**验收**：登录页英文/中文切换正常 ✅；切换主题颜色变化 ✅；侧边栏可折叠且状态持久化 ✅（`stores/theme.ts` 的 `sidebarCollapsed`）；移动端（375px）侧边栏变抽屉可正常开关 ✅。

---

### 0.0.8 ✅ — Docker 主机抽象

**目标**：可注册多个 Docker 主机，按主机切换操作。

> **完成（2026-10-13）**：后端全部落地——主机/用户管理 API、当前主机解析、TLS/Basic 认证、
> 集成测试设施。主机管理界面已随 0.0.13 落地。

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
  - ✅ 由 Api 中间件从**路由值** `hostId` 解析当前主机（写入 Scoped `ICurrentHost`）；路径形如 `/api/v1/hosts/{hostId}/...`，**无缺省值**
    - ✅ 顺带完成存在性校验：未知主机在进控制器之前就变成 `1300`/404，主机作用域端点无需各自重复校验
    - ✅ 资源可见性校验 `IResourcePermissionService.IsHostVisible`（0.0.9 落地：admin 可见全部，其余 fail-closed 拿空列表）；逐资源授权表仍排 0.3.0
- ✅ `IDockerClientFactory`（无状态）：只用 `DockerClientBuilder` 创建客户端
  - ✅ socket：`WithEndpoint(new Uri("unix:///var/run/docker.sock"))`
  - ✅ TCP（无认证）：`WithEndpoint(new Uri("tcp://host:2375"))`
  - ✅ `Infrastructure` 项目额外引入 `Docker.DotNet.Enhanced.X509` + `Docker.DotNet.Enhanced.BasicAuth`
  - ✅ TCP + TLS：`https://` 端点（默认端口 2376）+ 客户端证书；三项路径均必填（缺 CA 等于接受任意服务端证书）
  - ✅ TCP + Basic：`BasicAuthCredentials(user, pass, tlsEnabled)`；明文端点不阻断但记 Warning
- ✅ 主机管理 API（admin 专用）：
  - ✅ `POST /api/v1/admin/hosts/list`（并行探活）/ `create` / `{hostId}/update` / `{hostId}/delete` / `test`
  - 新增错误码 `1303`（主机名已被占用）；更新时用**变更前**的连接描述让客户端池失效
- ✅ 主机概览 API（任意已认证用户）：
  - ✅ `POST /api/v1/hosts/list`（**当前用户可见**的主机 + 连通状态；非管理员在授权表落地前拿到空数组）
  - ✅ `POST /api/v1/hosts/{hostId}/overview`（版本 / 资源计数 / 启动时间；离线返回 `status=offline` 而不是 502）
  - 本迭代初版遗留了「可见主机列表」「主机版本信息」「主机启动时间」三项（features.md 标着属于 0.0.8 但无端点），**均已随 0.0.9 补齐**
- ✅ 用户管理 API（admin 专用）：
  - ✅ `POST /api/v1/admin/users/list`（分页）/ `create` / `delete` / `update-password`
  - 自助改密需验原密码，管理员重置他人不需要；改密码与删号都撤销该用户全部令牌
- ✅ `IResourcePermissionService` 接口定义（**实现**排 0.1.0，仅做 host 可见性）

测试（从本迭代起为强制要求）：
- ✅ `tests/Docker.Manager.IntegrationTests/`（**项目骨架已于 0.0.2 落地**）新增集成测试辅助设施：`DockerFactAttribute` 探测宿主 `/var/run/docker.sock`，不可达时跳过用例（**不引入 Testcontainers**，见 [constraints.md](./constraints.md#集成测试门槛)）
- ✅ 容器/镜像/Volume/Network 等新 API 必须配集成测试，CI 卡（本迭代起生效：主机 / 用户 API 均已配）
- ✅ 不设行覆盖率门槛

---

### 0.0.9 ✅ — 容器 API 后端

**目标**：所有容器 CRUD 操作可通过 API 完成。

后端：
- 容器 API：
  - ✅ **`POST /api/v1/hosts/{hostId}/containers/create`**（`CreateContainerParameters` **精选字段**：镜像 / 名称 / 命令 / 入口点 / 环境变量 / 标签 / 端口 / 挂载 / 重启策略 / 自动删除 / 网络 / 主机名 / 工作目录 / TTY / stdin）
  - ✅ `POST /api/v1/hosts/{hostId}/containers/list`（支持 `all` + 分页）——分页在服务端内存完成（Docker 无分页参数），`pageSize` 上限 100 且夹紧后的值回显
  - ✅ `POST /api/v1/hosts/{hostId}/containers/inspect`（精选投影：状态 / 启动配置 / 宿主配置 / 挂载 / 网络）
  - ✅ `POST /api/v1/hosts/{hostId}/containers/logs`（`tail` / `since` / `timestamps` / `includeStderr`；**不含** `follow`，实时流归 0.3.0）
  - ✅ `start` / `stop` / `restart` / `remove`（前三个幂等：守护进程的 `304` 按成功处理；`remove` 不带 `force` 删运行中容器返回 `1404`/409）
- ✅ 所有接口走 `IDockerHostAccessor` 获取客户端，按 host 粒度做可见性校验（`CurrentHostMiddleware`，0.0.8 落地）
- ✅ `Docker.DotNet.Enhanced` 异常统一映射（`DockerErrorTranslator`，0.0.8 落地；0.0.9 补 `409` → `1404`）：
  - `DockerApiException` → 通用 Docker 错误（code 1402）
  - `DockerContainerNotFoundException` → code 1401（资源不存在）
  - `DockerImageNotFoundException` → code 1401
  - `TaskCanceledException` → code 1403（操作超时）

**验收**：curl 完整跑通容器 create/list/start/stop/restart/remove/logs/inspect；非授权 host 返回 code 1501。
✅ 已达成：八个端点全部落地，并由集成测试覆盖（查询 11 条 + 变更 19 条，其中「生命周期」用例在真实守护进程上跑完 create → start → stop → remove → 1401）。

---

### 0.0.10 ✅ — 镜像 API 后端

**目标**：镜像 CRUD + 拉取（含进度回传）。

后端：
- 镜像 API：
  - ✅ `POST /api/v1/hosts/{hostId}/images/list`（`all` / `search` / 分页；分页在服务端内存完成，`pageSize` 上限 100 且夹紧后的值回显）
  - ✅ `POST /api/v1/hosts/{hostId}/images/pull`（`image` + `tag?` + `platform?`）
  - ✅ `POST /api/v1/hosts/{hostId}/images/remove`（`imageId` + `force?`；**不**传 `noPrune`）
  - ✅ `POST /api/v1/hosts/{hostId}/images/inspect`（精选投影）
- ✅ Pull 进度：使用 `IProgress<JSONMessage>` 在服务端聚合，**0.0.10 不接入 SignalR**，仅在响应 `data` 一次性返回最终结果；进度信息写到 Serilog 日志。**SignalR 实时进度推迟到 0.3.0**
- ✅ 拉取失败有两条路径：「镜像不存在」由 SDK 抛异常 → `1401`；「仓库拒绕 / 网络中断」只在进度流里 → 检查后转 `1402`

**验收**：pull `nginx:latest` 完成后返回镜像 ID 与大小。
✅ 已达成：四个端点均落地并由集成测试覆盖（含真实守护进程上的拉取成功路径与「删除只解绑标签、镜像本体保留」语义）。

---

### 0.0.11 ✅ — 前端：容器 UI

**目标**：可视化容器管理。

前端：
- ✅ API 封装层 `src/Docker.Manager.UI/src/api/container.ts`：RPC 风格方法封装（八个端点全覆盖）
- ✅ 容器状态语义映射 `src/utils/container-status.ts`：状态→色调（含独立的**过渡态**一档）、端口展示、字节格式化
- ✅ `src/Docker.Manager.UI/src/stores/host.ts`：Pinia 当前主机选择（**由 0.0.7 / 0.0.8 提前落地**）
- ✅ **顶栏 `HostSelector` 下拉组件**（`components/business/HostSelector.vue`）：主机作用域显示当前主机名 + 状态点，全局作用域显示「选择主机」占位；列出可访问主机，**切换后停留在当前资源页**；写入 Pinia `useHostStore` → 路由跳转到 `/hosts/{新hostId}/{当前资源页}`；admin 可见「管理主机」入口（**由 0.0.7 提前落地**）
- ✅ `ContainerListView`（实现为 `views/ContainersView.vue`）：表格（名称、镜像、状态、端口、创建时间）+ 操作按钮（启动/停止/重启/删除）+ 状态自动刷新（轮询 **5 秒**，页面隐藏时暂停，失败不打断轮询）
- ✅ **`CreateContainerDialog.vue`**：可视化创建表单（镜像 + 名称 + 端口映射 + 环境变量 + Volume 挂载 + 重启策略 + 折叠的高级选项）；行式文本输入配**提交前校验与行号定位**；创建成功后可直接「立即启动」
- ✅ `ContainerLogsView.vue`：行数选择 + tail 加载 + 时间戳 / stderr 开关（自动滚动不在本版范围：后端不支持 `follow`，实时流归 0.3.0）
- ✅ `ContainerInspectView.vue`：分组卡片展示（基本信息 / 宿主配置 / 启动配置 / 挂载 / 网络 / 环境变量 / 标签），**不是** JSON 树

**验收**：容器列表 → 创建 → 立即启动 → 详情 → 日志 → 停止 → 删除全链路走通；容器端集成测试与 UI 单测全绿。✅

---

### 0.0.12 ✅ — 前端：镜像 UI

**目标**：可视化镜像管理。

前端：
- ✅ `ImageListView.vue`（实现为 `views/ImagesView.vue`）：表格（仓库、标签、镜像 ID、创建时间、大小、被引用数）+ 删除按钮；搜索走**服务端**（后端按仓库名 / 标签内存过滤，前端不再过滤一遍）；**不轮询**（镜像没有秒级漂移，重建表格只会打断阅读）
- ✅ `ImagePullDialog.vue`（实现为 `components/business/PullImageDialog.vue`）：只留**一个**输入框，由 `splitImageReference()` 拆成仓库与标签——后端拒绝「已含标签又另传 tag」，分两个输入框会逼用户先理解后端的参数划分
- ✅ `ImageInspectView.vue`：结构化投影展示（概览 / 构建配置 / 声明端口与卷 / 标签），**不是**裸 JSON 树——后端 inspect 已经是精选投影，逐字段排版比再包一层的 JSON 树更好读；原始 JSON 仅作可折叠的附录
- ✅ 删除语义如实展示：被容器引用需强制删除（`1404`），且区分「只解绑了标签」与「真的删掉了镜像」

**验收**：拉取 → 列表出现新镜像；多标签镜像只解绑单个标签且提示「镜像本体保留」；悬挂镜像以 `<none>` 占位且可按 ID 删除；`vue-tsc` / `eslint` 零问题，单测全绿。✅

---

### 0.0.13 ✅ — 前端：管理 UI

**目标**：管理员可在界面上完成主机与用户管理。

前端：
- ✅ `UserManagementView.vue`（实现为 `views/UsersView.vue`）：用户列表 + 新建对话框 + 重置密码对话框 + 删除确认；自己的删除按钮**直接禁用**（后端也有同样护栏，但让用户点下去才报错是糟糕的交互）
- ✅ `HostManagementView.vue`（实现为 `views/HostsView.vue`）：主机列表（连通性**三态**：可连接 / 连不上 / 未探测）+ 创建 / 编辑对话框 + 删除确认 + 启用开关（停用需二次确认）
  - 测试连通性按钮放在**表单内**（测当前填写的那份配置）而不是列表行上：后端 `admin/hosts/test` 收的是**一份配置**而不是 `hostId`，而列表里没有凭据内容，拿不到凭据就测不出真结论
- ✅ **主机分组管理 UI**：分组的新建 / 重命名 / 删除 + 主机归组调整（后端 `host_groups` 表与 `hosts.group_id` 列随本版落地）；内置分组名取 i18n，自定义分组原样显示管理员输入
- ✅ `ChangePasswordDialog.vue`：任意用户可改自己密码（从顶栏用户菜单进入）；成功后**清本地会话并回登录页**——后端在改密时已撤销全部刷新令牌，留着已登录的样子会让用户被毫无征兆地踢出去
- ✅ 路由守卫增强：按 `route.meta.requiresAdmin` 隐藏菜单项 + 拦截直接访问（0.0.7 已就位，本版补上真实的管理页）

**验收**：创建用户并用其登录成功；创建远程主机 + 测试连通性给出结论（连不上是 `200 + false`，不是错误响应）；删除主机后其容器页不可达；改密后旧令牌失效；删除分组后组内主机回到未分组而不是被删掉。✅

---

### 0.0.14 ✅ — 前端：MVP 打磨

**目标**：补齐 0.1.0 的交互与非功能细节，准备进入 RC。

前端：
- ✅ **命令面板** `components/common/CommandPalette.vue`：Ctrl+K / Cmd+K 唤起；搜索所有路由 + 常用操作（跳 Containers / 跳 Images / 创建容器 / 拉取镜像）；Fuse.js 模糊匹配
  - 阈值放宽到 `0.35`：面板查询普遍很短（`img`、`镜`），Fuse 默认阈值下这类输入**一条都搜不出来**，空面板比给几个无关结果更让人困惑
  - fuse.js 走动态 import（26 kB / gzip 9.8 kB 独立 chunk），不为一个偶尔用的功能拖慢首屏
  - 额外加了 `g+b` 折叠侧栏与 `g+s` 跳主机状态（见 `ui-guidelines.md`）
- ✅ **全局快捷键** `composables/useShortcuts.ts`：`g+c` / `g+i` / `g+v` / `g+n` / `g+d` / `?` 跳转与帮助弹窗
  - 三条约束：输入框内一律不劫持；除 Ctrl+K 外不抢占任何带修饰键的组合；chord 超时 1.5s 且第二个键无论是什么都先清空待定状态
  - 帮助弹窗内容由**同一份 chord 注册表**生成，不会出现「加了键但帮助里没写」
- ✅ 通用组件库扩充：`EmptyState`（0.0.12 已有）、`StatusBadge`、`JsonTree`、`CopyableText`
  - `CopyableText` 带非安全上下文回退：http 部署时 `navigator.clipboard` 不可用，而自托管场景很常见
  - `JsonTree` 按类型着色叶子标量，替代原先只能全量 `<pre>` 的 inspect 展示
- ✅ 业务组件：`ContainerStatusBadge`、`ImageSize`、`PortMappingEditor`、`EnvVarEditor`、`VolumeMountEditor`
  - 三个编辑器都是「批量粘贴 + 逐行编辑」双模：用户手上的环境变量与端口几乎总是已经存在的，逐行填表格是纯粹的重复劳动
  - 解析逻辑抽到 `utils/container-input.ts` 并单测：判错时不抛异常，而是**悄悄建出一个与用户意图不同的容器**
  - 三个编辑器**已接进页面**（不是建完就搁置）：`ContainerStatusBadge` 用于容器列表与详情页，`ImageSize` 用于镜像列表与详情页（并区分独占层体积 / 逻辑总体积），`JsonTree` 替换镜像详情页的全量 `<pre>`
  - ⚠️ **撤掉了挂载类型选择器**：后端 `ContainerVolumeMapping` 根本没有 `type` 字段（bind 与具名卷由 Docker 按来源路径是否存在判定），界面加下拉只会得到一个「选了也不生效」的开关
  - 解析失败时编辑器只回传**出错行之前**的合法部分，因此 `errorLine` 必须经 `v-model:error-line` 回传父级禁用提交——否则会建出「少了一条环境变量却提示成功」的容器
仓库：
- ✅ `README.md` 完善：CI 徽章、功能截图占位、文档链接矩阵、`docker run` 命令、**Docker Socket 挂载安全风险说明 + 推荐 socket-proxy 模式**
  - 修正了若干**不实描述**：原 README 让用户 `docker compose up` 与拉取镜像，但 `Dockerfile` / `docker-compose.yml` 要到 0.0.15 才交付；且 `SECURITY.md` / `CONTRIBUTING.md` 从未存在。现在明确区分「当前可用」与「目标形态」
- ✅ `docs/*.md` 第一版完整内容；`ui-guidelines.md` 的快捷键表同步为实现结果

后端：
- ✅ 全量日志脱敏审计：认证链路（登录 / 刷新 / 登出 / 改密 / 管理员引导）确认干净；但发现一处 **P1**——`HostService` 在 Information 级别记录端点原文，而端点是凭据载体（`tcp://admin:s3cr3t@host` 会被接受并落盘）。已补 `HostEndpoint.Redact()` 作为第二道防线
- ✅ 性能基线：容器 list 100 个容器场景 **P50=12ms / P95=35ms**（阈值 500ms），起回环 HTTP 假守护进程而非 mock `IDockerClient`，顺带覆盖 Docker.DotNet 的 HTTP 往返与 JSON 反序列化

**验收**：完整走查全部 UI 流程，无 console error；切换语言、主题、主机不丢登录态；容器创建对话框可成功创建 nginx 容器并访问。✅

---

### 0.0.15 ⬜ — 部署底座与发布流水线

**目标**：`docker run` 一行启动可登录、可发布。

> **为什么排在 0.0.x 末位**（原编号 `0.0.6`）：
>
> 1. **Dockerfile 要打的正是前端产物**——builder 阶段要跑 `pnpm build`，而容器/镜像/管理三套界面到 0.0.14 才打磨完。在 `0.0.6` 做，打出来的镜像里只有占位页，等于交付一个「能启动但没用」的东西。
> 2. **compose / socket-proxy 的安全说明依赖真实能力清单**——「只授 `CONTAINERS`/`IMAGES`/`NETWORK`/`VOLUME` 子集」这句话，在还没搞清楚 0.1.0 到底会调用哪些 Docker API 之前是猜的。
> 3. **Release Please 与 CHANGELOG 要读得懂提交历史**——在只有二十来个提交时启用，规则改动的返工成本远高于稳定后一次做对。
> 4. **初始管理员引导不等它**——那件事有鸡生蛋问题（要先能登录才能建用户），已于 0.0.4 落地，本版只剩部署与发布。

后端：
- （**已随 0.0.4 落地**，此处保留记录）启动时检测 `users` 表是否为空：若空，按环境变量 `ADMIN__USER` + `ADMIN__PASSWORD` 创建 admin（密码强度校验）
  - 判空用**物理行数**（裸 `SELECT COUNT(1)`）而非仓储计数——软删除会让业务计数为 0，从而导致重复引导并撞用户名唯一索引
  - 密码强度不满足 → **启动失败**；凭据未配置 → 只记 Warning 并正常启动
- 健康检查端点 `GET /healthz` 增强：返回 SQLite 连通性
- Dockerfile 多阶段构建（builder 阶段：Node + .NET SDK；runtime 阶段：仅 .NET runtime + dist）
- 非 root 用户 `app`（UID 10001）运行，根文件系统只读，data 卷可写
- `/healthz` 健康检查命令
- **SQLite 启用 WAL 模式**（PRAGMA journal_mode=WAL），提高并发读性能
- 反代场景的 `ForwardedHeaders` + 可信代理白名单（**0.0.4 遗留**：否则所有用户的登录失败会被算进同一个限流桶）

仓库：
- **`docker-compose.yml` 两种部署模式**：
  - 直挂 socket：`/var/run/docker.sock:/var/run/docker.sock`
  - **socket-proxy 模式**（推荐）：`tecnativa/docker-socket-proxy` 作为中介，仅授予 `CONTAINERS` / `IMAGES` / `NETWORK` / `VOLUME` 子集权限
- `scripts/backup.sh`：基于 `sqlite3 .backup` 的 SQLite 备份脚本；README 文档化 cron 示例
- `.github/release-please-config.json`：启用 Release Please（读 Conventional Commits 自动 PR + 自动 tag）
- `CHANGELOG.md` / `CONTRIBUTING.md` / `SECURITY.md` / `CODE_OF_CONDUCT.md` 出初始版本
- `.dockerignore` 扩充
- `dotnet format` 纳入 CI 门槛（**0.0.2 遗留**：当时只统一了行尾与空行，格式检查留到有部署流水线的这一步）

CI：
- `.github/workflows/ci.yml` 增强：build 镜像 + sanity test
- `.github/workflows/release.yml`：仅 `v*.*.*` tag 触发构建 + 推 GHCR

**验收**：`docker run -e ADMIN__USER=admin -e ADMIN__PASSWORD=StrongP@ss123 ...` → 看到登录页 → 用 admin 登录成功 → 控制台/日志无密码明文；`bash /app/scripts/backup.sh` 产生备份文件；镜像进入 GHCR 且 `v0.0.*` tag **不**推送。

---

### 0.1.0-rc.1 ⬜ — MVP 候选发布

**目标**：0.0.15 后冻结功能，仅修 bug。

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
