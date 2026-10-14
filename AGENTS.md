# AGENTS.md

> 面向 AI 编码代理的仓库指南。**只写「跑一遍代码库也发现不了」的内容**——命令、依赖铁律、
> 同步点、高频陷阱。约定详情一律链接 `docs/`，不要把 `docs/` 的内容抄进来。

## 项目定位

类 Portainer 的轻量 Docker 管理平台。**当前处于 0.0.x 开发迭代期**：未正式发布、API 可能破坏性变更、
不向 GHCR 发布 `0.0.*` tag。仓库**没有** `CONTRIBUTING.md` / `SECURITY.md`（0.0.15 才交付），
README 里的链接暂时指向不存在的文件属正常。

| 项目 | 位置 | 职责 |
|------|------|------|
| `Whalefleet.Core` | `src/` | 业务实体、值对象、枚举、仓储接口。**仅 BCL，无任何 NuGet** |
| `Whalefleet.Application` | `src/` | 用例编排、DTO、外部资源接口抽象 |
| `Whalefleet.Infrastructure` | `src/` | Docker SDK 适配、SQLite 仓储实现、安全组件 |
| `Whalefleet.Api` | `src/` | ASP.NET Core 10 宿主：Controllers、中间件、Serilog、DI |
| `Whalefleet.UI` | `src/` | Vue 3 + TS + UnoCSS + Element Plus 单工程 |
| `Whalefleet.UnitTests` | `tests/` | xUnit 单测。**不得引用 Api / Infrastructure** |
| `Whalefleet.IntegrationTests` | `tests/` | `WebApplicationFactory` + 真实 Docker socket。唯一允许 `Mvc.Testing` |

---

## 构建 / 测试 / 校验

后端（仓库根）：

````bash
dotnet build -c Release --nologo          # 与 CI 一致的构建
dotnet build -c Release -nologo -t:Rebuild # 查全量警告（见下方铁律）
dotnet test  -c Release                    # 一次跑完单测 + 集成测试
````

前端（`src/Whalefleet.UI/`，用 pnpm）：

````bash
pnpm install
pnpm typecheck    # vue-tsc --noEmit
pnpm lint         # eslint . --max-warnings 0
pnpm test         # vitest run
pnpm build        # typecheck + 生产构建
````

### 零警告铁律

`Directory.Build.props` 设了 `TreatWarningsAsErrors=true`，**警告即错误**。
但 `CodeAnalysisTreatWarningsAsErrors=false`，所以 `CA*` 分析器警告不致命——**仍然应清零**。

> ⚠️ **增量构建只报被重编译文件的警告**。改了 `Api` 再构建，看起来「只有 CA1014」，
> 但 `Application` / 测试项目里的 CA1859 / CA1307 / CA1861 只有全量 `-t:Rebuild` 才暴露。
> **不要据增量构建的结果断定自己没引入警告。**

> ⚠️ **前端改样式必须 `pnpm build` 后验产物**。UnoCSS 主题变量的错误是**编译期静默失败**
> ——`--brand-*` 写成十六进制，所有 `brand-*` 类全部消失且无任何报错。只看代码或 dev server 不足以确认。

---

## 架构与依赖铁律

分层依赖方向（箭头 = 引用方向）：`Core` ← `Application` ← `Infrastructure` ← `Api`，
`Core` ← `Infrastructure`，`Core` / `Application` ← 两个测试项目。

验证手段：

````bash
# Core 应无任何 NuGet 依赖（输出为空）
dotnet list src/Whalefleet.Core/Whalefleet.Core.csproj package --include-transitive
````

### 充血模型（非 DDD）

DDD 战术模式（聚合根 / 领域事件 / 领域服务）**已整体移除**。

- 实体的行为与不变量写在**实体自身的方法**里，推荐「私有构造 + 静态工厂」
- 不变量违反 → 抛 BCL **`ArgumentException`**（中间件映射 `1000` / 400）
- 用例执行失败（外部资源不可用、规则不允许）→ 抛 **`BusinessException`**（`Application/Common/`，自带错误码）
- **Entity 不得直接作为 HTTP 响应返回**，在应用服务里做 Entity → DTO 映射
- 枚举**仅用于封闭集合**；可运行时扩展的概念（如角色）必须建表 + 实体

### 接口归属判据

| 类型 | 归属 | 判据 |
|------|------|------|
| 业务概念（"管理 X"） | `Core` | 去掉整个外部世界后概念仍成立 |
| 外部资源抽象（"操作外部系统"） | `Application/Abstractions` | 抽象的是某种外部系统 |
| 第三方 SDK 适配 | `Infrastructure` | 包装 / 适配某个具体 SDK，可直接替换 |

