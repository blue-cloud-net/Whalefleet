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
| B-002 | [日志路径：/var/log/docker-manager/](#b-002-日志路径varlogdocker-manager) | 本次会话（用户提出） | 2026-09-16 |
| B-003 | [安装后路径布局（原生包 + 容器双轨）](#b-003-安装后路径布局原生包--容器双轨) | 本次会话（用户提出） | 2026-09-16 |

---

## B-001 配置文件路径：FHS 兼容

> **阶段**：未排期
> **优先级**：待评估
> **影响面**：配置系统、运维部署文档、原生包安装脚本
> **相关约束**：[constraints.md#配置系统](./constraints.md#配置系统)

### 目标

在 `/etc/docker-manager/settings.json` 读取运维配置；文件不存在时回落默认；首次安装（原生包 / 容器）后生成空壳配置文件供运维编辑。

### 现状

现有 4 级配置优先级（[constraints.md](./constraints.md#配置系统)）：

| 顺序 | 来源 | 说明 |
|------|------|------|
| 1 | `appsettings.json` | 默认配置基线 |
| 2 | `appsettings.{Environment}.json` | 环境覆盖 |
| 3 | 环境变量 | 容器 / CI 部署主要手段 |
| 4 | 命令行参数 | 最高优先级 |

### 拟议变更

在第 2 与第 3 之间**新增第 3 层**——`/etc/docker-manager/settings.json` 作为"运维 / 包管理器注入"层：

| 新顺序 | 来源 | 说明 |
|--------|------|------|
| 1 | `appsettings.json` | 默认配置基线（不变） |
| 2 | `appsettings.{Environment}.json` | 环境覆盖（不变） |
| **3** | **`/etc/docker-manager/settings.json`** | **本次新增：运维 / 包管理器注入** |
| 4 | 环境变量 | 容器 / CI 场景 |
| 5 | 命令行参数 | 最高优先级 |

### 行为规范（拟）

| 场景 | 行为 |
|------|------|
| 原生包首次安装 | 安装脚本生成 `/etc/docker-manager/settings.json`，内容为「全部已知配置键 + 默认值」的空壳文件 |
| 原生包升级 | 若文件已存在则**不覆盖**；若缺失则补建空壳 |
| 容器首次启动 | 由 `docker-compose.yml` / `helm` 模板挂载空壳到 `/app/etc/settings.json`（容器内路径待 B-003 拍板） |
| 启动时文件缺失 | **WARN 日志** + 继续使用 `appsettings.json` 默认值；**不阻塞启动** |
| 文件存在但 JSON 损坏 | **ERROR 日志** + 启动失败（沿用现有 `ValidateOnStart` 风格） |
| 文件权限 | `0644 root:root`（原生包）；容器内由 volume mount 控制 |
| 数组 / 标量混合 | 与现有规则一致：层级用 `:`、环境变量用 `__`、数字下标表达数组 |

### 开放问题

1. **空壳文件的注释**：JSON 本身不支持注释。备选：
   - 引入 **JSON5** 解析（依赖 `Manatee.Json` 或 `JsonCommentator`）
   - 单挂 `/etc/docker-manager/settings.json.example` 仅作注释示例，运行时读 `.json`
   - 空壳文件不含注释，仅 README 给出全量键表
2. **缺失行为**：倾向 WARN + 回落默认；但若运维期望"强一致"也可改为启动失败
3. **是否限制可写范围**：是否只允许 `/etc/.../settings.json` 覆盖特定键白名单，避免覆盖误改端口等关键项

---

## B-002 日志路径：/var/log/docker-manager/

> **阶段**：未排期
> **优先级**：待评估
> **影响面**：Serilog 配置、容器镜像 Dockerfile、原生包 systemd unit
> **相关约束**：[constraints.md#日志输出基线](./constraints.md#日志输出基线)

### 目标

Serilog 滚动日志落地 `/var/log/docker-manager/`，替代或并存现有 `/app/data/logs`。滚动策略沿用现有规范（Daily、保留 14 文件、UTF-8 无 BOM）。

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
| 原生包（systemd） | `/var/log/docker-manager/app-.log` | **本次新增** |
| 容器化（默认） | `/app/data/logs/app-.log`（保持现状） | 既有 |
| 控制台 stdout | `docker logs` / `journalctl -u docker-manager` | 既有 |

### 行为规范（拟）

| 项 | 策略 |
|----|------|
| 目录创建 | systemd `RuntimeDirectory=docker-manager` 自动创建 `/run/docker-manager`；日志目录 `/var/log/docker-manager/` 由包安装脚本 `postinst` 创建并 chown |
| 目录权限 | `0755 docker-manager:docker-manager`（需新建系统用户组）；或 `0755 root:root` + `0644` 文件权限 |
| 文件权限 | `0644`（避免 root 进程写日志时拒绝读取） |
| 滚动策略 | Daily / 保留 14 文件 / UTF-8 无 BOM（沿用） |
| 容器兼容 | 容器内不强制 `/var/log`；保留 `/app/logs` 软链接或配置项切换 |
| journal 集成 | 待定：是否同时输出到 systemd journal（`Serilog.Sinks.Systemd`），便于 `journalctl -u docker-manager` 直接看 |

### 开放问题

1. **是否需要新建系统用户 `docker-manager`**：当前 systemd unit（若未来引入）以 root 跑？建议**非 root** 运行（最小权限原则），日志目录 ownership 需随之调整
2. **journal vs 文件双写**：双写更利于排障（journal 含 PID / 启动 ID），但磁盘占用翻倍
3. **日志轮转冲突**：Linux 自带 `logrotate` vs Serilog 内置滚动——二选一还是叠加？倾向**只用 Serilog 内置**（已带压缩与保留数）
4. **容器内路径**：容器是否也用 `/var/log/docker-manager/`（顶层 volume 命名约定）以与原生包对齐？

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
| `/usr/lib/docker-manager/` 或 `/opt/docker-manager/` | 二进制 + 前端静态资源 | `/usr/lib` 适合包管理器拆分；`/opt` 适合"自包含包"。**待定** |
| `/etc/docker-manager/settings.json` | 运维配置 | 见 [B-001](#b-001-配置文件路径fhs-兼容) |
| `/var/lib/docker-manager/data.db` | SQLite 主数据库 | WAL 模式 |
| `/var/lib/docker-manager/uploads/` | 用户上传（未来：插件 / 备份 / 导入） | 当前未使用，预留 |
| `/var/lib/docker-manager/cache/` | 运行时缓存（图标、缩略图、临时文件） | 可被安全清理 |
| `/var/log/docker-manager/app-.log` | 滚动日志 | 见 [B-002](#b-002-日志路径varlogdocker-manager) |
| `/lib/systemd/system/docker-manager.service` | systemd unit | `Type=notify`（推荐）或 `simple` |

> **FHS 原则**：`/etc` 配置、`/var/lib` 持久数据、`/var/log` 日志、`/usr` 或 `/opt` 只读程序。

#### 容器化（保留向后兼容）

| 容器内路径 | 推荐宿主挂载点 | 内容 |
|------------|----------------|------|
| `/app/data` | `/var/lib/docker-manager`（推荐）或宿主 `./data`（开发） | SQLite + 缓存 |
| `/app/etc` | `/etc/docker-manager` | 配置文件（B-001） |
| `/app/logs` | `/var/log/docker-manager` | 日志（B-002） |
| `/app/wwwroot` | 内置，不挂载 | 前端 SPA 静态资源 |

> **推荐 docker-compose 挂载写法**（待 1.0.0 文档同步）：
>
> ```yaml
> volumes:
>   - /var/lib/docker-manager:/app/data      # 数据持久化
>   - /etc/docker-manager:/app/etc:ro       # 注入运维配置（只读）
>   - /var/log/docker-manager:/app/logs     # 日志导出到宿主
> ```

### 默认数据库

| 项 | 路径 |
|----|------|
| SQLite 主库 | `/var/lib/docker-manager/data.db` |
| WAL 文件 | `/var/lib/docker-manager/data.db-wal` |
| SHM 文件 | `/var/lib/docker-manager/data.db-shm` |
| 备份目录 | `/var/lib/docker-manager/backups/`（含 `scripts/backup.sh`） |

### 开放问题

1. **包格式**：仅 `deb`、仅 `rpm`、还是 CI 矩阵双出？优先 `deb`（GitHub Actions `ubuntu-latest` 原生支持）
2. **systemd Type**：`simple`（最简）vs `notify`（更精准的就绪探测，依赖 `sd_notify` P/Invoke）vs `forking`（不需要）
3. **运行用户**：
   - 固定 `docker-manager` 用户 + 组（手动 `postinst` 创建）
   - `DynamicUser=yes`（systemd 自管临时用户，无持久状态——与 `/var/lib` 持久化冲突，**不推荐**）
4. **升级兼容性**：升级时如何保留 `/var/lib/docker-manager/data.db` 与 `/etc/docker-manager/settings.json`？包管理器的 conffiles 机制需要把 settings.json 标记为 conffile
5. **容器内空壳 settings.json**：是否在镜像构建时把空壳文件 `COPY` 到 `/app/etc/settings.json`？
6. **数据迁移工具**：原生包场景是否需要独立 `/usr/lib/docker-manager/migrate` 工具，用于跨版本 schema 升级？

---

## 修订记录

| 日期 | 作者 | 说明 |
|------|------|------|
| 2026-09-16 | GitHub Copilot | 初稿，沉淀本次会话提出的 3 条需求（B-001 / B-002 / B-003） |
