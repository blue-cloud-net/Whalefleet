# 开发路线图

> 路线图以**版本里程碑**为单位组织，不绑定具体日期。严格遵循**语义化版本**（SemVer）。
>
> **唯一目标版本是 `1.0.0`**——本项目此前规划的首个对外版本是 `0.1.0`，
> 现已推迟：`0.1.0` 至 `0.8.0` 是功能迭代，`1.0.0` 才是首个对外发布的版本，
> 且必须先经过 `1.0.0-rc.N` 灰度。

---

## 版本段与发布模型

| 版本段 | 含义 | 打 tag | 推 GHCR |
|--------|------|:---:|:---:|
| `0.0.x` | **已完成的开发迭代**（0.0.1 – 0.0.15） | ❌ | ❌ |
| `0.x.0` | **功能迭代**——按模块成块交付 | ❌ 只作迭代编号 | ❌ |
| `1.0.0-rc.N` | **首个对外灰度**——只修 bug + 走完准入门槛 | ✅ | ✅ Pre-release，**不打 `latest`** |
| `1.0.0` | **首个正式版**——公开 API 冻结承诺自此生效 | ✅ | ✅ 含 `latest` |
| `1.x.y` | **修复版本**——自这一级起，小版本用于发补丁 | ✅ | ✅ |

> **为什么 0.x 完全不打 tag**：tag 是「一个已发布、可被他人引用的事实」。
> 0.x 从不对外发布，打 tag 只会制造「有 tag 却 pull 不到镜像」的误解，
> 并让 Release Please 的发布 PR 反复被提出却不该被合并。
> 0.x 的交付记录写在 [CHANGELOG.md](../CHANGELOG.md) 与本文件，正式发布时汇总进 `1.0.0` 一节。

### 版本号由谁算

不手工定版本号。Release Please 读 Conventional Commits 推导：

| 提交类型 | 版本变化 |
|---------|---------|
| `feat` | 小版本 +1（`0.1.0` → `0.2.0`） |
| `fix` | 修订号 +1 |
| `perf` / `refactor` / `docs` / `style` / `test` / `chore` / `ci` / `build` | 不触发发版 |
| `feat!` / `fix!` | 1.0.0 之前**只算小版本 +1**；1.0.0 之后才算主版本 +1 |

- **0.x 期间不细分 `0.x.y`**：开发期改动幅度大，一个迭代内混着新功能与修复是常态，
  强行按「功能版 / 修复版」切分会产出大量无实际内容的版本号。