**推论**：外部资源接口的**入参 / 返回类型**同样受约束。`DockerHostType`、`DockerHostConnection`
这类类型放 `Application/Abstractions` 而非 `Infrastructure`，否则 `Application` 就要反向引用
`Infrastructure`，依赖方向当场破裂。

### 反模式（见到就改）

| 反模式 | 表现 |
|--------|------|
| 贫血模型 | 实体只有属性，逻辑全在 Service |
| Entity 直接返回 DTO | Controller 直接拿 Entity 序列化 |
| `Core` 引用 NuGet | `Core.csproj` 含 `PackageReference` |
| `Application` 引用 `Infrastructure` | 应用服务直接 `new SqliteConnection()` |
| 审计属性开 `public set` | 绕过 `MarkCreated` / `MarkModified` |
| 为 Dapper 另建 Row DTO | 实为多余，见下方陷阱清单 |

---

## 关键约定（易踩）

### 新增错误码要同步 5 处

漏一处**守卫测试必挂**。同步点：

1. `Application/Common/ErrorCodes.cs` — 常量
2. `Application/Common/ErrorCodeCatalog.cs` — 分类（`ErrorCategory`）+ 默认文案
3. `Api/Errors/ErrorHttpStatus.cs` — 语义分类 → HTTP 状态码
4. 守卫测试：`UnitTests/Common/ErrorCodesTests.cs` + `IntegrationTests/Errors/ErrorHttpStatusTests.cs`
5. [docs/api-spec.md](docs/api-spec.md) — 错误码分段表

未登记的错误码**兜底为 `Internal` / 500**，**不按数值分段推断**。

### 其他后端约定

- **响应**：`ApiResult`（无 data）/ `ApiResult<T>`（含 data）+ `ApiResults` 静态工厂。
  工厂类**不能是泛型静态类**（触发 `CA1000`）。失败响应必须显式含 `"data": null`。
- **中间件管道顺序勿动**：`ExceptionHandlingMiddleware` → `UseSerilogRequestLogging`
  → `UseStatusCodePages` → `UseCors` → endpoints
- `throw;` **只能写在 catch 块内**。中间件用 `HandleExceptionAsync` 返回 bool，重抛留在 catch
- **API 是 RPC 风格**：`POST /api/v1/hosts/{hostId}/containers/start`，
  而非 `PUT /api/v1/hosts/{hostId}/containers/{id}`。`hostId` **只出现在路径中**，任何请求体都不得含它
- **`hostId` 就是 `DockerHost.Name`**（不区分大小写），**不是** `hosts.id`（那个 `Guid` 不进 URL）
- **配置优先级**：`appsettings.json` → `appsettings.{Env}.json` → 环境变量 → 命令行。
  层级用 `__`，数组用数字下标，键比较不区分大小写。
  ⚠️ `appsettings.json` 的 `Urls` **优先于** `ASPNETCORE_URLS`——改端口用 `--urls` 或 `URLS`
- **CORS 策略必须经 `IConfigureOptions<CorsOptions>` 延迟注册**；DI 注册期读配置会读到空值，
  静默不注册任何策略
- **每个新 API 端点必须配集成测试**，CI 卡

### 前端约定

- `types/api.ts` 必须与后端投影**逐字对应**；`ErrorCode` 常量表也在这里，
  与 `mocks/envelope.ts` 一起构成错误码的后端 5 处之外的**前端同步点**
- 纯逻辑放 `src/utils/*.ts` 并**返回 i18n key 而非文案**（可脱离组件单测）
- **禁止在模板里拼 i18n key**（如 `` t(`container.restartPolicy.${v}`) ``）——
  Docker 给的是 `unless-stopped`，key 是 `unlessStopped`，拼出来会显示原始 key 路径，
  且 vue-i18n 只告警不报错。改用显式 `Record` 映射 + 回退原值
- `el-option` **不接受 `null` 作为 `:value`**（控制台刷 type check 失败）。用空串表达「未分组 / 无」，
  提交时再转 `null`
- mock 必须**复刻易忽略的语义**（pageSize 夹紧、409/1404、tail 越界、创建后能接着 start），
  否则前端分支测不到

---

## 常见陷阱（docs 未记载，但极易踩）

