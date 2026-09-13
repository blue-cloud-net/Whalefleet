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

## 阶段 0：开发迭代期（0.0.x）

> 每个 0.0.x 迭代 = 1 个可独立验收的小型里程碑。允许破坏性变更，仅自测，不对外发布。

### 0.0.1 — 项目骨架 + CI 骨架

**目标**：可构建、可运行最小后端 + CI 流水线雏形。

后端：
- 三项目解决方案：`src/Docker.Manager.Api/`、`src/Docker.Manager.Core/`、`src/Docker.Manager.Infrastructure/`
- `Directory.Packages.props` 启用 **Central Package Management**（`<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`），所有 NuGet 包版本号集中此文件
- `Infrastructure` 项目引入 `Docker.DotNet.Enhanced` 包（采用最新稳定版），在 `Core` 中定义 `IDockerClientFactory` 接口抽象
- `Docker.Manager.Api` 启动监听 `:8080`，返回 `GET /healthz` → 200 OK
- `Directory.Build.props`：TargetFramework=net10.0、LangVersion=latest、可空引用类型开启、警告即错误
- Serilog 控制台输出

CI：
- `.github/workflows/ci.yml`：push 触发，`dotnet restore` → `dotnet build` → 失败即红

仓库：
- `README.md` 占位

**验收**：本地 `dotnet build` 通过；`/healthz` 返回 200；NuGet 还原成功拉取 `Docker.DotNet.Enhanced`。

---

### 0.0.2 — 后端基础设施

**目标**：所有后续接口的统一基线。

后端：
- 全局异常处理中间件（统一映射未捕获异常到 `ApiResult<T>` code=9999）
- `ApiResult<T>` 统一响应模型 + `BusinessException` 体系
- 配置系统（`appsettings.json` + 环境变量覆盖，key 用 `__` 分隔嵌套）
- Serilog 滚动文件输出（`/app/data/logs/app-.log`，按天滚动，保留 14 天）
- CORS 白名单（默认拒绝跨域）

**验收**：`GET /healthz` 异常时返回标准 `ApiResult` JSON。

---

### 0.0.3 — 数据层与密码安全

**目标**：用户表可创建，密码可哈希校验。

后端：
- SQLite 初始化（`/app/data/docker-manager.db`，目录不存在自动创建）
- Dapper 接入，仓储层抽象 `IUserRepository`
- 启动时建 `users` 表（id、username、password_hash、role、created_at）
- Argon2id 密码哈希（`Isopoh.Cryptography.Argon2`），禁止明文落库与日志
- 单元测试覆盖哈希/校验

---

### 0.0.4 — 认证 API（核心）

**目标**：可通过 API 登录拿到 JWT。

后端：
- JWT access token（HS256，15 分钟）+ refresh token（随机 32 字节 base64，7 天）
- `POST /api/v1/auth/login`：校验用户名/密码，返回 access + refresh
- `[Authorize]` 中间件 + 角色检查 `[Authorize(Roles = "admin")]`
- 登录速率限制（按 IP + 用户名，5 次/分钟 → 429）
- 单元测试覆盖登录成功/失败/锁定

**验收**：curl 测试登录成功 → 用 access token 调用受保护接口 → 不带 token 返回 401。

---

### 0.0.5 — 认证完善 + 前端基础设施

**目标**：完整认证闭环 + 前端可对接后端。

后端：
- `POST /api/v1/auth/refresh`：用 refresh token 换新 access
- `POST /api/v1/auth/logout`：撤销当前 refresh token（写入黑名单表）
- 安全响应头中间件：X-Content-Type-Options=nosniff、X-Frame-Options=DENY、Referrer-Policy=no-referrer
- 密码强度校验工具类（≥10 字符、含字母+数字或符号）

前端：
- `src/web/` 工作区，Vite + Vue 3 + TypeScript + Element Plus 骨架
- `vite.config.ts` 配置 `/api` 与 `/hubs` 代理到 `http://localhost:8080`
- Pinia `useUserStore`（user、accessToken、refreshToken 持久化到 localStorage）
- Axios 实例 + 拦截器：自动注入 Authorization、`401` 时尝试 refresh 并重发原请求
- Vue Router 全局守卫：未登录跳 `/login`

