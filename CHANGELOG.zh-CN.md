# 变更记录

本文件记录本项目所有值得注意的变更。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

> **语言 / Language**: [English](CHANGELOG.md) | **简体中文**
>
> **与自动化工具的交接**：`0.0.1` – `0.0.14` 的条目由人工维护，内容汇总自
> [docs/roadmap.md](docs/roadmap.md) 中标记为已完成的里程碑。自 **0.0.15** 起，
> 英文版 [CHANGELOG.md](CHANGELOG.md) 交由 [Release Please](https://github.com/googleapis/release-please)
> 接管：它从 Conventional Commits 生成变更说明、每个版本开一个 PR 并自动打 tag。
> `CHANGELOG.zh-CN.md` **不会**被自动生成，英文版每次变更时必须手工同步本文件。

> **关于 `0.0.x` 的说明**：每个 `0.0.x` 迭代都是内部开发里程碑，不向 GHCR 发布 `0.0.*` tag，
> 迭代之间 API 可能发生破坏性变更。`0.0.6` 已改编号为 `0.0.15`（部署底座与发布流水线），
> 因此 `0.0.6` 是**空号**——不对应任何里程碑。

---

## [Unreleased]

暂无。下一个里程碑是 `0.1.0-rc.1`（候选发布：冻结功能，只修 bug）。

> `0.0.15`（部署底座与发布流水线）已在 `dev` 完成；其变更记录小节与 tag
> 将由 Release Please 在本次改动合入后自动生成。

---

## [0.0.14] — MVP 打磨（前端）

里程碑目标：补齐进入 RC 所需的交互与非功能细节。

### 新增

- **命令面板** `components/common/CommandPalette.vue`：Ctrl+K / Cmd+K 唤起；用 Fuse.js 模糊匹配搜索
  所有路由与常用操作（跳 Containers / 跳 Images / 创建容器 / 拉取镜像）。
  阈值放宽到 `0.35`：面板查询普遍很短（`img`、`镜`），Fuse 默认阈值下这类输入一条都搜不出来，
  空面板比给几个无关结果更让人困惑。fuse.js 走动态 import（26 kB / gzip 9.8 kB 独立 chunk），
  不为偶尔用的功能拖慢首屏。额外加了 `g+b`（折叠侧栏）与 `g+s`（跳主机状态）。
- **全局快捷键** `composables/useShortcuts.ts`：`g+c` / `g+i` / `g+v` / `g+n` / `g+d` / `?` 跳转与帮助弹窗。
  三条约束：输入框内一律不劫持；除 Ctrl+K 外不抢占任何带修饰键的组合；chord 超时 1.5 s，
  且第二个键无论是什么都先清空待定状态。帮助弹窗内容由**同一份 chord 注册表**生成，
  不会出现「加了键但帮助里没写」。
- **通用组件**：`StatusBadge`、`JsonTree`、`CopyableText`。
  `CopyableText` 带非安全上下文回退——http 部署时 `navigator.clipboard` 不可用，而自托管场景很常见。
  `JsonTree` 按类型着色叶子标量，替代原先只能全量 `<pre>` 的 inspect 展示。
- **业务组件**：`ContainerStatusBadge`、`ImageSize`、`PortMappingEditor`、`EnvVarEditor`、
  `VolumeMountEditor`。三个编辑器都是「批量粘贴 + 逐行编辑」双模：用户手上的环境变量与端口
  几乎总是已经存在的，逐行填表格是纯粹的重复劳动。解析逻辑抽到 `utils/container-input.ts` 并单测；
  解析失败时编辑器只回传**出错行之前**的合法部分，因此 `errorLine` 必须经 `v-model:error-line`
  回传父级禁用提交——否则会建出「少了一条环境变量却提示成功」的容器。
- **仓库**：`README.md` 定稿（CI 徽章、功能截图占位、文档链接矩阵、`docker run` 命令、
  Docker Socket 挂载安全风险说明与推荐的 socket-proxy 模式）、`docs/*.md` 第一版完整内容。
  修正了若干**不实描述**：原 README 让用户 `docker compose up` 与拉取镜像，但 `Dockerfile` /
  `docker-compose.yml` 要到 0.0.15 才交付；且 `SECURITY.md` / `CONTRIBUTING.md` 从未存在。

### 变更

- 三个编辑器**已接进页面**而非建完搁置：`ContainerStatusBadge` 用于容器列表与详情页，
  `ImageSize` 用于镜像列表与详情页（并区分独占层体积 / 逻辑总体积），
  `JsonTree` 替换镜像详情页的全量 `<pre>`。
- **撤掉了挂载类型选择器**：后端 `ContainerVolumeMapping` 根本没有 `type` 字段
  （bind 与具名卷由 Docker 按来源路径是否存在判定），界面加下拉只会得到一个「选了也不生效」的开关。
- `ui-guidelines.md` 的快捷键表同步为实现结果。

### 安全

- 全量日志脱敏审计：认证链路（登录 / 刷新 / 登出 / 改密 / 管理员引导）确认干净；但发现一处
  **P1**——`HostService` 在 Information 级别记录端点原文，而端点是凭据载体
  （`tcp://admin:s3cr3t@host` 会被接受并落盘）。已补 `HostEndpoint.Redact()` 作为第二道防线。

### 性能

- 性能基线：容器 list 100 个容器场景 **P50=12ms / P95=35ms**（阈值 500 ms），
  起回环 HTTP 假守护进程而非 mock `IDockerClient`，顺带覆盖 Docker.DotNet 的 HTTP 往返与 JSON 反序列化。

---

## [0.0.13] — 管理 UI（前端）

里程碑目标：管理员可在界面上完成主机与用户管理。

### 新增

- **用户管理** `views/UsersView.vue`：用户列表 + 新建对话框 + 重置密码对话框 + 删除确认；
  自己的删除按钮**直接禁用**——后端也有同样护栏，但让用户点下去才报错是糟糕的交互。
- **主机管理** `views/HostsView.vue`：主机列表（连通性**三态**：可连接 / 连不上 / 未探测）+
  创建 / 编辑对话框 + 删除确认 + 启用开关（停用需二次确认）。
- **主机分组管理 UI**：分组的新建 / 重命名 / 删除 + 主机归组调整（后端 `host_groups` 表与
  `hosts.group_id` 列随本版落地）。内置分组名取 i18n，自定义分组原样显示管理员输入。
- **改密对话框** `ChangePasswordDialog.vue`：任意用户可从顶栏用户菜单改自己密码；
  成功后**清本地会话并回登录页**——后端在改密时已撤销全部刷新令牌，
  留着已登录的样子会让用户被毫无征兆地踢出去。
- **路由守卫增强**：按 `route.meta.requiresAdmin` 隐藏菜单项并拦截直接访问
  （守卫在 0.0.7 已就位，本版补上真实的管理页）。

### 变更

- **测试连通性按钮放在表单内**（测当前填写的那份配置）而不是列表行上：后端 `admin/hosts/test`
  收的是**一份配置**而非 `hostId`，列表里没有凭据内容，拿不到凭据就测不出真结论。

---

## [0.0.12] — 镜像 UI（前端）

里程碑目标：可视化镜像管理。

### 新增

- **镜像列表** `views/ImagesView.vue`：表格（仓库、标签、镜像 ID、创建时间、大小、被引用数）+
  删除按钮；搜索走**服务端**（后端按仓库名 / 标签内存过滤，前端不再过滤一遍）；
  **不轮询**——镜像没有秒级漂移，重建表格只会打断阅读。
- **拉取对话框** `components/business/PullImageDialog.vue`：只留**一个**输入框，
  由 `splitImageReference()` 拆成仓库与标签——后端拒绝「已含标签又另传 tag」，
  分两个输入框会逼用户先理解后端的参数划分。
- **镜像详情页**：结构化投影展示（概览 / 构建配置 / 声明端口与卷 / 标签），**不是**裸 JSON 树——
  后端 inspect 已经是精选投影，逐字段排版比再包一层的 JSON 树更好读；原始 JSON 仅作可折叠的附录。
- 删除语义如实展示：被容器引用需强制删除（`1404`），且区分「只解绑了标签」与「真的删掉了镜像」。

---

## [0.0.11] — 容器 UI（前端）

里程碑目标：可视化容器管理。

### 新增

- **API 封装层** `src/api/container.ts`：RPC 风格方法封装（八个端点全覆盖）。
- **容器状态语义映射** `utils/container-status.ts`：状态→色调（含独立的**过渡态**一档）、
  端口展示、字节格式化。
- **`stores/host.ts`**：Pinia 当前主机选择。
- **顶栏 `HostSelector` 下拉组件** `components/business/HostSelector.vue`：主机作用域显示当前主机名 +
  状态点，全局作用域显示「选择主机」占位；列出可访问主机，**切换后停留在当前资源页**；
  写入 `useHostStore` 并跳转到 `/hosts/{新hostId}/{当前资源页}`；admin 可见「管理主机」入口。
- **容器列表** `views/ContainersView.vue`：表格（名称、镜像、状态、端口、创建时间）+
  操作按钮（启动/停止/重启/删除）+ 状态自动刷新（轮询 **5 秒**，页面隐藏时暂停，失败不打断轮询）。
- **创建容器对话框** `CreateContainerDialog.vue`：可视化创建表单（镜像 + 名称 + 端口映射 +
  环境变量 + Volume 挂载 + 重启策略 + 折叠的高级选项）；行式文本输入配**提交前校验与行号定位**；
  创建成功后可直接「立即启动」。
- **日志页** `ContainerLogsView.vue`：行数选择 + tail 加载 + 时间戳 / stderr 开关
  （自动滚动不在本版范围：后端不支持 `follow`，实时流归 0.3.0）。
- **详情页** `ContainerInspectView.vue`：分组卡片展示（基本信息 / 宿主配置 / 启动配置 / 挂载 /
  网络 / 环境变量 / 标签），**不是** JSON 树。

---

## [0.0.10] — 镜像 API（后端）

里程碑目标：镜像 CRUD + 拉取（含进度回传）。

### 新增

- **镜像端点**：
  - `POST /api/v1/hosts/{hostId}/images/list`（`all` / `search` / 分页；分页在服务端内存完成，
    `pageSize` 上限 100 且夹紧后的值回显）
  - `POST /api/v1/hosts/{hostId}/images/pull`（`image` + `tag?` + `platform?`）
  - `POST /api/v1/hosts/{hostId}/images/remove`（`imageId` + `force?`；**不**传 `noPrune`）
  - `POST /api/v1/hosts/{hostId}/images/inspect`（精选投影）
- 拉取失败有两条路径：「镜像不存在」由 SDK 抛异常 → `1401`；「仓库拒绕 / 网络中断」只在进度流里 →
  检查后转 `1402`。

### 说明

- 拉取进度用 `IProgress<JSONMessage>` 在服务端聚合，**0.0.10 不接入 SignalR**，
  仅在响应 `data` 一次性返回最终结果，进度信息写到 Serilog 日志。**SignalR 实时进度推迟到 0.3.0。**
- 四个端点均由集成测试覆盖（含真实守护进程上的拉取成功路径与「删除只解绑标签、镜像本体保留」语义）。

---

## [0.0.9] — 容器 API（后端）

里程碑目标：所有容器 CRUD 操作可通过 API 完成。

### 新增

- **容器端点**：
  - `POST /api/v1/hosts/{hostId}/containers/create`（`CreateContainerParameters` **精选字段**：
    镜像 / 名称 / 命令 / 入口点 / 环境变量 / 标签 / 端口 / 挂载 / 重启策略 / 自动删除 / 网络 /
    主机名 / 工作目录 / TTY / stdin）
  - `POST /api/v1/hosts/{hostId}/containers/list`（支持 `all` + 分页）——分页在服务端内存完成
    （Docker 无分页参数），`pageSize` 上限 100 且夹紧后的值回显
  - `POST /api/v1/hosts/{hostId}/containers/inspect`（精选投影：状态 / 启动配置 / 宿主配置 /
    挂载 / 网络）
  - `POST /api/v1/hosts/{hostId}/containers/logs`（`tail` / `since` / `timestamps` / `includeStderr`；
    **不含** `follow`，实时流归 0.3.0）
  - `start` / `stop` / `restart` / `remove`（前三个**幂等**：守护进程的 `304` 按成功处理；
    `remove` 不带 `force` 删运行中容器返回 `1404` / 409）
- 所有接口走 `IDockerHostAccessor` 获取客户端，按 host 粒度做可见性校验（`CurrentHostMiddleware`，
  0.0.8 落地）。
- `Docker.DotNet.Enhanced` 异常统一映射（`DockerErrorTranslator`，0.0.8 落地；0.0.9 补 `409` → `1404`）：
  `DockerApiException` → 通用 Docker 错误（1402）、`DockerContainerNotFoundException` /
  `DockerImageNotFoundException` → `1401`（资源不存在）、`TaskCanceledException` → `1403`（操作超时）。

### 说明

- 由集成测试覆盖（查询 11 条 + 变更 19 条），其中「生命周期」用例在真实守护进程上跑完
  create → start → stop → remove → 1401。

---

## [0.0.8] — Docker 主机抽象

里程碑目标：可注册多个 Docker 主机，按主机切换操作。

### 新增

- **新建 `hosts` 表**（id、name、type=socket|tcp、endpoint、auth_kind、auth_config_json、enabled、
  审计列）。`name` 不区分大小写唯一（`WHERE is_deleted = 0` 部分索引），且**不可改**——
  它是对外主机标识。
- 启动时若 `hosts` 表为空，插入默认主机：`name="local"`、`type=socket`、
  `endpoint="/var/run/docker.sock"`。
- **`IDockerClientPool`**（Singleton）：按**规范化连接串**池化客户端，同端点的主机名共享连接；
  空闲超时回收（`ReapIdleClients` + 后台任务）、`Invalidate`、进程退出统一释放；
  构建失败必定清条目（`Lazy<T>` 会永久缓存异常，不然一次失败后就再也无法重试）。
- **`IDockerHostAccessor`**（Scoped）：`hostId`（主机名）→ `hosts` 表 → 客户端；`Enabled=false` → `1302`；
  连通性测试用临时客户端 + Ping，**不经池**。
- **当前主机解析中间件**：由 Api 中间件从**路由值** `hostId` 解析当前主机（写入 Scoped `ICurrentHost`），
  路径形如 `/api/v1/hosts/{hostId}/...`，**无缺省值**。顺带完成存在性校验：未知主机在进控制器之前
  就变成 `1300` / 404，主机作用域端点无需各自重复校验。
- **资源可见性校验** `IResourcePermissionService.IsHostVisible`（0.0.9 落地：admin 可见全部，
  其余 fail-closed 拿空列表）；逐资源授权表仍排 0.3.0。
- **`IDockerClientFactory`**（无状态）：只用 `DockerClientBuilder` 创建客户端——socket 走
  `WithEndpoint(new Uri("unix:///var/run/docker.sock"))`，TCP 无认证走 `tcp://host:2375`，
  TCP + TLS 走 `https://` 端点（默认端口 2376）+ 客户端证书（三项路径均必填，缺 CA 等于接受任意服务端证书），
  TCP + Basic 走 `BasicAuthCredentials(user, pass, tlsEnabled)`（明文端点不阻断但记 Warning）。
  `Infrastructure` 项目额外引入 `Docker.DotNet.Enhanced.X509` + `Docker.DotNet.Enhanced.BasicAuth`。
- **主机管理 API**（admin 专用）：`POST /api/v1/admin/hosts/list`（并行探活）/ `create` /
  `{hostId}/update` / `{hostId}/delete` / `test`；新增错误码 `1303`（主机名已被占用）；
  更新时用**变更前**的连接描述让客户端池失效。
- **主机概览 API**（任意已认证用户）：`POST /api/v1/hosts/list`（当前用户可见的主机 + 连通状态）、
  `POST /api/v1/hosts/{hostId}/overview`（版本 / 资源计数 / 启动时间；离线返回 `status=offline` 而不是 502）。
- **用户管理 API**（admin 专用）：`POST /api/v1/admin/users/list`（分页）/ `create` / `delete` /
  `update-password`；自助改密需验原密码，管理员重置他人不需要；改密码与删号都撤销该用户全部令牌。
- **集成测试设施** `DockerFactAttribute`：探测宿主 `/var/run/docker.sock`，不可达时跳过用例，
  **不引入 Testcontainers**——本项目的被测依赖就是宿主 Docker daemon 本身。

### 变更

- **主机来源改为数据库**：`Docker:Hosts` 配置节删除；`Docker:ClientCache` 更名为 `Docker:Pool`。

### 说明

- 从本迭代起，容器 / 镜像 / Volume / Network 等新 API **必须配集成测试**，CI 卡（主机 / 用户 API 均已配）。
- 不设行覆盖率门槛。
- 主机管理界面已随 0.0.13 落地。

---

## [0.0.7] — 前端登录页与基础布局

里程碑目标：可视化登录体验 + 全局导航骨架 + UnoCSS / 主题基建。

### 新增

- **UnoCSS 接入**：`unocss` + `presetUno` + `presetTypography` + `presetIcons`（Iconify 按需）。
- **主题系统** `composables/useTheme.ts`：亮色 / 暗色 / 跟随系统 + 蓝 / 绿 / 紫主题色。
- **登录页** `LoginView.vue`：居中卡片 + Element Plus 表单 + 主题切换按钮（已接
  `POST /api/v1/auth/login`，失败按**错误码**映射 i18n，不透出后端中文 message）。
- **主布局** `layouts/AppLayout.vue`：顶栏 64px + 左侧 200px 可折叠侧边栏 + 内容区；移动端侧边栏变抽屉。
  - **两段式信息架构**：全局作用域（`meta.scope='global'`：总览 / 全局设置 / 主机管理 / 用户管理）
    与主机作用域（`meta.scope='host'`：`/hosts/:hostId/{dashboard,containers,images,volumes,networks}`）
    使用**不同侧边栏菜单**。
  - **主机作用域进入 URL**（`/hosts/:hostId/**`），可深链 / 可分享；路由守卫负责把 `hostId`
    同步到 `useHostStore`，未知 hostId 重定向 `/overview`。
  - 主机作用域也进入 API 路径（`/api/v1/hosts/{hostId}/...`），全局作用域端点不含该段。
  - 侧边栏菜单**从路由表自动派生**（按 `meta.scope` / `showInSidebar` / `requiresAdmin` 过滤），
    新增路由无需改布局。
- **总览页** `OverviewView.vue`：登录后的默认落地页。**长条形**列出所有可见主机
  （名称 / 类型 / endpoint / 版本与运行时长 / 四类资源计数 / 状态 + 行内按钮）+ 总数与在线数摘要；
  **整行可点**进入主机作用域。主机按 `order` 分组展示（生产 / 测试 / 本机 / 未分组），无成员的分组不渲染。
  行内操作（编辑（admin）/ 查看详情 / 状态图）均 `@click.stop` 防止触发整行导航。
- **主机状态页** `HostStatusView.vue` + `HostStatusPanel.vue`：运行状态（在线/离线）、运行时长、
  四类资源计数与 CPU/内存占位（真实数据待 SignalR 0.3.0）。总览卡片的「详情 / 状态图」按钮与该页
  **复用同一组件**。
- **顶栏组件** `AppBreadcrumb.vue`（层级由 `utils/breadcrumbs.ts` 纯函数派生，已被单测覆盖）、
  `UserMenu.vue`（用户名 / 角色标签 / 登出）。
- **侧边栏组件** `AppSidebar.vue`（桌面侧栏 + 移动端抽屉，只负责容器与断点）+ `SidebarNav.vue`
  （菜单内容，两处复用）。
- **国际化**：vue-i18n，`locales/zh-CN.json` / `locales/en-US.json`；key 用 dotted 命名，缺失 fallback 英文。
- 路由守卫实现未登录跳 `/login` 与 `requiresAdmin` 拦截。

### 变更

- 顶栏与侧边栏从内联在 `AppLayout.vue` 里的实现拆成独立组件（`layouts/`），并补上面包屑。
- **`useHostStore` 不做持久化**：hosts / groups 来自后端，快照必然陈旧；`currentId` 由 URL 驱动，
  无需 localStorage（旧版本的持久化键在启动时一次性清理）。
- **语义颜色令牌收口**：`main.css` 增表面 / 文本 / 状态 / 层级四类令牌，`uno.config.ts` 映射为
  `surface-*` / `content-*` / `line-*` / `status-*`；组件不再写 `gray-500`、`bg-white dark:bg-gray-800`
  这类亮暗双写（此前 `app-card` 写死的 `dark:bg-gray-800` 与早已存在的 `--surface-card` 就是漂移源头）。
- **缺失态重新设计**：主机落地页原先在数据缺失时整块不渲染、只剩四个裸「—」，看起来像加载失败；
  现改为信息卡恒显 + 弱化占位 + 原因说明 + 离线提示条。
- **概览 / 状态两页职责切分**：此前逐字重复，现概览 = 身份与技术信息、状态 = 实时指标与事件。
- **主机侧边栏菜单顺序**调整为「状态 → 容器 → 镜像 → 数据卷 → 网络 → 编排 → 事件」，
  与能力交付节奏对齐（编排依赖容器/镜像且排 0.4.0）。
- **占位页改为「功能预告」** `PlaceholderSection`：图标 + 版本徽章 + 能力清单 + 排期依据，
  不再是「空白卡片 + 一句话」。
- 清理：删除重复的「详情」按钮（与「状态图」绑同一函数）、删除死代码 `HostStatusPanel.vue`、
  侧栏主机身份卡改分组标题（主机名从 4 处收敛为 2 处）、分组描述由硬编码中文改为 i18n。

---

## [0.0.5] — 认证完善 + 前端基础设施

里程碑目标：完整认证闭环 + 前端可对接后端。

### 新增

- **`POST /api/v1/auth/refresh`**：轮换刷新令牌（旧令牌立即作废，并用 `replaced_by_token_hash` 标记令牌族）。
- **`POST /api/v1/auth/logout`**：撤销当前 refresh token（复用令牌表的 `revoked_at`，**不**另建黑名单表）。
- **安全响应头中间件**：`X-Content-Type-Options=nosniff`、`X-Frame-Options=DENY`、
  `Referrer-Policy=no-referrer`。用 `Response.OnStarting` 挂载——异常处理中间件的 `Response.Clear()`
  会清掉直接赋值的响应头。
- **密码强度校验工具类** `PasswordPolicy`（≥10 字符、含字母 + 数字或符号），
  登录与初始管理员引导共用。
- **前端工作区** `src/Whalefleet.UI/`：Vite + Vue 3 + TypeScript + Element Plus 骨架；
  `vite.config.ts` 配置 `/api` 与 `/hubs` 代理到 `http://localhost:8080`。
- **Pinia `useAuthStore`**（user、accessToken、refreshToken 持久化到 localStorage）。
- **Axios 实例 + 拦截器**：注入 Authorization；`401` 时静默刷新一次并重发原请求
  （**并发 401 只刷一次**——刷新令牌是一次性的，多刷会被服务端判定为重放）。
- **Vue Router 全局守卫**：未登录跳 `/login`（另含 `requiresAdmin` 拦截与 hostId 同步）。
- 登录页接 `POST /api/v1/auth/login`；失败文案按**错误码**映射 i18n。

### 修复

- 集成测试宿主改用**非池化连接**（`Persistence:Pooling=false`）。原先清理时调用的
  `SqliteConnection.ClearAllPools()` 是进程级操作，会释放并行运行的其他测试正在用的连接句柄，
  实测造成约 1/14 概率的偶发失败。

### 说明

- **轮换语义**（完整表见 [docs/api-spec.md](docs/api-spec.md)）：已被轮换的旧刷新令牌再次使用 →
  判定为重放 → 撤销该用户**全部**刷新令牌；登出后的令牌再次使用 → 仅拒绝，不牵连其他会话。

---

## [0.0.4] — 认证 API（核心）

里程碑目标：可通过 API 登录拿到 JWT。

### 新增

- **JWT access token**（HS256，15 分钟）+ **refresh token**（随机 32 字节 base64url，7 天）。
  - 声明 `sub` / `unique_name` / `role` / `jti` 集中定义于 `Application/Abstractions/JwtClaimNames`
    （签发方在 Infrastructure、验证方在 Api，声明名必须有一处共同定义）。
  - 密钥经 `Jwt:Secret` 配置，**缺失或不足 32 字节则启动失败**（`ValidateOnStart`）。
  - 刷新令牌落库的是 **SHA-256 哈希**（base64url，属持久化契约）；原文只在签发那一刻返回给客户端。
- **`POST /api/v1/auth/login`**：校验用户名 / 密码，返回 access + refresh。
  - **防用户名枚举（两层）**：用户名不存在与密码错误返回**完全相同的错误码与文案**；
    且用户不存在时也执行一次等价成本的**哑元校验**，使响应耗时不可区分。
  - **失败计数**：按 IP + 用户名 **5 次/分钟 → 429**；窗口自首次失败起算，登录成功则清零。
- **`POST /api/v1/auth/me`**：需认证；返回当前用户摘要（前端刷新页面时校验持久化的 token）。
- **`[Authorize]` 中间件** + 角色检查 `[Authorize(Roles = "admin")]`。Api 层 `ICurrentUser`
  实现接管 Infrastructure 的占位实现（`services.Replace`）。鉴权失败同样返回统一 `ApiResult`，
  并区分 `1100`（未认证）/ `1101`（**已过期**）/ `1500`（无权限）。
- **初始管理员引导**（**原属部署迭代，因鸡生蛋问题提前到本版**）。

### 说明

- 集成测试覆盖：登录成功、凭据错误、枚举不可区分、限流锁定与窗口过期、
  令牌过期 / 篡改 / 异密钥 / 格式非法。
- **已知缺口（留给 0.0.15）**：反代后 `Connection.RemoteIpAddress` 是代理 IP，
  而本项目尚未启用 `ForwardedHeaders`（需配合可信代理白名单）——
  不加会将所有用户的登录失败算进同一个限流桶。

---

## [0.0.3] — 数据层与密码安全

里程碑目标：用户表可创建，密码可哈希校验。

### 新增

- **SQLite 初始化**（`/app/data/whalefleet.db`，目录不存在自动创建），开外键与 `busy_timeout`；
  Dapper 接入，仓储层抽象 `IUserRepository`（另含 `IHostRepository` / `IRoleRepository` /
  `IRefreshTokenRepository`）。
- **架构迁移 `0001`–`0004`**：启动时按 `PRAGMA user_version` 执行内嵌脚本，版本须从 1 连续，
  否则启动失败。
- **`users` 表**（id、username、password_hash、role_id、审计列）；列名遵循「属性名 ↔ 列名同名、
  仅大小写风格不同」的审计列约定（`creation_time` / `creator_id` / `last_modification_time` /
  `last_modifier_id` / `is_deleted` / `deletion_time` / `deleter_id`）。
- **Dapper 直接物化充血实体**：实体给**非公开无参构造**即可，**不建 Row DTO、不加
  `InternalsVisibleTo`**；须开启 `MatchNamesWithUnderscores` 才能把 `creation_time` 映射到 `CreationTime`。
- **`RepositoryBase`** 在插入 / 更新时调用 `MarkCreated` / `MarkModified` 写入审计
  （值取自 `TimeProvider` 与 `ICurrentUser`）。
- **实体审计与软删除**：`Core/Common/Auditing/` 提供 9 个只读细粒度接口与 3 级基类
  （`CreationAuditedEntity` → `AuditedEntity` → `FullAuditedEntity`，**无** `SoftDeleteEntity`）；
  审计属性一律 `internal set` + `public` 的 `MarkCreated` / `MarkModified` / `Delete` / `Restore` 写入；
  时间一律 **UTC** 且**显式传入**（实体不自取）；`TUserKey` **无类型约束**（支持 `Guid` / `long` /
  `string`），「无用户」用**默认值哨兵** `default(TUserKey)`。
- **角色从 `UserRole` 枚举改为 `roles` 表** + `IRoleRepository`（`users.role_id` 外键），
  内置 admin / operator / viewer / auditor 四角色（默认 auditor）。
  默认角色**至多一个**，靠部分唯一索引 `ux_roles_default` 在数据层保证，**不靠代码**；
  内置角色不可删，实体 `Retire()` 抛 `InvalidOperationException` + 仓储 `WHERE is_built_in = 0` 双重兜底。
- **`ICurrentUser`** 抽象（`Application/Abstractions/`）+ `RepositoryBase` 注入
  `ICurrentUser` + `TimeProvider` 完成审计填充（0.0.4 认证落地后由 Api 层实现替换占位实现）。
- **Argon2id 密码哈希**（`Konscious.Security.Cryptography.Argon2`，MIT），禁止明文落库与日志。
  - PHC 编码串（`$argon2id$v=19$m=…,t=…,p=…$salt$hash`）由 `Core/Auth/PasswordHash` 值对象负责编解码，
    算法参数随哈希一同持久化，因此日后调参不会让旧哈希失效。
  - `IPasswordHasher`（`Application/Abstractions`）抽象 + `Argon2PasswordHasher`
    （`Infrastructure/Security`）实现；摘要比较一律走 `CryptographicOperations.FixedTimeEquals`。
  - 参数经 `Security:Argon2` 配置，越界在**启动时**失败（`ValidateOnStart`，
    直接复用 `PasswordHash` 的校验规则避免两处漂移）。

### 说明

- 设计细节见 [docs/architecture.md](docs/architecture.md) 的「实体审计与软删除」
  与「持久化实体物化约定（Dapper）」。
- 依赖方向守卫：`Whalefleet.Core` 仍**无任何 NuGet 依赖**。

---

## [0.0.2] — 后端基础设施

里程碑目标：所有后续接口的统一基线。

### 新增

- **统一响应模型**：`ApiResult`（无 data）与 `ApiResult<T>`（含 data）+ 静态工厂方法
  `ApiResults`。失败响应**必须显式含 `"data": null`**（勿开 `WhenWritingNull`）。
- **错误码分层**：常量、语义分类与默认文案放 `Application/Common`
  （`ErrorCodes` / `ErrorCategory` / `ErrorCodeCatalog`）；「语义分类 → HTTP 状态码」放 `Api/Errors`
  （`ErrorHttpStatus`）。既避免应用层依赖 Web 概念，又让两侧都能被单测。
- **全局异常处理中间件**：`BusinessException` 自带错误码；
  `DomainException` / `ArgumentException` / `JsonException` / `BadHttpRequestException` → 1000/400、
  `KeyNotFoundException` → 1001/404、`TaskCanceledException` → 1003/408、兜底 9999/500。
  中间件用 `HandleExceptionAsync` 返回 bool，使 `throw;` 留在 `catch` 块内。
- **中间件管道顺序**（勿动）：`ExceptionHandlingMiddleware` → `UseSerilogRequestLogging`
  （异常降级 Debug）→ `UseStatusCodePages` → `UseCors` → endpoints。
- **CORS 白名单策略**：经 `IConfigureOptions<CorsOptions>` **延迟注册**——DI 注册期直接读配置会读到
  尚未定型的值，导致静默地不注册任何策略。默认 `Cors:AllowedOrigins` 为空数组，
  即**不发出任何 `Access-Control-*` 响应头**（等价于完全拒绝跨域）；非法 origin（含尾斜杠）启动即失败。
- **JSON 编码策略**：`ApiJsonDefaults.Apply(options)` 是**唯一入口**，MVC 与错误响应写出共用，防止两处漂移；
  编码器 `JavaScriptEncoder.Create(UnicodeRanges.All)` 让中文原样输出而非 `\uXXXX` 转义。
- **Serilog**：控制台 + 滚动文件；生产 `/app/data/logs/app-.log`，开发 `logs/app-.log`
  （按天滚动 / 保留 14 个 / UTF-8 无 BOM）；5xx 记 Error 含堆栈，4xx 记 Warning **不含**堆栈；
  请求日志在「发生异常」的请求上降级为 `Debug`。
- **配置**：硬编码的 `UseUrls("http://+:8080")` 改为 `appsettings.json` 的 `Urls` 键，使 `TestServer`
  能接管监听。注意 `Urls` 优先级**高于** `ASPNETCORE_URLS`，改端口用 `--urls` 或 `URLS`。
- **集成测试载体**：`tests/Whalefleet.IntegrationTests/`（含 `WebApplicationFactory`）提前落地，
  用于验证 HTTP 管道级契约。
- **环境变量命名规范**：`__` 分隔层级、数字下标表达数组；键比较大小写不敏感，
  新增配置项统一用全大写下划线风格。
- **依赖升级**：`Microsoft.Extensions.*` → 10.0.12；Serilog →
  AspNetCore 10.0.0 / Settings.Configuration 10.0.1 / Sinks.File 7.0.0 / Sinks.Console 6.1.1；
  测试栈 → Microsoft.NET.Test.Sdk 18.10.0 / xunit 2.9.3 / xunit.runner.visualstudio 4.0.0 /
  coverlet.collector 10.0.1。
- **`.editorconfig` 清理**：统一 LF（与 `.gitattributes` 的 `* text=auto` 一致）、末尾换行统一、
  清除 6 个重复键、为 `[*.sh]` 显式声明 LF 以保护 `scripts/backup.sh`。

### 变更

- **`Docker.DotNet.Enhanced` 3.x → 4.3.3（破坏性）**：4.x 移除了 `DockerClientConfiguration` 与
  `IDockerClient`，`DockerClientFactory` 改为 `DockerClientBuilder.WithEndpoint(...).Build()`；
  新增 `DockerClientFactoryTests`，把「能编译」升级为「能构造出可用客户端」。

### 说明

- **例外——`FluentAssertions` 停留在 7.2.2**：8.x 起改为 Xceed 商业授权，7.x 是最后一个 Apache-2.0 版本；
  本项目为 MIT 开源且不引入商业授权依赖，故不跟进 8.x。

---

## [0.0.1] — 项目骨架 + CI 骨架

里程碑目标：可构建、可运行最小后端 + CI 流水线雏形。

### 新增

- **4 个分层项目**（`Whalefleet.slnx`）：
  - `src/Whalefleet.Core/`——业务实体、值对象、枚举。**仅依赖 BCL，不引用任何 NuGet 包**。
  - `src/Whalefleet.Application/`——用例编排、DTO、外部资源接口抽象（如 `IDockerClientFactory`）。
    依赖 Core。
  - `src/Whalefleet.Infrastructure/`——第三方 SDK 适配（`Docker.DotNet.Enhanced`）、仓储实现、
    外部服务集成。依赖 Core + Application。
  - `src/Whalefleet.Api/`——ASP.NET Core 10 宿主进程，托管 Controllers / SignalR Hubs /
    静态前端。依赖 Application + Infrastructure。
  - `tests/Whalefleet.UnitTests/` 与 `tests/Whalefleet.IntegrationTests/`。
- **`Directory.Packages.props` 启用 Central Package Management**，所有 NuGet 版本号集中此文件。
- **外部资源接口放在应用层**（非领域层），例如 `IDockerClientFactory` 定义在
  `Application/Abstractions`。
- **`Directory.Build.props`**：`TargetFramework=net10.0`、`LangVersion=latest`（C# 14）、
  可空引用类型开启、隐式 using 开启、**`TreatWarningsAsErrors=true`（CI 必须零警告）**。
- **`GET /healthz`** 返回 200 OK。此端点用 Controller 风格，刻意区别于后续 RPC 风格的
  `POST /api/v1/...` 端点；其成功响应是**裸 JSON，不包 `ApiResult`**。
- **Serilog** 控制台输出（JSON 配置 + `ReadFrom.Configuration`）。

### 变更

- 原规划的「三项目（Api / Core / Infrastructure）」经评审后调整为 **4 个分层项目**。

### 说明

- **CI**：`.github/workflows/ci.yml` 在 push / pull_request 到 `main` / `dev` 时触发，
  步骤为 `dotnet restore` → `dotnet build -c Release`（warn-as-error）→ `dotnet test -c Release`，
  NuGet 包缓存 key 基于 `*.csproj` + `Directory.Packages.props`。
- **DDD 战术模式已于 0.0.2 移除**：`Domain` 更名为 `Core`，聚合根 / 领域事件 / `DomainException`
  全部删除，保留分层依赖方向与充血模型。详见 [docs/architecture.md](docs/architecture.md)。
- 注意 `0.0.6` 是**空号**，见本文件顶部说明。

[Unreleased]: https://github.com/blue-cloud-net/whalefleet/compare/v0.0.14...HEAD
