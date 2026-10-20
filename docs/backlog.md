# 需求待办池（未排期）

> 本文档用于沉淀**已识别但尚未排进任何版本阶段**的需求与目标。
>
> 它与以下文档**正交**：
>
> | 文档 | 职责 |
> |------|------|
> | [roadmap.md](./roadmap.md) | 按**版本阶段**锁定交付计划（0.0.x → 1.0.0） |
> | [features.md](./features.md) | 按**版本标签**枚举已排期功能 |
> | [constraints.md](./constraints.md) | 锁定**技术栈与版本基线**（Serilog、Docker SDK、.NET 等） |
> | **backlog.md（本文件）** | 沉淀**待排期诉求**——想法阶段，不承诺交付时间 |
>
> **流转规则**：当某条 backlog 项被排进具体阶段后，**必须**从此文档移除，并同步写入 [roadmap.md](./roadmap.md) 与 [features.md](./features.md)。backlog 不留"已完成条目"。

---

## 索引

| 编号 | 标题 | 来源 | 提出日期 |
|------|------|------|----------|
| B-001 | [配置文件路径：FHS 兼容](#b-001-配置文件路径fhs-兼容) | 本次会话（用户提出） | 2026-09-16 |
| B-002 | [日志路径：/var/log/whalefleet/](#b-002-日志路径varlogwhalefleet) | 本次会话（用户提出） | 2026-09-16 |
| B-003 | [安装后路径布局（原生包 + 容器双轨）](#b-003-安装后路径布局原生包--容器双轨) | 本次会话（用户提出） | 2026-09-16 |
| B-004 | [Prometheus `/metrics` 指标端点](#b-004-prometheus-metrics-指标端点) | 版本策略重构评审 | 2026-10-15 |
| B-005 | [应用模板（App Templates 等价物）](#b-005-应用模板app-templates-等价物) | 0.x 路线图评审 | 2026-10-16 |
| B-006 | [GitOps：从 Git 仓库同步 Stack](#b-006-gitops从-git-仓库同步-stack) | 0.x 路线图评审 | 2026-10-16 |
| B-007 | [计划任务 / cron](#b-007-计划任务--cron) | 0.x 路线图评审 | 2026-10-16 |
| B-008 | [共享凭据管理与主机快照浏览](#b-008-共享凭据管理与主机快照浏览) | 0.x 路线图评审 | 2026-10-16 |
| B-009 | [SIEM 外发与告警渠道扩展](#b-009-siem-外发与告警渠道扩展) | 0.x 路线图评审 | 2026-10-16 |
| B-010 | [Terraform Provider / CLI、危险操作二次确认、容器归属调整](#b-010-terraform-provider--cli危险操作二次确认容器归属调整) | 0.x 路线图评审 | 2026-10-16 |
| B-011 | [MCP 独立部署形态（独立容器 / stdio 包）](#b-011-mcp-独立部署形态独立容器--stdio-包) | 0.x 路线图评审 | 2026-10-16 |

> **2026-10-16 批量沉淀**：B-005 – B-011 是在 0.x 路线图重排时从「竞品补差候选清单」中筛下来的，
> 均已确认**不进 1.0.0 范围**（见 [roadmap.md#1000-之后明确非目标](./roadmap.md#1000-之后明确非目标)）。
> 条目内容为**简述**，排期前需按 B-001 的粒度补齐方案与开放问题。

---

## B-001 配置文件路径：FHS 兼容

> **阶段**：未排期
> **优先级**：待评估
> **影响面**：配置系统、运维部署文档、原生包安装脚本
> **相关约束**：[constraints.md#配置系统](./constraints.md#配置系统)

### 目标

在 `/etc/whalefleet/settings.json` 读取运维配置；文件不存在时回落默认；首次安装（原生包 / 容器）后生成空壳配置文件供运维编辑。

### 现状

现有 4 级配置优先级（[constraints.md](./constraints.md#配置系统)）：

| 顺序 | 来源 | 说明 |
|------|------|------|
| 1 | `appsettings.json` | 默认配置基线 |
| 2 | `appsettings.{Environment}.json` | 环境覆盖 |
| 3 | 环境变量 | 容器 / CI 部署主要手段 |
| 4 | 命令行参数 | 最高优先级 |

### 拟议变更

在第 2 与第 3 之间**新增第 3 层**——`/etc/whalefleet/settings.json` 作为"运维 / 包管理器注入"层：

| 新顺序 | 来源 | 说明 |
|--------|------|------|
| 1 | `appsettings.json` | 默认配置基线（不变） |
| 2 | `appsettings.{Environment}.json` | 环境覆盖（不变） |
| **3** | **`/etc/whalefleet/settings.json`** | **本次新增：运维 / 包管理器注入** |
| 4 | 环境变量 | 容器 / CI 场景 |
| 5 | 命令行参数 | 最高优先级 |

### 行为规范（拟）

| 场景 | 行为 |
|------|------|
| 原生包首次安装 | 安装脚本生成 `/etc/whalefleet/settings.json`，内容为「全部已知配置键 + 默认值」的空壳文件 |
| 原生包升级 | 若文件已存在则**不覆盖**；若缺失则补建空壳 |
| 容器首次启动 | 由 `docker-compose.yml` / `helm` 模板挂载空壳到 `/app/etc/settings.json`（容器内路径待 B-003 拍板） |
| 启动时文件缺失 | **WARN 日志** + 继续使用 `appsettings.json` 默认值；**不阻塞启动** |
| 文件存在但 JSON 损坏 | **ERROR 日志** + 启动失败（沿用现有 `ValidateOnStart` 风格） |
| 文件权限 | `0644 root:root`（原生包）；容器内由 volume mount 控制 |
| 数组 / 标量混合 | 与现有规则一致：层级用 `:`、环境变量用 `__`、数字下标表达数组 |

### 开放问题

1. **空壳文件的注释**：JSON 本身不支持注释。备选：
   - 引入 **JSON5** 解析（依赖 `Manatee.Json` 或 `JsonCommentator`）
   - 单挂 `/etc/whalefleet/settings.json.example` 仅作注释示例，运行时读 `.json`
   - 空壳文件不含注释，仅 README 给出全量键表
2. **缺失行为**：倾向 WARN + 回落默认；但若运维期望"强一致"也可改为启动失败
3. **是否限制可写范围**：是否只允许 `/etc/.../settings.json` 覆盖特定键白名单，避免覆盖误改端口等关键项

---

## B-002 日志路径：/var/log/whalefleet/

> **阶段**：未排期
> **优先级**：待评估
> **影响面**：Serilog 配置、容器镜像 Dockerfile、原生包 systemd unit
> **相关约束**：[constraints.md#日志输出基线](./constraints.md#日志输出基线)

### 目标

Serilog 滚动日志落地 `/var/log/whalefleet/`，替代或并存现有 `/app/data/logs`。滚动策略沿用现有规范（Daily、保留 14 文件、UTF-8 无 BOM）。

### 现状

当前日志输出基线（0.0.2 起）：

| 项 | 策略 |
|----|------|
| 输出目标 | 控制台 + 滚动文件（`Serilog.Sinks.File`） |
| 文件路径 | 生产 `/app/data/logs/app-.log`；开发 `logs/app-.log` |
| 滚动周期 | 按天 |
| 保留数量 | 14 个文件 |
| 文件编码 | UTF-8 无 BOM |

### 拟议变更

按部署形态分流日志路径：

| 部署形态 | 日志路径 | 来源 |
|----------|----------|------|
| 原生包（systemd） | `/var/log/whalefleet/app-.log` | **本次新增** |
| 容器化（默认） | `/app/data/logs/app-.log`（保持现状） | 既有 |
| 控制台 stdout | `docker logs` / `journalctl -u whalefleet` | 既有 |

### 行为规范（拟）

| 项 | 策略 |
|----|------|
| 目录创建 | systemd `RuntimeDirectory=whalefleet` 自动创建 `/run/whalefleet`；日志目录 `/var/log/whalefleet/` 由包安装脚本 `postinst` 创建并 chown |
| 目录权限 | `0755 whalefleet:whalefleet`（需新建系统用户组）；或 `0755 root:root` + `0644` 文件权限 |
| 文件权限 | `0644`（避免 root 进程写日志时拒绝读取） |
| 滚动策略 | Daily / 保留 14 文件 / UTF-8 无 BOM（沿用） |
| 容器兼容 | 容器内不强制 `/var/log`；保留 `/app/logs` 软链接或配置项切换 |
| journal 集成 | 待定：是否同时输出到 systemd journal（`Serilog.Sinks.Systemd`），便于 `journalctl -u whalefleet` 直接看 |

### 开放问题

1. **是否需要新建系统用户 `whalefleet`**：当前 systemd unit（若未来引入）以 root 跑？建议**非 root** 运行（最小权限原则），日志目录 ownership 需随之调整
2. **journal vs 文件双写**：双写更利于排障（journal 含 PID / 启动 ID），但磁盘占用翻倍
3. **日志轮转冲突**：Linux 自带 `logrotate` vs Serilog 内置滚动——二选一还是叠加？倾向**只用 Serilog 内置**（已带压缩与保留数）
4. **容器内路径**：容器是否也用 `/var/log/whalefleet/`（顶层 volume 命名约定）以与原生包对齐？

---

## B-003 安装后路径布局（原生包 + 容器双轨）

> **阶段**：未排期
> **优先级**：待评估
> **影响面**：原生包打包、systemd unit、容器镜像构建、README 部署文档
> **依赖**：B-001（配置）、B-002（日志）

### 目标

明确不同部署形态下，**程序 / 数据 / 配置文件 / 日志 / 数据库** 的落点，统一遵循 FHS（Filesystem Hierarchy Standard），同时保留容器化部署的灵活性。

### 拟议布局

#### 原生包（遵循 FHS）

| 路径 | 内容 | 备注 |
|------|------|------|
| `/usr/lib/whalefleet/` 或 `/opt/whalefleet/` | 二进制 + 前端静态资源 | `/usr/lib` 适合包管理器拆分；`/opt` 适合"自包含包"。**待定** |
| `/etc/whalefleet/settings.json` | 运维配置 | 见 [B-001](#b-001-配置文件路径fhs-兼容) |
| `/var/lib/whalefleet/data.db` | SQLite 主数据库 | WAL 模式 |
| `/var/lib/whalefleet/uploads/` | 用户上传（未来：插件 / 备份 / 导入） | 当前未使用，预留 |
| `/var/lib/whalefleet/cache/` | 运行时缓存（图标、缩略图、临时文件） | 可被安全清理 |
| `/var/log/whalefleet/app-.log` | 滚动日志 | 见 [B-002](#b-002-日志路径varlogwhalefleet) |
| `/lib/systemd/system/whalefleet.service` | systemd unit | `Type=notify`（推荐）或 `simple` |

> **FHS 原则**：`/etc` 配置、`/var/lib` 持久数据、`/var/log` 日志、`/usr` 或 `/opt` 只读程序。

#### 容器化（保留向后兼容）

| 容器内路径 | 推荐宿主挂载点 | 内容 |
|------------|----------------|------|
| `/app/data` | `/var/lib/whalefleet`（推荐）或宿主 `./data`（开发） | SQLite + 缓存 |
| `/app/etc` | `/etc/whalefleet` | 配置文件（B-001） |
| `/app/logs` | `/var/log/whalefleet` | 日志（B-002） |
| `/app/wwwroot` | 内置，不挂载 | 前端 SPA 静态资源 |

> **推荐 docker-compose 挂载写法**（待 1.0.0 文档同步）：
>
> ```yaml
> volumes:
>   - /var/lib/whalefleet:/app/data      # 数据持久化
>   - /etc/whalefleet:/app/etc:ro       # 注入运维配置（只读）
>   - /var/log/whalefleet:/app/logs     # 日志导出到宿主
> ```

### 默认数据库

| 项 | 路径 |
|----|------|
| SQLite 主库 | `/var/lib/whalefleet/data.db` |
| WAL 文件 | `/var/lib/whalefleet/data.db-wal` |
| SHM 文件 | `/var/lib/whalefleet/data.db-shm` |
| 备份目录 | `/var/lib/whalefleet/backups/`（含 `scripts/backup.sh`） |

### 开放问题

1. **包格式**：仅 `deb`、仅 `rpm`、还是 CI 矩阵双出？优先 `deb`（GitHub Actions `ubuntu-latest` 原生支持）
2. **systemd Type**：`simple`（最简）vs `notify`（更精准的就绪探测，依赖 `sd_notify` P/Invoke）vs `forking`（不需要）
3. **运行用户**：
   - 固定 `whalefleet` 用户 + 组（手动 `postinst` 创建）
   - `DynamicUser=yes`（systemd 自管临时用户，无持久状态——与 `/var/lib` 持久化冲突，**不推荐**）
4. **升级兼容性**：升级时如何保留 `/var/lib/whalefleet/data.db` 与 `/etc/whalefleet/settings.json`？包管理器的 conffiles 机制需要把 settings.json 标记为 conffile
5. **容器内空壳 settings.json**：是否在镜像构建时把空壳文件 `COPY` 到 `/app/etc/settings.json`？
6. **数据迁移工具**：原生包场景是否需要独立 `/usr/lib/whalefleet/migrate` 工具，用于跨版本 schema 升级？

---

## B-004 Prometheus `/metrics` 指标端点

> **阶段**：未排期
> **优先级**：待评估
> **影响面**：Api 中间件与配置、可观测性、部署文档
> **相关约束**：[constraints.md#配置系统](./constraints.md#配置系统)

### 目标

暴露标准的 `/metrics` 端点（Prometheus 文本格式），供外部采集器抓取。
当前只有 `/healthz`（存活 / SQLite 连通性）与 Serilog 文本日志，**没有任何可被采集的指标**。

### 背景

`1.0.0` 的准入门槛里有「性能压测」与「可观测性」两项，但没有指标端点时，
压测只能靠外部工具测一次、发布后无法持续观察。

### 拟议变更（未定）

- 指标来源：`System.Diagnostics.Metrics`（BCL 内置，零第三方依赖）而非引入 `prometheus-net`
- 内容范围（待定）：HTTP 请求数与延迟分位数、SQLite 查询数与耗时、Docker 操作耗时与失败数、
  连接池与客户端池的水位
- 是否默认开启：待定。**倾向默认关闭**——与 `ReverseProxy` 同类理由：
  指标端点会泄露主机名、容器 ID、请求路径，裸暴露在公网上是不必要的风险

### 开放问题

1. 端点路径是否需要避开 `/api` 前缀（避免被 SPA 回退吞掉）？`/healthz` 已有先例
2. 标签基数：主机名 / 容器名是低基数，但 `containerId` 是高基数，**不应**作为标签
3. 是否需要鉴权（复用 JWT，还是只允许从内网 / 反代访问）？
4. 与 1.0.0 的「生产就绪」承诺如何取舍：当前 roadmap 明确把它列在 1.0.0 之外

---

## B-005 应用模板（App Templates 等价物）

> **阶段**：未排期
> **优先级**：中——**adoption 杠杆**，不是运维深度
> **影响面**：新表 `templates`、模板发现与校验、部署编排（依赖 Stack）、前端模板画廊
> **对标**：Portainer CE 的内置模板 + 自定义模板（JSON 清单，可托管在自己的 web 服务器）

### 目标

提供「一键部署」入口：每个模板 = 名称 + 图标 + 镜像或 compose 片段 + 若干可填变量。
用户在界面里选 nginx / redis / postgres / Nextcloud，填几个参数（端口、卷路径、密码）即完成部署。

### 要点（未定）

- 内置模板随镜像发布，还是运行时从远端拉取（**离线可用性**是关键取舍）
- 自定义模板：托管 JSON 清单（Portainer 的做法）还是界面内直接维护（存库）
- 模板变量需支持校验（端口范围、必填、密码强度）与默认值
- **与「导出 Stack 为 YAML」的关系**：`0.14.0` 已交付反向导出定义，本项落地后应把「存为模板」接上

### 开放问题

1. 模板清单的 schema 是否兼容 Portainer 的 JSON 格式（可复用社区模板库，但等于承担外部格式的兼容包袱）
2. 内置模板的数量与审计责任：模板里的镜像是第三方产物，是否需要签名 / 摘要固定
3. 是否允许模板执行 `build`（Portainer 的 compose 模板不含 build，Docker 已知问题）

---

## B-006 GitOps：从 Git 仓库同步 Stack

> **阶段**：未排期
> **优先级**：中高——2026 年自托管平台的主流形态
> **影响面**：新表 `git_sources`、新 NuGet（git 客户端或 shell out 到 `git`）、凭据管理、同步策略
> **对标**：Portainer App Delivery（Sources + Workflows）、Komodo 的 Sync

### 目标

把 Stack 的 YAML 来源从「上传一次」变为「某个 Git 仓库的某个路径」，支持手动同步与 webhook 触发同步。

### 要点（未定）

- Git 客户端：库内实现（如 `LibGit2Sharp`）还是 shell out 到宿主机的 `git`（**容器镜像需带 git**）
- 凭据：SSH key 与 PAT 的存放（与 [B-008](#b-008-共享凭据管理与主机快照浏览) 共享）
- 冲突策略：远端改动与本地界面改动的优先级（只读同步 vs 双向）
- 与 `0.15.0` 的资源 Webhook 的关系：webhook 触发同步还是触发重新部署

### 开放问题

1. 相对路径 bind mount 在 Git 部署下的解析问题（Portainer 有专门 FAQ，说明这里容易出错）
2. 是否支持分支 / tag / commit 固定
3. 同步失败时的回滚与告警

---

## B-007 计划任务 / cron

> **阶段**：未排期
> **优先级**：中
> **影响面**：新表 `schedules`、后台调度器（`IHostedService` + `PeriodicTimer`）、执行记录
> **对标**：Komodo 的 Schedules

### 目标

按 cron 表达式定期执行动作：定期备份、定期 prune、定时重启容器、定时重新部署 Stack。

### 要点（未定）

- 是否引入 cron 解析库（如 `Cronos`）还是只支持「每 N 分钟 / 每天 HH:mm」这类固定间隔
- 执行记录：复用 `0.9.0` 的审计表，还是另立 `schedule_runs`
- 与 `0.11.0` 告警的关系：任务失败应触发告警（Portainer 把「备份失败」列为内置规则）
- **多实例部署时**需要分布式锁——本项目当前是单实例，1.0.0 之前不构成问题

### 开放问题

1. 定期 prune 与「先列清单再确认」的交互原则冲突——自动清理要不要单独的开关
2. 任务执行与并发操作（用户同时手动操作）的冲突处理

---

## B-008 共享凭据管理与主机快照浏览

> **阶段**：未排期
> **优先级**：中
> **影响面**：新表 `shared_credentials`、加密存储、主机快照表
> **对标**：Portainer 的 Shared credentials（SSH / Git）+ Snapshot browsing

### 目标

两件事：

1. **共享凭据**：把 SSH key、Git PAT、Registry 凭据集中管理并复用（多个功能引用同一条凭据，而不是各存一份）
2. **主机快照浏览**：主机离线时，界面展示**最后一次成功探测到的状态**（而不是整页报错或空白），并明确标注「这是 x 分钟前的快照」

### 开放问题

1. 凭据的引用计数与「删除时被引用」的处理（拒绝删除 vs 级联清空）
2. 快照的存储位置与过期策略（SQLite 表 vs 文件）；是否与 `0.6.0` 的可观测性数据共用
3. 快照浏览是否会把「过期数据」误当成实时数据（界面必须强提示，否则比空白更危险）

---

## B-009 SIEM 外发与告警渠道扩展

> **阶段**：未排期
> **优先级**：中——企业向
> **影响面**：Serilog Sink（syslog / HTTP）、`0.9.0` 审计记录的转发、告警渠道适配器
> **对标**：Portainer 的「Stream auth and activity logs to an external provider」

### 目标

1. 把认证日志与活动日志**实时转发**到外部系统（syslog / RFC 5424 或 HTTP 端点）
2. 告警渠道从 Webhook / 邮件扩展到 Slack / Telegram / ntfy / Discord

### 开放问题

1. 转发失败时的行为（丢弃并计数 vs 本地缓冲重试）——**不能阻塞主流程**
2. 是否需要**脱敏**后再外发（审计记录里含主机名与用户名；与 `0.18.0` 的 MCP 脱敏同理）
3. 告警渠道是「每个渠道一个适配器」还是「所有渠道都退化成 Webhook 配置」

---

## B-010 Terraform Provider / CLI、危险操作二次确认、容器归属调整

> **阶段**：未排期
> **优先级**：低到中（三条独立项，可分别排期）
> **影响面**：对外开放的 API、前端交互守则、`0.12.0` 的权限模型
> **对标**：Portainer 的 Terraform 管理 + Policies（Change confirmation policy）+ Container ownership

### 三条独立项

1. **Terraform Provider / CLI**：把 API 包装为声明式资源管理入口。
   前置是 `0.19.0` 的 API 冻结——**在冻结之前做等于白做**。
2. **危险操作二次确认**：删容器 / 删卷 / prune / commit 等破坏性操作要求手输资源名确认。
   Portainer 把它做成可配的 Policy（`admin/environments/policies`）。
3. **容器归属调整**：把容器的可见性从「创建者」改为「可转移」。依赖 `0.12.0` 的容器级 RBAC。

### 开放问题

1. 二次确认的强度（手输名字 vs 输入固定短语 vs 只弹确认框）——过强会让日常操作变累
2. 二次确认是全局开关还是按操作粒度
3. 容器归属调整在多管理员场景下是否会被滥用（转移即提权）

---

## B-011 MCP 独立部署形态（独立容器 / stdio 包）

> **阶段**：未排期
> **优先级**：中
> **影响面**：新增镜像或 NuGet 包、发布流程、客户端配置文档
> **对标**：Portainer MCP Server（`portainer/portainer-mcp` 容器 / `uvx` stdio / `.mcpb` 包）

### 目标

`0.18.0` 选择的是**同进程 `/mcp`**（零额外部署形态）。本项把 MCP 服务器拆成可独立部署与升级的形态：

- 独立容器（多个用户共享一个 MCP 网关）
- stdio 包（供本地客户端拉起，无需暴露 HTTP）

### 开放问题

1. stdio 包需要 NuGet 发布形态（本项目当前不发布 NuGet），或 AOT 单文件分发
2. 独立容器形态下需要**两层凭据**（共享 gate token + 用户自己的 PAT），比同进程复杂
3. 是否值得——同进程形态已覆盖绝大多数场景，拆分的主要收益是「MCP 可独立升级」

---

## 修订记录

| 日期 | 作者 | 说明 |
|------|------|------|
| 2026-10-16 | GitHub Copilot | 0.x 路线图重排（`0.1.0`–`0.19.0`）时沉淀 B-005 – B-011；确认均不进 1.0.0 范围 |
| 2026-10-15 | GitHub Copilot | 新增 B-004（Prometheus 指标）；`1.0.0` 准入评审时确认不纳入 1.0.0 范围 |
| 2026-09-16 | GitHub Copilot | 初稿，沉淀本次会话提出的 3 条需求（B-001 / B-002 / B-003） |
