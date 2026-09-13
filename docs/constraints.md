# 技术约束

> 本文档定义项目所采用的**技术栈版本基线**、**安全约束**、**CI/CD 约束**、**部署约束**与**边界条件**。
> 版本号策略严格遵循 SemVer，详细路线图见 [roadmap.md](./roadmap.md)。

---

## 技术栈选型

### 后端

| 组件 | 选型 | 版本约束 | 说明 |
|------|------|---------|------|
| 运行时 | .NET | **10 LTS**（支持到 2028-11） | C# 14 |
| 架构风格 | **DDD 分层** | 4 个后端项目 | `Domain` / `Application` / `Infrastructure` / `Api`，详细见 [architecture.md](./architecture.md#ddd-依赖方向) 与 [ddd-guide.md](./ddd-guide.md) |
| Web 框架 | ASP.NET Core | 10 | WebAPI + Minimal API + SignalR Hub |
| Docker 交互 | [`Docker.DotNet.Enhanced`](https://github.com/testcontainers/Docker.DotNet) | 最新稳定 | fork 自 `dotnet/Docker.DotNet`；Docker API ≥29.8.0；AOT 兼容 |
| Docker TLS 认证 | `Docker.DotNet.Enhanced.X509` | 最新稳定 | 远程 TCP 可选 TLS |
| Docker Basic 认证 | `Docker.DotNet.Enhanced.BasicAuth` | 最新稳定 | 远程 TCP 可选 Basic |
| 数据访问 | Dapper | 最新稳定 | 手写 SQL，不引 ORM |
| 数据库 | SQLite + `Microsoft.Data.Sqlite` | 跟随 .NET | 单节点嵌入式，**WAL 模式**启用 |
| 密码哈希 | `Isopoh.Cryptography.Argon2` | 最新稳定 | Argon2id |
| JWT | `Microsoft.AspNetCore.Authentication.JwtBearer` | 跟随 .NET | 内置 |
| 实时通信 | ASP.NET Core SignalR | 10 | 原生集成 |
| SignalR 客户端 | `@microsoft/signalr` | **9+** | 跟随 .NET 主版本 |
| 日志 | Serilog | 最新稳定 | 控制台 + 滚动文件 |
| OpenAPI（1.0.0） | `Microsoft.AspNetCore.OpenApi` + **Scalar** | 最新稳定 | 替代 Swagger UI |

> **SDK 选型说明**：选用 `Docker.DotNet.Enhanced` 是因为上游 `Docker.DotNet` 3.x 已停摆多年，而 fork 同步 Docker API 29.8.0 且持续维护。`Core` 层通过 `IDockerClientFactory` / `IDockerHostAccessor` 抽象隔离 SDK，未来可平滑回退到上游 3.x。详见 [architecture.md](./architecture.md)。

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
| 终端组件 | xterm.js | **5.5+** | 容器 exec 终端（0.4.0） |
| 国际化 | vue-i18n | **11** | Vue 3.5 适配；中文 / 英文双语 |
| 包管理 | pnpm | 最新稳定 | 比 npm/yarn 快、磁盘省 |

### 配套工具

| 组件 | 选型 | 说明 |
|------|------|------|
| NuGet 包管理 | Central Package Management（`Directory.Packages.props`） | .NET 10 原生支持；所有 NuGet 版本集中管理 |
| 代码格式化（后端） | `dotnet format` | CI 中执行 |
| 代码格式化（前端） | Prettier + ESLint | CI 中执行 |
| 测试（后端） | xUnit + FluentAssertions + Testcontainers（基于 Docker.DotNet.Enhanced 做集成测试） | 单元 + 集成 |
| 测试（前端） | Vitest + Vue Test Utils | 组件测试 |

### DDD 约定（0.0.1 起强制）

| 项 | 约定 |
|----|------|
| 聚合根命名 | 名词单数，如 `DockerHost`、`Container`、`Image`、`User`。继承 `Docker.Manager.Domain.Common.AggregateRoot<TId>` |
| 实体命名 | 名词单数，继承 `Docker.Manager.Domain.Common.Entity<TId>`。仅在不属于聚合根时使用 |
| 值对象命名 | 名词，如 `HostEndpoint`、`ContainerStatus`、`PortMapping`。继承 `Docker.Manager.Domain.Common.ValueObject`，**必须不可变**（init-only 或 readonly） |
| 仓储接口 | `IXxxRepository`，定义在 `Domain/Repositories/`，实现在 `Infrastructure/Persistence/`。**不允许反向依赖**（Domain 不得引用 Dapper / SQLite 等） |
| 外部资源接口 | `IXxxFactory` / `IXxxAccessor` 等，定义在 `Application/Abstractions/`，实现在 `Infrastructure/`。典型：`IDockerClientFactory`、`IDockerHostAccessor` |
| 领域事件 | 过去时态命名（`ContainerStarted`、`HostRegistered`），实现 `IDomainEvent`。仅聚合根可产生 |
| 跨聚合修改 | 通过领域事件订阅实现最终一致性；同聚合内调用直接走方法引用 |
| 业务异常 | 领域不变量的违反用 `DomainException`；应用层用例执行失败用 `BusinessException`（`Application/Common/`） |
| 反模式 | 贫血模型、Entity 直接返回 DTO、聚合根跨边界互相引用、Domain 引用 NuGet 包（仅 BCL） |

详细示例与反模式清单见 [ddd-guide.md](./ddd-guide.md)。

---

## 安全约束

### 鉴权与令牌

| 项 | 策略 |
|----|------|
| 密码哈希 | **Argon2id**（`Isopoh.Cryptography.Argon2`），禁止明文落库与日志 |
| 密码强度 | ≥10 字符，含字母 + 数字或符号 |
| JWT 算法 | HS256 |
| JWT access token 有效期 | **15 分钟** |
| JWT refresh token 有效期 | **7 天**，写入数据库可撤销表 |
| 初始管理员引导 | 环境变量 `ADMIN__USER` + `ADMIN__PASSWORD`（启动时若 `users` 表为空则创建） |
| JWT Secret 来源 | 仅环境变量 `JWT__Secret`；**缺失或过弱（<32 字节）则启动失败** |
| 0.4.0 终端短时 token | `POST /api/v1/auth/terminal-token` 返回 5 分钟过期 token，**仅能用于 WebSocket `/ws/container/exec`** |

### 速率限制

| 接口 | 限制 |
|------|------|
| `POST /api/v1/auth/login` | 按 IP + 用户名，**5 次/分钟 → 429** |

### HTTP 安全响应头（0.0.5 起强制）

| Header | 值 |
|--------|----|
| X-Content-Type-Options | `nosniff` |
| X-Frame-Options | `DENY` |
| Referrer-Policy | `no-referrer` |

### CORS

- **生产环境**：默认同源，**禁用 CORS**（一体化镜像托管前端，Nginx 风格反代或 ASP.NET Core 静态文件中间件）
- **开发环境**：仅通过 Vite 代理（`/api` → `http://localhost:8080`，`/hubs` → 同上），不直接开放跨域

### Docker Socket 挂载（⚠️ 高危）

> **Docker.Manager 容器挂载 `/var/run/docker.sock` 等于具备宿主机 Docker 的 root 权限**，单容器漏洞 = 宿主机沦陷。

**推荐部署模式（socket-proxy）**：

```yaml
# docker-compose.yml 推荐写法
services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
      POST: 0       # 禁用 POST（禁止 exec/创建容器）—— 仅在只读模式下开启
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro

  docker-manager:
    image: ghcr.io/blue-cloud-net/docker-manager:latest
    depends_on: [socket-proxy]
    environment:
      - DEFAULT_HOST__ENDPOINT=tcp://socket-proxy:2375
    volumes:
      - ./data:/app/data
```

socket-proxy 默认仅授予 `GET`（只读 + 列出），如需写操作（创建容器、启动/停止等）按需开启对应子集。

详细的安全声明与部署模式见 `README.md` 与 `SECURITY.md`（仓库根）。

### 日志脱敏（0.0.13 起强制）

- 启动时执行 grep 审计：确认无明文密码、access token、refresh token 写入日志
- Serilog 配置 `.MinimumLevel.Information()` + `.Filter.ByExcluding(...)` 过滤敏感字段
- 禁止在结构化日志的 `MessageTemplate` 中直接拼接密码/token

---

## CI/CD 约束

### Git 工作流

| 项 | 策略 |
|----|------|
| 主分支 | `main`（仅稳定发布）+ `develop`（集成分支） |
| 分支保护 | 1 review + CI 通过 |
| 合并策略 | Squash merge |
| PR 标题 | 走 Conventional Commits（squash merge 后成为主 commit） |
| DCO | **不要求** Signed-off-by |

### Conventional Commits 严格规范

- 允许 `feat!:`、`fix!:` 等破坏性变更前缀
- 推荐使用 scope（如 `feat(container):`、`fix(auth):`）
- Type 限定：`feat` / `fix` / `docs` / `style` / `refactor` / `perf` / `test` / `build` / `ci` / `chore`

### Release Please（0.0.6 启用）

- 读 Conventional Commits 自动生成 release notes
- 自动开 PR 修改 `CHANGELOG.md`
- 自动打 tag
- 状态文件：`.github/.release-please-manifest.json`

### 镜像发布（0.0.6 起）

| 项 | 策略 |
|----|------|
| 镜像仓库 | `ghcr.io/blue-cloud-net/docker-manager` |
| 标签策略 | **仅 semver**：`v0.1.0` / `0.1.0` / `0.1` / `latest` |
| 触发规则 | `v0.0.*` tag 仅构建做 sanity test，**不推 GHCR**；`v0.1.0-rc.*` / `v0.x.0` / `v1.0.0` tag 推 GHCR + 创建 Release |

### 集成测试门槛

- **0.0.8 起强制**：每个新 API 必须配 Testcontainers 集成测试，CI 卡
- **不设行覆盖率门槛**（避免过度追求数字）

---

## API 约定

- 风格：**RPC 风格**（非 RESTful）
- 路径前缀：`/api/v1/`
- 示例：`POST /api/v1/container/start`，而非 `POST /api/v1/containers/{id}`
- 完整错误码分段见 [api-spec.md](./api-spec.md)

---

## 部署约束

### Docker 容器部署

- 镜像内监听 **`:8080` HTTP**（不内置 HTTPS）
- **生产环境必须前置 HTTPS 反代**（Caddy / Traefik / nginx），详细文档化在 `README.md`
- 必须挂载 `/var/run/docker.sock` 或使用 socket-proxy
- 数据卷 `./data:/app/data`（SQLite 数据库与日志）

### 独立进程部署

```
dotnet Docker.Manager.Api.dll --urls http://0.0.0.0:8080
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
| 文件路径 | `/app/data/docker-manager.db` |
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

- 实时推送（SignalR），**不持久化历史指标**（v1.0 范围）

### 镜像构建

- 0.1.0 仅支持从 Docker Hub pull
- 镜像构建（Dockerfile 上传）→ 0.2.0
- 私有 Registry login → 0.4.0

### 容器终端

- 0.4.0 仅 WebSocket 长连接交互式终端（xterm.js）
- **不做一次性 exec API**（`POST /api/v1/container/exec`）

### 容器 stats

- 0.3.0 仅 SignalR 实时流
- **不做一次性 stats API**（`POST /api/v1/container/stats` 单次查询）

### Prune / 清理

- **v1.0 不做**（运维刚需但推迟）
- 不在 0.x 范围

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