**验收**：登录页表单提交 → 拿到 token 跳首页；token 过期自动刷新。

---

### 0.0.6 — 部署底座与初始管理员引导

**目标**：`docker run` 一行启动可登录。

后端：
- 启动时检测 `users` 表是否为空：若空，按环境变量 `ADMIN__USER` + `ADMIN__PASSWORD` 创建 admin（密码强度校验）
- 健康检查端点 `GET /healthz` 增强：返回 SQLite 连通性
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

### 0.0.7 — 前端登录页与基础布局

**目标**：可视化的登录体验 + 全局导航骨架 + UnoCSS/主题/快捷键基建。

前端（详细见 `docs/ui-guidelines.md`）：
- **UnoCSS 接入**：`unocss` + `presetUno` + `presetTypography` + `presetIcons`（Iconify 按需）
- **主题系统**：`composables/useTheme.ts`（亮色 / 暗色 / 跟随系统 + 蓝/绿/紫主题色）
- `LoginView.vue`：居中卡片 + Element Plus 表单 + 主题切换按钮
- **主布局** `layouts/AppLayout.vue`：顶栏 64px + 左侧 200px 可折叠侧边栏 + 内容区；移动端侧边栏变抽屉
- **顶栏组件**：`Breadcrumb.vue`、`UserMenu.vue`
- **侧边栏组件**：`Sidebar.vue`（按 `route.meta.showInSidebar` 与 `requiresAdmin` 过滤）
- 国际化：vue-i18n，`locales/zh-CN.json` / `locales/en-US.json`；key 用 dotted 命名，缺失 fallback 英文
- 占位首页 `DashboardView.vue`
- 路由守卫实现未登录跳 `/login` 与 `requiresAdmin` 拦截

**验收**：登录页英文/中文切换正常；切换主题颜色变化；侧边栏可折叠且状态持久化；移动端（375px）侧边栏变抽屉可正常开关。

---

### 0.0.8 — Docker 主机抽象

**目标**：可注册多个 Docker 主机，按主机切换操作。

后端：
- 新建 `hosts` 表（id、name、type=socket|tcp、endpoint、tls_config_json、enabled、created_at）
- 启动时若 hosts 表为空，插入默认主机：`name="local"`、`type=socket`、`endpoint="/var/run/docker.sock"`
- `Infrastructure` 项目额外引入 `Docker.DotNet.Enhanced.X509` + `Docker.DotNet.Enhanced.BasicAuth`
- `IDockerClientFactory`（在 `Core` 中定义接口，`Infrastructure` 实现）：按 `hostId` 返回缓存的 `DockerClient` 实例
  - 使用 `DockerClientBuilder` 风格
  - socket：`WithEndpoint(new Uri("unix:///var/run/docker.sock"))`
  - TCP（无认证）：`WithEndpoint(new Uri("tcp://host:2375"))`
  - TCP + TLS：`WithEndpoint(...)` + `WithAuthProvider(new CertificateCredentials(cert))`
  - TCP + Basic：`WithEndpoint(...)` + `WithAuthProvider(new BasicAuthCredentials(user, pass))`
- `IDockerHostAccessor`：Scoped 服务，从请求 Header `X-Host-Id`（缺省 `"local"`）解析当前 host
- 主机管理 API（admin 专用）：
  - `POST /api/v1/host/list` / `create` / `update` / `delete` / `test`
- 用户管理 API（admin 专用）：
  - `POST /api/v1/user/list` / `create` / `delete` / `update-password`
- `IResourcePermissionService` 接口定义（0.1.0 仅实现 host 可见性）

测试（从本迭代起为强制要求）：
- `tests/Docker.Manager.IntegrationTests/` 引入 `Testcontainers` for .NET
- 容器/镜像/Volume/Network 等新 API 必须配集成测试，CI 卡
- 不设行覆盖率门槛