- 突破常规的版本（`1.0.0`、`1.0.0-rc.1`）在提交信息里写一行 `Release-As: 1.0.0` 指定。
- 发布流程（何时打 tag、CHANGELOG 谁写）见 [AGENTS.md](../AGENTS.md#发布流程)。

---

## 进度图例与总览

> **图标约定**：版本标题与条目**首列**的图标即为其状态，全文统一。

| 图标 | 状态 | 判定口径 |
|:---:|------|----------|
| ✅ | **已完成** | 代码已合入主干，且该条目的验收标准全部通过 |
| 🚧 | **进行中** | 已开工但未闭环——本节至少一个条目尚未完成 |
| ⬜ | **未开始** | 尚未开工，条目与设计处于最终态 |
| ❌ | **明确不做** | 已判定为非目标（见文末「1.0.0 之后」） |

> **维护规则**
>
> 1. 条目级图标**只出现在「已完成」与「进行中」的版本内**；未开始的版本仅在标题标 ⬜，条目保持干净。
> 2. 子条目默认**继承父条目的图标**；只有明确标注状态时才单独覆盖。
> 3. 一个版本内**不再有任何 ⬜ 条目**时，才能把标题从 🚧 改成 ✅。
> 4. 「验收」小节是**该版本的收口清单**，只有它整体打 ✅ 才算真正交付。
> 5. **`0.x.0` 的编号即执行顺序**：迭代按编号从小到大推进，编号本身不重排。
>    反过来，某个编号尚未开工时可以整体后移（连同其后续编号一起平移），
>    但**已经开工的编号不可改**——改号会让 CHANGELOG、issue 与文档里的引用全部对不上。
> 6. 0.0.1 – 0.0.15 的迭代详情已从本文件移除，交付内容见
>    [CHANGELOG.md](../CHANGELOG.md)，设计决策见 [architecture.md](./architecture.md) 与
>    [constraints.md](./constraints.md)。`0.0.6` 是**空号**，不对应任何里程碑。

### 当前进度（截至 2026-10-15）

| 版本 | 主题 | 状态 | 备注 |
|------|------|:---:|------|
| `0.0.1` – `0.0.15` | 项目骨架到部署底座 | ✅ | 15 个迭代全部完成，交付内容见 CHANGELOG |
| `0.1.0` | 授权基座与 MVP 收口 | 🚧 | 授权重写为带作用域的 User → Role → Permission（进行中）；批量删除 / socket-proxy / 反代示例已完成 |
| `0.2.0` | Volume 与 Network | ⬜ | 两类资源的 CRUD + 详情 |
| `0.3.0` | 镜像构建与私有 Registry | ⬜ | 构建 + 凭据存储 + login + push |
| `0.4.0` | 实时通道（SignalR） | ⬜ | 日志流 / stats / 通知条；进度推送改实时 |
| `0.5.0` | 资源粒度 RBAC | ⬜ | 授权下沉到容器级 |
| `0.6.0` | exec 交互式终端 | ⬜ | WebSocket + xterm.js + 短时 terminal token |
| `0.7.0` | Docker Compose / Stack | ⬜ | 部署 / 更新 / 停止 / 删除 Stack |
| `0.8.0` | API 契约与可观测性 | ⬜ | OpenAPI/Scalar、指标、文档定稿 |
| `1.0.0-rc.N` | 首个对外灰度 | ⬜ | 只修 bug + 走完准入门槛 |
| `1.0.0` | 首个正式版 🎉 | ⬜ | API 冻结承诺生效 |

---

## 阶段一：功能迭代（0.x，不对外发布）

> 每个 `0.x.0` 迭代 = 1 个可独立验收的功能块。允许破坏性变更，仅自测。
> 0.0.15 已交付的容器 / 镜像 / 主机 / 用户能力不在此重复列出。

### 0.1.0 🚧 — 授权基座与 MVP 收口

**目标**：落地**带作用域的 User → Role → Permission** 授权模型——权限落在角色上、分配带主机作用域，
让角色真正参与授权判定；并修掉影响部署正确性的缺陷。

**授权基座**

- ⬜ **权限目录**：静态封闭集合（**不建表**——「应用有没有某功能」是编译期事实），
  按 `domain.level` 命名，分 Global 与 Host 两类作用域。Host 域为 `host.read` / `host.admin`
  与 `container` / `image` / `network` / `volume` 各自的 read + write；
  Global 域为 `user.read` / `user.write` / `role.read` / `role.write` / `host.manage` / `audit.read`。
  `PermissionScope` **预留 Container 一档**，但本迭代只开放全局与主机两条分配路径
- ⬜ **角色携带权限**：新增 `role_permissions` 表与迁移；`Role` 实体带权限集合，
  内置角色（`admin` / `operator` / `viewer` / `auditor`）的权限集**不可修改**。
  `users.role_id` 废弃——用户 ↔ 角色改为多对多
- ⬜ **带作用域的角色分配**：新增 `user_role_assignments`（`host_id` 可空，NULL = 全局分配）。
  全局分配的全局权限生效；主机分配只在该主机上生效，且其**全局权限被忽略**
  ——否则「把 admin 分配到某台主机」会静默变成全局提权
- ⬜ **角色管理 API**：`POST /api/v1/admin/roles/{list,create,update,remove,set-default,set-permissions}`；
  删除仍有有效分配的角色返回 `1512`（须先撤销分配）
- ⬜ **角色分配 API**：主机中心 `POST /api/v1/admin/hosts/{hostId}/roles/{list,assign,revoke}`
  与用户中心 `POST /api/v1/admin/users/{userId}/roles/{list,assign,revoke}`
- ⬜ **鉴权解析改查库**：带作用域的分配装不进单个 JWT claim，因此授权事实改为**每次请求解析并缓存**，
  `[Authorize(Roles = …)]` 换成按权限判定的自定义 `AuthorizationHandler`，内置 `admin` 角色短路。
  JWT 里的角色名只供界面展示、不再参与判定；附带收益是角色与权限改动**即时生效**，不等 token 过期
- ⬜ 新错误码：`1510` 角色不存在、`1511` 角色名已占用、`1512` 角色仍有分配、
  `1513` 内置角色受保护、`1514` 未知权限、`1515` 角色分配不存在
- ⬜ **前端**：角色管理页（角色 → 权限矩阵，内置角色只读）；用户管理与主机视图的
  「可见主机」对话框改为**角色选择器**；登录与 `auth/me` 返回 `roles` 与全局有效权限集，
  菜单与操作按钮按权限集显隐

**MVP 收口**

- ✅ **批量删除端点**：容器与镜像各一条 `batch-remove`（前端 `BatchActionBar` 已就绪，本迭代补上后端；
  **部分成功也返回 200**，逐条汇报失败）
- ✅ **socket-proxy 写操作修复**：`docker-compose.yml` 的 `POST: 1` 与固定版本已就位（0.0.x），
  本迭代补上 `proxy` 对 `socket-proxy` 的 `depends_on`（否则启动顺序不定 → 首启探活报 `1301`）
- ✅ 反向代理示例配置：`examples/reverse-proxy/`（nginx 与 Traefik，含 TLS 与 `X-Forwarded-*`），
  `.env.example` 补 `REVERSEPROXY__*`

**验收**：

1. `viewer` 被分配到某台主机后，能看到该主机及其全部容器 / 镜像 / 卷 / 网络，
   但启停 / 删除任一资源返回 `1502`；改授 `operator` 后可写
2. 同一用户在 A 主机为 `operator`、在 B 主机为 `viewer` 时，两台主机上的操作权限**互不串味**
3. 运行时新建自定义角色并分配后，**无需重登、无需等待 token 过期**即生效
4. 新建用户自动获得默认角色（`auditor`）的全局分配
5. 把内置 `admin` 分配到某台主机后，它带来的**全局权限不生效**（主机分配只认主机域权限）
6. `socket-proxy` 模式下真的完成「创建 → 启动 → 停止 → 删除」一个 nginx 容器并拉取一个镜像

> **待手工跑的那一条**：socket-proxy 模式的「创建 → 启动 → 停止 → 删除 + 拉取镜像」
> 需要真实起一套 compose + socket-proxy 容器，属于部署验收而非 CI 能覆盖的范围。
> 其余条目（授权闭环、批量删除的 200/1404 分支、反代配置）已有集成测试与前端契约测试覆盖。
>
> **迁移口径**：迁移建出授权相关的两张新表并调整 `users`，**不搬运历史授权数据**——
> 0.x 从未产出正式发布，没有存量部署可搬运。迁移脚本的注释需写明这一取舍，避免后来者以为是漏写。

---

### 0.2.0 ⬜ — Volume 与 Network

**目标**：把 Docker 主要资源类型管理补齐。

- Volume API：`POST /api/v1/hosts/{hostId}/volumes/{list,create,remove,batch-remove,inspect}`
- Network API：`POST /api/v1/hosts/{hostId}/networks/{list,create,remove,inspect}`
- 前端：两个侧边栏占位页转正（列表 / 创建 / 详情）
- 容器创建表单接入卷与网络选择

**验收**：创建具名卷并挂进容器后能读到内容；创建自定义网络并把容器接入；
两个占位页不再是预告文案。

---

### 0.3.0 ⬜ — 镜像构建与私有 Registry

**目标**：支持上传 Dockerfile 构建镜像，并把镜像推到私有仓库。

- 镜像构建：`POST /api/v1/hosts/{hostId}/images/build`（multipart：Dockerfile + context tar）。
  **本版一次性返回结果**，与现有 `images/pull` 保持同一形态；进度推送随 0.4.0 一起改
- Registry 凭据存储（加密落 SQLite）、登录与验证
- Registry 管理 API：`POST /api/v1/admin/registries/{list,add,remove,test}`
- 镜像 push：`POST /api/v1/hosts/{hostId}/images/push`
- 前端：构建对话框（文件上传）、Registry 配置页、镜像列表的「push」操作

**验收**：上传一个 Dockerfile 能构建出镜像；配置一个私有 Registry 后能 push 成功。

---

### 0.4.0 ⬜ — 实时通道（SignalR）

**目标**：把「轮询 + 转圈」换成实时推送。

- SignalR Hub：`/hubs/hosts/{hostId}/container-log`、`/hubs/hosts/{hostId}/container-stats`、
  `/hubs/notification`（全局通知，不含 hostId）
- Hub 鉴权：JWT 通过 `?access_token=...` 查询参数传递（WebSocket 不支持自定义 Header）
- `ContainerEventWatcher`（后台 `Docker.Events` 监听）→ 推到 `NotificationHub`
- 镜像 pull 与构建的进度改用 SignalR 推送（替换 0.3.0 的一次性等待）
- 前端：容器日志页改实时流（保留历史 tail 加载）、详情页 CPU/内存/网络图、全局通知条

**验收**：容器输出日志时页面逐行出现；停止容器后 1 秒内收到通知；
100 个容器同时刷新时列表不卡顿。

---

### 0.5.0 ⬜ — 资源粒度 RBAC

**目标**：把 0.1.0 建好的作用域化角色分配下沉到容器级。

- `user_role_assignments` 增加 `container_id`（与 `host_id` 成对出现——容器 ID 只在单台守护进程内唯一），
  容器级分配复用同一张表与同一套判定，不另立模型
- 判定链路扩展到容器级：主机级分配给的是该主机的域级权限，容器级分配在其之上**再收窄**——
  两者取交集，容器级分配不会放宽主机级分配已经禁止的操作
- 授权管理 UI 细化到容器粒度；错误码 `1502`（write 权限缺失）在本版启用

**验收**：只在主机级分配了 `container.read` 的用户能看列表但不能启停容器；
追加一条容器级 `container.write` 分配后可操作该容器、且操作不到同主机的其他容器。

---

### 0.6.0 ⬜ — exec 交互式终端

**目标**：在容器内开一个可交互的 shell。

- **新增 `POST /api/v1/auth/terminal-token`**：返回 5 分钟过期的独立短时 token，
  仅能用于 WebSocket 连接 `/ws/hosts/{hostId}/containers/exec`；**不继承 access token 的全 API 权限**
- WebSocket 端点 `/ws/hosts/{hostId}/containers/exec?containerId=…&token=…`
  （独立于 SignalR，原始双向流；不接受 access token）
- 错误码 `1104`（terminal token 无效或过期）在本版启用
- 前端：容器详情页内嵌 xterm.js 终端面板（FitAddon + WebLinksAddon）

**验收**：打开终端能执行 `ls` / `uname` 并看到输出；
terminal token 过期后无法重连；断开后不残留容器内进程。

---

### 0.7.0 ⬜ — Docker Compose / Stack

**目标**：覆盖 Portainer CE 大部分用户的实际使用场景。

- Stack 管理：`POST /api/v1/hosts/{hostId}/stacks/{list,deploy,update,stop,remove}`
  （用 Docker.DotNet 调用 + label `com.docker.compose.project=<name>` 过滤）
- 前端：Stack 管理页（上传 YAML、查看服务列表、启停与删除）
- 主菜单「编排」占位页转正

**验收**：上传一份 `docker-compose.yml` 能部署出服务并看到状态；
更新 YAML 后能滚动重启；删除 Stack 后其容器一并消失。

---

### 0.8.0 ⬜ — API 契约与可观测性

**目标**：让接口与运行状态对外可查，为 1.0.0 收口。

- **OpenAPI 自动生成**（`Microsoft.AspNetCore.OpenApi`）+ **Scalar UI**（`/scalar/v1`）
- **API 冻结的可执行手段**：路由清单与 DTO 形状的快照测试
  （冻结承诺若没有快照测试，就只是一句口号）
- **版本可见性**：端点与 UI 暴露应用版本，用户报障时能确认跑的是哪个版本
- 前端性能基线复测（列表 P95）

**验收**：Scalar 页面能浏览全部端点；
改动任一响应 DTO 会让快照测试失败；界面某处能显示当前版本号。

---

## 阶段二：候选与正式发布（1.x）

### 1.0.0-rc.N ⬜ — 首个对外灰度

**目标**：冻结功能，只修 bug，同时走完下列准入门槛。

- 不引入新功能
- 集中修复自测与外部反馈
- 中英文文档一致性校对
- CI 自动构建 `v1.0.0-rc.N` tag → 推 GHCR（**不打 `latest`**）→ 创建 GitHub Release

**准入门槛**（全部完成才发 rc）：

| # | 项 | 口径 |
|---|----|------|
| 1 | 性能压测 | 1000 容器主机的 list P95、SignalR 并发订阅数、SQLite 写吞吐；对照 0.0.14 建立的 100 容器基线（P50 12ms / P95 35ms） |
| 2 | 安全审计 | NuGet 与 pnpm 依赖 CVE 扫描、镜像扫描、Docker Socket 风险复审、反代示例配置复核 |
| 3 | **arm64 镜像冒烟** | 多架构推送里的 arm64 层必须**真的启动过**——当前只在 amd64 runner 上验证过 |
| 4 | 迁移连续性 | 从任意已发布的 0.x 数据卷升级到 1.0.0 的步骤与回滚说明 |
| 5 | 备份 / 恢复演练 | 含 `jwt.key` 的完整恢复，并在旧版本数据卷上实测 |
| 6 | 质量门槛转硬门槛 | `dotnet format` 零违规（去掉 `continue-on-error`）；前端 `pnpm lint` 与 `pnpm test` 进 CI |
| 7 | 文档定稿 | 双语逐节一致、截图补齐、API 冻结声明写入 [api-spec.md](./api-spec.md) |
| 8 | 外部验收 | 3 名外部用户在自托管环境跑通完整流程 |

**验收**：8 项门槛全部 ✅；`v1.0.0-rc.1` 可从 GHCR 匿名拉取。

---

### 1.0.0 ⬜ — 首个正式版 🎉

**对外承诺**（自本版起生效）：

- **公开 API 路径与请求 / 响应结构冻结**，向后兼容到 2.0.0
- **Docker SDK 抽象层不变**：`Application/Abstractions` 中的 `IDockerClientFactory` /
  `IDockerHostAccessor` 接口稳定；`Infrastructure` 里的 SDK 版本可平滑升级，
  不影响 Controller 与应用服务层
- **SQLite schema 版本化管理**：启动时检查 `PRAGMA user_version` 并自动迁移，
  迁移脚本从 1 连续编号

---

## 1.0.0 之后（明确非目标）

> 以下能力**不在 1.0.0 范围内**，需求已记录在 [backlog.md](./backlog.md)：

- Agent 模式（目标主机部署轻量 Agent）
- OAuth / OIDC 登录
- 操作审计日志（事件页目前只有表格骨架）
- 多实例 + 外部数据库（PostgreSQL）
- 系统 prune（容器 / 镜像 / 网络 / 卷清理）
- 一次性 exec API 与一次性 stats API（只做长连接）
- 原生包 + systemd 部署形态（当前只提供容器化与独立进程两种）
- Prometheus `/metrics` 端点
- 公开 demo 实例
