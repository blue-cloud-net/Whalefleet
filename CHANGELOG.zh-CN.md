# 变更记录

本文件记录本项目所有值得注意的变更。

格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

> **语言 / Language**: [English](CHANGELOG.md) | **简体中文**
>
> **本文件与英文版均由人工维护，自动化永不写入。** [Release
> Please](https://github.com/googleapis/release-please) 配置里开了 `skip-changelog`，
> 它开出的发布 PR 只含版本号变更（`.github/release-please-manifest.json` 与
> `src/Whalefleet.UI/package.json`）。**必须在合并该 PR 之前于其中手写新版本的小节（中英双语）**
> —— 合并该 PR 的动作正是产生 tag 的动作，两者之间没有任何时间窗口。
> 完整流程见 [AGENTS.md](AGENTS.md)。
>
> `0.0.1` – `0.0.15` 的条目汇总自 [docs/roadmap.md](docs/roadmap.md) 中标记为已完成的里程碑。

> **关于 `0.x` 的说明**：每个 `0.x` 版本都是内部开发里程碑。不打 tag、不向 GHCR 发布，
> 版本之间 API 可能发生破坏性变更。首个对外版本是 `1.0.0`，此前先出 `1.0.0-rc.N`。
> `0.0.6` 已改编号为 `0.0.15`（部署底座与发布流水线），
> 因此 `0.0.6` 是**空号**——不对应任何里程碑。

---

## [Unreleased]

进行中。下一个里程碑是 `0.1.0`——授权基座与 MVP 收口
（各版本的条目在此以内部草稿形式累积，正式发布前会重写进 `1.0.0` 的发布记录）。

### `0.1.0`——授权基座与 MVP 收口（内部草稿）

#### 新增

- **权限目录**：**静态封闭集合**，按 `domain.level` 命名（`host.read`、`container.write`、
  `user.manage` 等），分 Global 与 Host 两类作用域。
  - 它**刻意不建表**。「本版本有没有这个功能」是编译期事实，为它建表只会让运行时可配的角色
    逐渐偏离代码真正能强制的范围。
  - `PermissionScope` 现在就**预留 Container 一档**，但本迭代只开放全局与主机两条分配路径——
    容器级作用域见 `0.12.0`。
- **`role_permissions` 表**（迁移 `0006`）。角色现在**携带**自己的权限集——只有角色名是决定不了
  什么的，这个集合才是「运维」真正成为运维的原因。
  - 权限以 `domain.level` **文本**存储，每个（角色，权限）组合一行：
    新增一个权限是「一条目录项 + 一行数据」，而不是一个所有存量行都得跟着拓宽的新列。
  - **内置**角色的权限集**不可修改**。管理员可以自由增删改自己的角色，但
    `admin` / `operator` / `viewer` / `auditor` 的语义是随迁移种入的——
    一个能被清空权限集的角色，已经不是它名字所声称的那个角色。
  - `admin` **显式存入全部权限行**，**同时**仍按角色名短路。这些行让权限矩阵在界面上是诚实的；
    短路则保证后续版本新增的权限不会静默地漏给管理员。
- **`user_role_assignments` 表**（迁移 `0006`）——用户与角色的关联改为**多对多**且**带作用域**。
  `host_id` 可空：`NULL` 是全局分配，否则该分配只作用于那一台主机。
  - **全局**分配贡献角色的 Global 权限；**主机**分配只在该主机上贡献该角色的 Host 域权限，
    其 Global 权限被忽略——否则把 `admin` 分配到某台主机就等于一次静默的系统级提权。
  - 同一人在同一台主机上可持有多个角色，有效权限取**并集**。唯一索引是部分索引
    （`WHERE is_deleted = 0`），撤销后可重新分配；键是 `(user_id, role_id, host_id)` 而非二元组。
  - `users.role_id` **废弃**。角色不再是用户「拥有」的东西，而是被「分配」给用户的东西。
- **`SqliteRoleRepository` 改为读写权限**，并新增 **`SqliteUserRoleAssignmentRepository`** 注册进 DI。
  两者写路径统一走 `RepositoryBase`，审计字段与软删除行为因此与其他实体一致。
- **角色管理端点**：`api/v1/admin/roles` 下的 `list` / `create` / `update` / `remove` /
  `set-default` / `set-permissions`。删除仍有**有效分配**的角色会返回 `1512` 而**不是**级联撤销：
  级联会悄悄收回没人要求收回的访问权，而「这个角色还在用」是管理员能据以行动的问题。
- **角色分配端点**，分主机中心与用户中心两套：
  `api/v1/admin/hosts/{hostId}/roles/{list,assign,revoke}` 与
  `api/v1/admin/users/{userId}/roles/{list,assign,revoke}`。
  - 两套都保留是因为它们回答的是**两个不同的问题**——「谁能访问这台机器」与「这个人拥有什么」——
    需要拿一个列表去过滤另一个的界面，只会渲染出半真半假的结果。
- **鉴权改为每次请求从库解析**，并在请求内缓存。`[Authorize(Roles = "admin")]` 换成
  按权限判定的 `AuthorizationHandler`。
  - 带作用域的分配**根本装不进单个 JWT claim**：同一人可能在 A 主机是 `operator`、在 B 主机是
    `viewer`，任何 claim 快照都只是一个残缺且终将过时的答案。
  - 顺带的好处：角色与权限的改动**立即生效**，不再要等 access token 过期。
  - `role` claim 仍然签发——界面要展示角色、据此隐藏菜单项——但它**不再参与授权判定**。
- **新错误码 `1510`–`1515`**（角色不存在、角色名已占用、角色仍有分配、内置角色受保护、
  未知权限、角色分配不存在），已同步进错误码目录、HTTP 状态映射、两处守卫测试、
  API 规范文档与前端错误码表。
- **容器与镜像的批量删除**（`POST .../containers/batch-remove` 与 `.../images/batch-remove`）。
  单次最多 **100** 个 id，裁剪空白、去重并保留原始顺序。
  - 返回 `200` + 逐项结果，而不是单一状态码：Docker 主机繁忙时半数条目失败属常态，
    把它们压成一个 4xx/5xx 会连带丢掉已成功的那部分。全部失败时另有 `IsTotalFailure` 标记，
    调用方可据此弹提示，而不是让用户面对「点了没反应」。
  - 镜像删除按 `force` 拆成**两批**下发。批量请求只带一个 `force` 标志，
    因此「能强删的强删、运行中的拒绝」只能靠客户端拆批表达，除非再加一个端点。
- **角色管理页**（角色 → 权限矩阵，内置角色只读渲染），以及主机中心的「谁能访问这台机器」
  与用户中心的「这个人拥有什么」两个视图。登录与 `auth/me` 现在同时返回用户的角色与
  其**全局**有效权限集，菜单项与操作按钮据此显隐。
- **`examples/reverse-proxy/`**——nginx（`whalefleet.conf` + 代理参数 include）与 Traefik
  （纯标签）两套配置，并在 `.env.example` 中补齐 `REVERSEPROXY__ENABLED` / `KNOWNPROXIES` /
  `KNOWNNETWORKS`。转发头的处理必须与设置这些头的反代相匹配；配错的表征是在登录限流开始把
  所有用户锁进同一个 `X-Forwarded-For` 桶之前，毫无异常。

#### 变更

- **`ResourcePermissionService` 改为从角色解析**，不再从逐用户授权行判定。它的逐主机谓词
  已换成 `FilterVisibleAsync`（**只查一次**该用户的分配后在内存里过滤——逐主机查询会把一个主机列表
  变成 N 次往返），而可见性本身也从「存不存在带 read 位的行」变成「这个用户在该主机上的有效权限集
  是否包含 `host.read`」。管理员仍短路。
- **`User` 不再携带 `RoleId`**。创建用户现在改为建立一条默认角色（`auditor`）且 `host_id = NULL`
  的分配；「至少保留一名管理员」统计的是 `admin` 的全局分配数，而不是指向它的用户数。
- **`ICurrentUser.Role` 变为 `ICurrentUser.Roles`**，access token 携带**多个**角色 claim 而非一个。
  它此后只承担展示职责。

> **`0.1.0` 的部署验收尚未完成**：socket-proxy 形态的
> 创建 → 启动 → 停止 → 删除 + 拉取闭环需要在真实 `docker compose up` 下跑一遍，CI 不覆盖组合栈。
>
> **迁移口径**：`0006` 建出授权相关的新表并调整 `users`，**不搬运历史授权数据**。
> 本迭代从未产出正式发布的 tag，因此没有需要迁移的生产数据。

---

## [0.0.15] — 部署底座与发布流水线

里程碑目标：让 `docker run` 一行启动即可登录，并让产物具备可发布性。

### 新增

- **多阶段 `Dockerfile`**（`Node → .NET SDK → .NET runtime`）：运行期镜像只装发布后的 Api 程序集
  与前端产物——无 SDK、无 Node，**且不含源码**。直挂 socket 的部署形态下，
  镜像里多一份源码就多一份攻击面，而单容器漏洞等于宿主机沦陷。
- **非 root 运行用户 `app`（UID 10001）**；`/app/data` 是唯一可写路径，
  且**只读根文件系统已实测可运行**。
  - `.NET 8+` 的 `aspnet` 基础镜像**自带** `app` 用户，但 UID 是 1654——
    `groupadd --gid 10001 app` 会以 `group 'app' already exists` 直接失败并把镜像构建卡死。
    镜像改用 `groupmod` / `usermod` 改写该账号。坚持写死 10001 是因为挂载宿主目录时生效的是
    **数字** uid，README 里的 `chown 10001:10001` 必须与镜像一致。
  - 运行期镜像额外装了 `curl` 与 `sqlite3`。没有 `curl`，`HEALTHCHECK` 只能退化成
    「进程还在」——而那恰恰挡不住「进程活着，但 SQLite 打不开」；没有 `sqlite3`，
    README 让人在容器内跑的备份脚本会直接「未找到命令」。
- **前端产物同源托管**：由 Api 进程托管 `wwwroot`（`Spa` 配置节，生产启用）。
  启用但找不到产物目录时**启动即失败**——交付一个「能启动但打开是 404」的容器，比启动失败糟得多。
  - 刻意**不用 `MapFallbackToFile`**：它把所有未匹配路由都导向 `index.html`，包括打错的 `/api`
    路径。症状是「接口返回 200 + HTML，日志里全是成功」，前端表现为 JSON 解析失败，
    几乎不可能靠读日志定位。因此 SPA 回退显式排除 `/api` 与 `/healthz`。
  - 整套挂在 `UseCurrentHost` **之前**：前端路由 `/hosts/{hostId}/…` 与接口
    `/api/v1/hosts/{hostId}/…` 同前缀，而 `UseCurrentHost` 会把任何请求的 `hostId` 路由值
    当主机查库。放在它之后，每次刷新深链都先抛「主机不存在」。
- **健康检查接入 SQLite 连通性**（`SELECT 1`），失败返回 503 并附异常信息。
  此前 `/healthz` 无条件返回 200——挂进容器后等于一个永远绿的探针：data 卷没挂上、磁盘满、
  文件权限错，探针照样说健康，而每个业务端点其实都在 500。
  - 刻意**不查 Docker daemon**：主机停机维护时管理界面仍需可用，把它算进健康检查会导致容器被
    orchestrator 反复重启，而重启并不能让 Docker 恢复。
  - ⚠️ 注册必须走 `AddCheck(name)`：直接注册 `IHealthCheck` 会以**实现类全名**作为注册项名字，
    而 `HealthCheckService` 按名字索引结果——结果是 `/healthz` 永远查不到该项，只能回退成 unhealthy。
- **反代场景的 `ForwardedHeaders` + 可信代理白名单**（`ReverseProxy` 配置节，默认关闭）。
  不配的后果：所有用户的登录失败落进同一个限流桶，5 次失败后全体用户在一分钟内都无法登录。
  三条约束：默认不启用（`X-Forwarded-For` 由客户端可控）；开了开关却没有白名单直接启动失败；
  地址写错抛异常而非静默忽略。网段解析用 `System.Net.IPNetwork` 而非已标记过时的
  `HttpOverrides.IPNetwork`（ASPDEPR005）。
- **`docker-compose.yml` 两种部署模式**（用 `profiles` 隔离）：socket-proxy（默认、推荐）
  与直挂 socket。
  - socket 挂载加 `:ro` **不会**降低风险：权限由守护进程的 API 决定，`:ro` 只能防止误写 socket
    文件本身。
- **`scripts/backup.sh`**：基于 `sqlite3 .backup` 的**在线**备份——直接 `cp` 拷到的是某一瞬时的
  文件状态，正在写事务时可能拷出撕裂的文件，而这种损坏要到恢复时才暴露。
  每次备份后跑 `integrity_check`，并按文件名排序轮转。
- **Release Please + 镜像发布**：`.github/release-please-config.json` 从 Conventional Commits
  推导版本号；`release.yml` **仅**在 tag 时构建推送，且推 GHCR 前先冒烟——
  tag 一旦推送就已被他人引用，此时才发现起不来，撤回只会制造混乱。
- **`.dockerignore` 扩充**。**必须**排除 `.env*`：compose 用的 `ADMIN__PASSWORD` /
  `JWT__SECRET` 通常就在那里。

### 变更

- **项目更名为 `Whalefleet`**（原 `Docker.Manager`），含镜像名。
- **零配置部署**：`JWT__SECRET` / `ADMIN__PASSWORD` 降级为*可选覆盖项*。缺失时在首次启动自动生成
  并持久化到数据卷（`jwt.key` / `initial-admin.txt`，均 0600）；显式配置则以你给的为准，
  因此多实例共用密钥、密钥轮换、测试注入的口子都保留。
  - JWT 密钥**必须**落盘而非每次重新生成。密钥一变，所有已签发的 access token 立即失效，
    而存在库里的 refresh token 要用**旧密钥**去换——于是「容器重启 = 全员静默登出」，
    且**无任何报错**。
  - 自动生成的密码只写文件、**绝不进日志**：日志会被采集器上报到集中式系统，打印等于扩散凭据。
    有一条测试专门钉住这一点。
  - `ADMIN__AUTOBOOTSTRAP=false` 可彻底关闭管理员自举，用于数据卷由外部预置的场景
    （账号已随备份恢复 / 由运维脚本写库）——否则自举会与既有 `admin` 抢用户名，
    撞上 `users.username` 唯一索引。
  - `direct` 模式不再写 `container_name`。compose 在**解析配置时**就校验该名字全局唯一，
    与 `proxy` 共用会让 `docker compose --profile direct up -d` 直接失败、一个容器都起不来。
    `proxy` 保留该名字——文档与 `backup.sh` 大量依赖 `exec whalefleet`。
  - 直挂 socket 模式否则会报 **1301**：宿主机 socket 是 `srw-rw---- root:docker`
    （GID 常为 983），而镜像内 `app` 用户不在该组。用
    `--group-add $(stat -c '%g' /var/run/docker.sock)`。
  - `.gitignore` 补上 `.env` 规则。它原本一条都没有，而 `.dockerignore` 只挡镜像不挡 git——
    `cp .env.example .env` 后一次 `git add .` 就会把密钥提交进去。
- **CI 增加镜像构建与冒烟**（`/healthz`、首页含 `<div id="app">`、非 root）。
  只构建不推送——推送是 `release.yml` 在 tag 时的事。
  之所以要放进 CI：`Dockerfile` 里的前端构建在 .NET 的 build-and-test job 里**完全不会被触发**，
  而它恰是最容易坏的一环（Node 版本、lockfile、产物目录约定）。
  曾出现过「后端测试全绿，但镜像根本构建不出来」而 CI 完全无感。
- Actions 升到 Node 24 运行时，并接入 **Dependabot**（盯 `github-actions`，每周开 PR，
  minor/patch 合并成一个）。这些 action 都要求 Actions Runner ≥ v2.327.1——
  GitHub 托管的 `ubuntu-latest` 已满足；**自托管 runner 必须先升级**。

### 修复

- `release.yml` 有一个重复的 `name` 键。
- `.dockerignore` 用 `**/data`，把 `src/Whalefleet.UI/src/mocks/data/`（前端 mock 源码）也一起
  排除了，构建以 `Could not resolve "./data/hosts"` 收场，且**无任何提示指向 .dockerignore**。
  现在只排除仓库根的 `/data` 运行期数据目录。

### 安全

- 备份脚本会把 `jwt.key` 一并拷成 `<备份名>.db.jwt.key`。恢复备份时若漏掉它，程序会生成新密钥
  → 旧令牌全废、所有在线用户被静默登出，**无任何报错**。
- 恢复的备份需要 `docker exec -u 0 whalefleet chown 10001:10001 /app/data/jwt.key
  /app/data/whalefleet.db`：`docker cp` 会把文件落成**执行 cp 的宿主机用户**的 UID（常见 1000），
  而 `jwt.key` 是 0600 → 镜像内 `app` 用户读不到。已写进 `scripts/backup.sh` 的头部注释。
- `dotnet format` 接入 CI 但**暂不阻断**：仓库存在 **328 处**违规且非本版引入
  （`.editorconfig` 从 0.0.1 起就要求成员访问加 `this.`，代码里有 160 处没加）。
  一次性修正要动 40+ 个文件，而 `IDE0049`（未使用字段）**会删代码**——在依赖 Dapper 约定映射的
  前提下，不适合由格式化工具静默完成。违规数与分类写进 job summary 作为持续记录的基线。

### 说明

- `0.0.x` 版本不向 GHCR 发布，也**完全不打 tag**——它们只是迭代编号。
  首个对外镜像随 `1.0.0` 发布。
- `SECURITY.md` / `CONTRIBUTING.md` / `CODE_OF_CONDUCT.md` 曾在本版起草，随后又被删除。
  仓库有意不提供这三份文件，任何文档都不得链接它们。
- 发布流水线在本版之后经过重新设计（首个对外发布由 `0.1.0` 改为 `1.0.0`）。
  当前流程见 [AGENTS.md](AGENTS.md)。

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
  （自动滚动不在本版范围：后端不支持 `follow`，实时流归 0.7.0）。
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
  仅在响应 `data` 一次性返回最终结果，进度信息写到 Serilog 日志。**SignalR 实时进度推迟到 0.7.0。**
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
    **不含** `follow`，实时流归 0.7.0）
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
  其余 fail-closed 拿空列表）；逐资源授权表已在 0.1.0 落地（主机级），容器级作用域排 0.12.0。
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
  四类资源计数与 CPU/内存占位（真实数据待 SignalR 0.7.0）。总览卡片的「详情 / 状态图」按钮与该页
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
  与能力交付节奏对齐（编排依赖容器/镜像且排 0.5.0）。
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