---

### 0.0.9 — 容器 API 后端

**目标**：所有容器 CRUD 操作可通过 API 完成。

后端：
- 容器 API：
  - **`POST /api/v1/container/create`**（`CreateContainerParameters` 完整字段映射）
  - `POST /api/v1/container/list`（支持 `hostId` + `all` + 分页）
  - `POST /api/v1/container/start` / `stop` / `restart` / `remove` / `logs` / `inspect`
- 所有接口走 `IDockerHostAccessor` 获取客户端，按 host 粒度做可见性校验
- `Docker.DotNet.Enhanced` 异常统一映射：
  - `DockerApiException` → 通用 Docker 错误（code 1402）
  - `DockerContainerNotFoundException` → code 1401（资源不存在）
  - `DockerImageNotFoundException` → code 1401
  - `TaskCanceledException` → code 1403（操作超时）

**验收**：curl 完整跑通容器 create/list/start/stop/restart/remove/logs/inspect；非授权 host 返回 code 1501。

---

### 0.0.10 — 镜像 API 后端

**目标**：镜像 CRUD + 拉取（含进度回传）。

后端：
- 镜像 API：
  - `POST /api/v1/image/list`（`hostId` + 分页）
  - `POST /api/v1/image/pull`（`hostId` + `image` + `tag`）
  - `POST /api/v1/image/remove`（`hostId` + `imageId` + `force`）
  - `POST /api/v1/image/inspect`（`hostId` + `imageId`）
- Pull 进度：使用 `IProgress<JSONMessage>` 在服务端聚合，**0.0.10 不接入 SignalR**，仅在响应 `data` 一次性返回最终结果；进度信息写到 Serilog 日志。**SignalR 实时进度推迟到 0.3.0**

**验收**：pull `nginx:latest` 完成后返回镜像 ID 与大小。

---

### 0.0.11 — 前端：容器 UI

**目标**：可视化容器管理。

前端：
- API 封装层 `src/web/src/api/container.ts`：RPC 风格方法封装
- `src/web/src/stores/host.ts`：Pinia 当前主机选择 + 持久化
- **顶栏 `HostSelector` 下拉组件**：显示当前主机名 + 在线状态点；列出可访问主机；admin 可见「管理主机」入口；切换写入 Pinia `useHostStore` + 自动后续请求带 `X-Host-Id`（通过 `composables/useDockerRequest.ts` 拦截器实现）
- `ContainerListView.vue`：表格（名称、镜像、状态、端口、创建时间）+ 操作按钮（启动/停止/重启/删除）+ 状态自动刷新（轮询 5 秒）
- **`CreateContainerDialog.vue`**：可视化创建表单（镜像下拉 + 名称 + 端口映射 + 环境变量键值对 + Volume 挂载 + 重启策略选择 + 高级选项折叠面板「网络模式 / 资源限制 / capabilities」）
- `ContainerLogsView.vue`：行数选择 + tail 加载 + 时间过滤 + 自动滚动开关
- `ContainerInspectView.vue`：JSON 树形展示 + 基本信息卡片

---

### 0.0.12 — 前端：镜像 UI

**目标**：可视化镜像管理。

前端：
- `ImageListView.vue`：表格（仓库、标签、镜像 ID、创建时间、大小）+ 删除按钮
- `ImagePullDialog.vue`：镜像名 + 标签输入 + 进度展示（轮询 0.0.10 的 pull 结果，**不接入 SignalR**）+ 完成后自动刷新列表
- `ImageInspectView.vue`：JSON 树形展示

---

### 0.0.13 — 前端：管理 UI + MVP 打磨

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

### 0.1.0-rc.1 — MVP 候选发布

**目标**：0.0.13 后冻结功能，仅修 bug。

- 不引入新功能
- 集中修复 QA 反馈
- 文档审阅与中英文一致性校对
- CI 自动构建 `v0.1.0-rc.1` tag → 推 GHCR → 创建 Pre-release

**验收**：3 名外部用户在自托管环境跑通完整流程。

---