| # | 陷阱 | 后果 / 正确做法 |
|---|------|----------------|
| 1 | **Dapper 需显式开 `MatchNamesWithUnderscores`** | 否则 `creation_time` 静默丢值 |
| 2 | **Dapper 参数绑定绕过 `ITypeHandler`** | 值类型直接装箱，只有物化路径生效。Guid 写入必须 `ToStorageText()` 转小写 36 位；枚举必须转小写文本 |
| 3 | **集成测试宿主必须 `Persistence:Pooling=false`**（4 个测试宿主全设） | `ClearAllPools()` 是进程级操作，会释放并行测试正在用的句柄（约 1/14 概率偶发 `ObjectDisposedException`）→ **禁止在测试里调 `ClearAllPools()`** |
| 4 | **迁移版本须从 1 连续** | `PRAGMA user_version` 跳号则启动失败 |
| 5 | **默认主机种子不能写进迁移** | 迁移先于种子执行，那时 `hosts` 表还是空的。归属在 `DatabaseInitializer.SeedDefaultHostAsync` |
| 6 | **仓储「未命中」返回 `false`，不抛异常** | 错误码由应用服务决定 |
| 7 | **`IHasModificationTime` 等审计接口全部只读** | 开 `set` 即绕过不变量 |
| 8 | **仓库统一 LF**（`.editorconfig` + `.gitattributes` 的 `* text=auto`）；`[*.sh]` 显式 LF | shell 脚本含 CR 会在容器里以 `\r: command not found` 失败 |
| 9 | **El 浮层在暗色下变白底** | `main.ts` 必须**显式 import** `element-plus/theme-chalk/dark/css-vars.css`（EP 暗色是独立可选包），放在 `element-plus/dist/index.css` 之后、`main.css` 之前 |
| 10 | **UnoCSS 必须 `darkMode: 'class'`** | 项目主题开关由 `<html class="dark">` 驱动；默认 `prefers-color-scheme` 会让「手动选亮色但系统是暗色」无法保持 |
| 11 | **`--brand-50..900` 必须写成空格分隔的 RGB 通道**（如 `--brand-500: 64 158 255`） | `uno.config.ts` 映射为 `rgb(var(--brand-N) / <alpha-value>)`；写十六进制 → `var()` 非法 → **所有 brand 类静默失效** |
| 12 | **路由参数不要自己 `encodeURIComponent`** | vue-router 会再编码一次（`sha256:` → `sha256%253A`），详情页拿到的 id 永远对不上。传原始值，`route.params` 读回来已解码 |
| 13 | **MSW 路径正则结尾别多写 `/`** | `/([^/]+)/update$/` 永远匹配不上（要求路径结束后还有斜杠），表现为端点 404 |
| 14 | **模板里用到但从 script 删掉的 import**，Vue 只在渲染时报 `is not a function` | **整块内容空白且 v-loading 卡住**，看不出是编译问题。改完 import 必须真跑一次页面 |