## 0.1.0 — MVP 首个 semver 稳定版 🎉

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

## 0.2.0 — Volume / Network / 镜像构建

**目标**：把 Docker 主要资源类型管理补齐；支持上传 Dockerfile 构建镜像；增加批量删除能力。

后端：
- Volume API：`POST /api/v1/volume/{list,create,remove,batch-remove,inspect}`
- Network API：`POST /api/v1/network/{list,create,remove,inspect}`
- 镜像构建：`POST /api/v1/image/build`（multipart：Dockerfile + context tar），通过 `IProgress<JSONMessage>` 推送进度到 SignalR 临时频道（**仅本请求客户端可见**）
- 容器批量删除：`POST /api/v1/container/batch-remove`（接受 `containerIds[]`，并行删除，返回每个的成功/失败状态）
- 镜像批量删除：`POST /api/v1/image/batch-remove`（接受 `imageIds[]`）
- 私有 Registry 凭据**存储**（暂不暴露 pull/push 入口，凭据加密落 SQLite）

前端：
- Volume / Network 列表/创建/详情页
- 镜像构建对话框（文件上传 + 实时进度条）
- 容器/镜像列表页增加**多选 checkbox** + 「批量删除」操作按钮（带二次确认）

---

## 0.3.0 — 实时推送与资源粒度 RBAC

**目标**：上线实时日志 / 监控 / 异常通知；落地按 host/container 粒度的资源授权。

后端：
- SignalR Hub：`/hubs/container-log`（订阅日志）、`/hubs/container-stats`（订阅统计）、`/hubs/notification`（全局通知）
- Hub 鉴权：JWT 通过 `?access_token=...` 查询参数传递（WebSocket 不支持自定义 Header）
- `ContainerEventWatcher`（后台 `Docker.Events` 监听）→ 推到 `NotificationHub`
- 资源权限表：`user_host_permissions`（user_id × host_id，权限位 read/write/admin）、`user_container_permissions`（细粒度补充）
- `ResourcePermissionMiddleware`：在容器/镜像/Volume/Network 操作前校验用户对 hostId（必要时 containerId）有 read/write 权限
- 授权管理 API：`POST /api/v1/permission/{grant,revoke,list}`
- 镜像 pull 进度改用 SignalR 推送

前端：
- 容器日志页改用 SignalR 实时流（保留历史 tail 加载）
- 容器详情页增加实时 CPU/内存/网络图（轻量图表：自实现 sparkline 或 echarts）
- 全局通知条（容器 died → toast）
- 用户管理页增加「分配主机权限」操作

---

## 0.4.0 — 生态：exec 终端 / Compose / Registry

**目标**：覆盖 Portainer CE 90% 用户场景。

后端：
- **新增 `POST /api/v1/auth/terminal-token`**（admin/user 专用）：返回 5 分钟过期的独立短时 token，仅能用于 WebSocket 连接 `/ws/container/exec`；不继承 access token 的全 API 权限
- WebSocket 端点 `/ws/container/exec?containerId=...&cmd=/bin/sh&token=<terminalToken>`（独立于 SignalR，原始双向流，**鉴权专用 terminal token**，不接受 access token）
- Stack 管理：`POST /api/v1/stack/{list,deploy,update,stop,remove}`，内部用 Docker.DotNet 调用 + label 过滤（`com.docker.compose.project=<name>`）
- 私有 Registry 登录：`POST /api/v1/registry/{login,list,remove}` + 凭据安全存储（DPAPI / OS keyring）
- 镜像 push：`POST /api/v1/image/push`（使用已配置的 Registry 凭据）

前端：
- 容器详情页内嵌 xterm.js 终端面板（AttachAddon + FitAddon）；打开终端时先请求 terminal-token，再用其连接 WebSocket
- Stack 管理页（上传 YAML、查看服务列表）
- Registry 配置页（添加、测试连接、删除）
- 镜像列表增加「push 到 Registry」按钮（仅当目标镜像已配置 Registry 时可用）

---

## 1.0.0 — 生产就绪版

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