| 15 | **`.NET 8+ 的 aspnet 基础镜像自带 `app` 用户，UID 是 1654** | `groupadd --gid 10001 app` 会以 `group 'app' already exists` 失败，把镜像构建卡死。改用 `groupmod --gid` / `usermod --uid` |
| 16 | **aspnet 运行时镜像不含 `curl` 与 `sqlite3`** | 健康检查会退化成「进程还在」（挡不住「进程活着但 SQLite 打不开」）；README 让人在容器内跑备份脚本也会失败。两者都要在 runtime 阶段显式装 |
| 17 | **`MapFallbackToFile` 会吞掉打错的 `/api` 路径** | 症状是「接口返回 200 + HTML、日志里全是成功」，前端表现为 JSON 解析失败，几乎不可能靠读日志定位。SPA 回退必须显式排除 `/api` 与 `/healthz` |
| 18 | **SPA 回退必须挂在 `UseCurrentHost` 之前** | 前端 `/hosts/{hostId}/...` 与接口 `/api/v1/hosts/{hostId}/...` 同前缀，而 `UseCurrentHost` 会把任何请求的 `hostId` 路由值当主机查库。放在它之后，每次刷新深链都先抛「主机不存在」 |
| 19 | **`AddHealthChecks().AddCheck<T>(name)` 里的 `name` 不能省** | 直接注册 `IHealthCheck` 会以**实现类全名**作为注册项名字，而 `HealthCheckService` 按名字索引结果——结果是 `/healthz` 永远查不到该项，只能回退成 unhealthy |
| 20 | **`apiFactory.WithSetting` 是就地修改（返回 `this`）** | 打在类共享的 `IClassFixture` 上会把配置留给本类**其他**用例，表现为「随机顺序下某条用例莫名失败」。需要坏配置时必须 `new ApiFactory()` |
| 21 | **compose 的两种部署模式必须用 `profiles` 隔离，不能靠注释** | 两个 service 若共用 `container_name`，同时启动会直接冲突；而注释掉一个会让「另一种模式」在文件里彻底消失 |
| 22 | **`sqlite3 .backup` 而非 `cp`** | 直接拷数据库文件拷到的是某一瞬时状态，正在写事务时可能拷出撕裂的文件，恢复时才报 `database disk image is malformed` |

> **Dapper 物化能力（已核实，勿再沿用旧判断）**：Dapper **可以**物化「非公开构造 + 非公开 setter」
> 的充血实体，用 `skipVisibility` 动态方法。**不需要** Row DTO，**不需要** `InternalsVisibleTo`。
> 实体给一个非公开无参构造即可。IVT 仅在 Infrastructure 需以强类型代码直接调非公开成员时才要。

---

## 提交规范

- **Conventional Commits，正文用中文**。Type 白名单：
  `feat` / `fix` / `docs` / `style` / `refactor` / `perf` / `test` / `build` / `ci` / `chore`
- scope 用**小写模块名**：`core` / `infra` / `api` / `application` / `ui` / `build` / `sln` / `scaffold`
- 破坏性变更用 `feat!:` / `fix!:`
- 分支：`main`（仅稳定发布）+ `dev`（集成分支）；合并策略 **Squash merge**；
  **PR 标题**走 Conventional Commits
- 详细规范见 [docs/constraints.md](docs/constraints.md)

---

## 文档语言约定

| 文件 | 语言 |
|------|------|
| `README.md` | **英文**（GitHub 默认展示） |
| `README.zh-CN.md` | 中文，与上表**逐节对应** |
| `CHANGELOG.md` | **英文**；0.0.15 起由 Release Please 自动生成 |
| `CHANGELOG.zh-CN.md` | 中文镜像，**人工维护** |
| `docs/*.md`、`AGENTS.md` | 仅中文 |
| 提交信息 | 中文 |

> ⚠️ **改一侧必须同步另一侧。** README 两版的徽章块、章节数、链接集合应一一对应；
> CHANGELOG 两版的版本号序列与条目数应完全相同。
>
> ⚠️ README 里的 `docs/*.md#中文锚点` 链接**保持中文锚点原样**——`docs/` 只有中文版。

---

## 开发环境备注

- **前端 dev server 已在 30123 端口运行**（用户自己在别的终端跑着 `pnpm dev`，端口来自
  `vite.config.ts` 的 `server.port`）。走查浏览器直接用 `http://localhost:30123`，
  **不要重复启动**——Vite 会退到 30124 造成两个实例、抢 CPU 且易掉线。
  仅当确认 30123 确实没在跑时才允许启动。
  ⚠️ **5173 是本机另一个项目的 dev server**（food-log），打开它会看到完全无关的页面。
- **本机 Docker socket 可用**，`DockerFactAttribute` / `DockerImageFactAttribute` /
  `DockerRegistryFactAttribute` 的用例会**真的跑**，不是永远 Skip。
- 本机配了 registry 镜像加速器，`DockerRegistryFactAttribute` 必须**先探
  `/etc/docker/daemon.json` 的 `registry-mirrors`**，只探 `registry-1.docker.io` 会让国内 / 内网机器
  永远跳过 pull 成功用例。

---

## 文档索引

| 文档 | 何时读 |
|------|--------|
| [docs/overview.md](docs/overview.md) | 项目定位、非目标、发布模型与版本号策略 |
| [docs/constraints.md](docs/constraints.md) | 技术栈版本基线、安全 / CI-CD 约束、**Conventional Commits 规范**、配置系统、API 约定、集成测试门槛 |
| [docs/architecture.md](docs/architecture.md) | 分层依赖方向、充血模型约定、实体清单、**反模式清单**、Dapper 物化约定、鉴权架构 |
| [docs/api-spec.md](docs/api-spec.md) | RPC 风格 API 约定、**错误码分段表**、SignalR Hub、WebSocket 终端 |
| [docs/features.md](docs/features.md) | 按模块分组的完整功能列表（按版本标注） |
| [docs/roadmap.md](docs/roadmap.md) | 版本迭代与进度（**版本编号即执行顺序**；`0.0.6` 是空号） |
| [docs/ui-guidelines.md](docs/ui-guidelines.md) | UI 布局、断点、组件分层、快捷键、主题、路由 |
| [src/Whalefleet.UI/README.md](src/Whalefleet.UI/README.md) | 前端快速开始、**Mock 模式**（`?mock=on`，顶栏有 MOCK 角标） |
| [CHANGELOG.md](CHANGELOG.md) | 已交付内容（0.0.1 – 0.0.14） |

其他规范来源（**不要抄进代码注释或 PR**）：

- `.editorconfig` — C# 风格与命名规则（interface 以 `I` 开头、类型 PascalCase、私有字段 camelCase、
  括号规则已放宽）
- `Directory.Build.props` — 目标框架、`TreatWarningsAsErrors`、`AnalysisLevel=latest-recommended`、
  `NoWarn` 列表、Central Package Management
- `.github/workflows/ci.yml` — **唯一的 CI 门槛**（restore → build Release → test Release）
