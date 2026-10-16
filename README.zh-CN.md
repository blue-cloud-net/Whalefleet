# Whalefleet

> **语言 / Language**：**简体中文** | [English](README.md)

> 一个类 Portainer 的轻量化 Docker 管理平台。基于 Web 的可视化界面，用于管理本地及远程 Docker 主机上的容器、镜像、Volume、Network 等资源。
>
> **后端** .NET 10 LTS + Dapper + SQLite + SignalR ｜ **前端** Vue 3.5+ + TypeScript 5.7+ + UnoCSS + Element Plus 2.14+ ｜ **Docker SDK** [Docker.DotNet.Enhanced](https://github.com/testcontainers/Docker.DotNet) 4.3.3（Docker Engine API v29.4.1）

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10 LTS](https://img.shields.io/badge/.NET-10_LTS-512BD4)](https://dotnet.microsoft.com)
[![Vue 3.5+](https://img.shields.io/badge/Vue-3.5+-42b883)](https://vuejs.org)
[![Docker Engine API v29.4.1](https://img.shields.io/badge/Docker_Engine_API-v29.4.1-2496ED)](https://github.com/testcontainers/Docker.DotNet)
[![GHCR](https://img.shields.io/badge/GHCR-blue--cloud--net%2Fwhalefleet-2496ED)](https://ghcr.io/blue-cloud-net/whalefleet)

> 当前阶段：**0.x 功能迭代期**（未正式发布，不打 `0.x` tag，也不推 GHCR）。
> **唯一目标版本是 `1.0.0`**，此前先出 `1.0.0-rc.N`；公开 API 在 `1.0.0` 之前**不冻结**。
> 版本规划见 [docs/roadmap.md](docs/roadmap.md)。

---

## ✨ 核心特性

- 🐳 **容器全生命周期管理**：创建 / 启动 / 停止 / 重启 / 重命名 / 复制 / 删除 / 日志查看 / 详情（0.0.9）；0.1.0 起支持批量删除；**批量启停与资源级清理**（0.6.0）；**导出 / 导入 / commit**（0.14.0）
- 🖼 **镜像管理**：拉取 / 删除 / 详情 / 打标签（0.0.10 起）；0.1.0 起支持批量删除；**Dockerfile 构建与私有 Registry**（0.4.0）；**save / load** 与**更新检测**（0.14.0 / 0.16.0）
- 💾 **Volume / Network 完整 CRUD**（0.2.0）
- 🖥 **多主机管理**：本地 socket + 远程 TCP（可选 TLS / Basic 认证），顶栏切换（0.0.8 起）；**分组 + 标签，支持按分组批量操作与磁盘用量**（0.10.0）
- 🔐 **首日鉴权**：JWT + refresh token + Argon2id 密码哈希 + 登录速率限制（0.0.4 起）；**操作审计**（0.9.0）
- 👥 **四个内置角色**（admin / operator / viewer / auditor）+ 主机粒度可见性（0.0.8，分配能力 0.1.0）；**容器级 RBAC**（0.12.0）；**团队与批量授权**（0.13.0）
- 🔔 **实时推送**（0.7.0）：SignalR 实时日志流 / CPU 内存网络监控 / 容器异常通知
- 🚨 **告警规则体系**（0.11.0）：三级严重度 + 可配阈值 + 冷却期 + Webhook / 邮件渠道
- 💻 **Web 终端**（0.8.0）：xterm.js + 独立短时 terminal token
- 📦 **Docker Compose / Stack 管理**（0.5.0），并支持**资源 Webhook 与 `.env` 变量**（0.15.0）
- 🤖 **MCP 服务器**（0.18.0）：把 Docker 管理能力暴露给 AI 客户端（VS Code Copilot、Claude Desktop）——默认只读，环境变量值自动脱敏
- 🎨 **现代 UI**：UnoCSS + Element Plus + 命令面板（Ctrl/K）+ 全局快捷键 + 亮/暗主题 + 三色主题色

---

## ⚠️ 安全声明：Docker Socket 挂载

> **挂载 `/var/run/docker.sock` 等于赋予容器内进程对宿主机 Docker 的 root 权限**——单容器漏洞 = 宿主机沦陷。

**强烈推荐使用 socket-proxy 模式**部署：

```yaml
services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy:v0.5.0
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
      POST: 1       # 写操作总闸；关掉则整个 API 退化为只读
      SWARM: 0
      INFO: 1
      PING: 1
      VERSION: 1
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro

  whalefleet:
    image: whalefleet:local
    depends_on: [socket-proxy]
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      # 三项全部可选：省略时密码与 JWT 密钥会在首次启动时自动生成，
      # 并持久化到 ./data（jwt.key / initial-admin.txt）。
      - ADMIN__USER=admin
      # - ADMIN__PASSWORD=<初始强密码>
      # - JWT__SECRET=<你的强密钥-≥32-字节>
      # 主机不再是配置项（0.0.8 起为 hosts 表）：默认只有 local（/var/run/docker.sock），
      # 指向 socket-proxy 等其他主机请在启动后通过管理界面添加。
    volumes:
      - ./data:/app/data
      - ./scripts:/app/scripts
    restart: unless-stopped
```

如必须**直挂 socket**，请确保 Whalefleet 实例仅暴露给可信网络。完整的风险分析见 [docs/constraints.md](docs/constraints.md#docker-socket-挂载-️-高危)。

---

## 🚀 一键启动

### 方式一：Socket-Proxy 模式（推荐）

> **尚未发布任何镜像。** 项目处于 `0.x` 功能迭代期，不对外发布镜像，
> 因此当前唯一的镜像来源是本地构建；首个对外镜像随 `v1.0.0-rc.1` 发布。

```bash
# 1. 克隆并进入仓库
git clone https://github.com/blue-cloud-net/whalefleet.git
cd whalefleet

# 2. 构建镜像（Node + .NET SDK + runtime 三阶段）
docker build -t whalefleet:local .

# 3. 启动。**无需任何配置**——密码与 JWT 密钥都会自动生成。
#    默认使用 socket-proxy 模式（推荐）
docker compose up -d

#    改用直挂 socket 模式：先停掉，再
#    docker compose --profile direct up -d

# 4. 取回自动生成的初始管理员密码
docker compose exec whalefleet cat /app/data/initial-admin.txt

# 5. 浏览器访问 http://localhost:8080，用上面文件里的凭据登录
#
# ⚠️ 直挂 socket 模式需要把容器加入宿主机 docker 组，否则报 1301：
#    export DOCKER_GID=$(stat -c '%g' /var/run/docker.sock)
#
# 想自己指定密码与密钥？cp .env.example .env 后按注释填。
# 留空即等价于不传。
```

### 验证部署

```bash
docker compose ps                              # 期望 STATUS 为 "healthy"
docker compose exec whalefleet bash /app/scripts/backup.sh   # 产出经过校验的备份

# ⚠️ 备份目录里除了 .db，还应有一个同名的 .db.jwt.key——
#    恢复时漏掉它会让所有在线用户被静默登出。
ls -1 data/backups/

# 定时备份（每天 03:17——避开整点以减少与其他任务争抢 IO）：
#   17 3 * * * docker exec whalefleet bash /app/scripts/backup.sh \
#     >> /var/log/whalefleet-backup.log 2>&1
```

### 方式二：直挂 Socket（仅可信环境）

```bash
# ⚠️ --group-add 不可省：容器内 app 用户（UID 10001）不在宿主机 docker 组，
#    连不上 socket，表现为 1301「无法连接 Docker 守护进程」。
docker run -d \
  --name whalefleet \
  -p 8080:8080 \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v $(pwd)/data:/app/data \
  --group-add "$(stat -c '%g' /var/run/docker.sock)" \
  --restart unless-stopped \
  whalefleet:local

# 取回自动生成的初始管理员密码
docker exec whalefleet cat /app/data/initial-admin.txt
```

### 方式三：独立进程部署

```bash
dotnet Whalefleet.Api.dll --urls http://0.0.0.0:8080
```

要求宿主机当前用户对 `/var/run/docker.sock` 有读写权限（通常需要加入 `docker` 用户组）。

---

## 📸 功能截图

> _占位：1.0.0-rc.N 阶段补充截图_

| Dashboard | Container List | Container Logs |
|-----------|---------------|----------------|
| _(待补充)_ | _(待补充)_ | _(待补充)_ |

---

## 📚 文档

| 文档 | 说明 |
|------|------|
| [docs/overview.md](docs/overview.md) | 项目背景、定位、目标、非目标、发布模型、仓库标识 |
| [docs/constraints.md](docs/constraints.md) | 技术栈版本基线、安全 / CI/CD 约束、部署约束、边界条件 |
| [docs/features.md](docs/features.md) | 按模块分组的完整功能列表（按版本标注） |
| [docs/roadmap.md](docs/roadmap.md) | 以 `1.0.0` 为目标的版本规划（0.1.0 – 0.19.0 功能迭代 + RC 与正式发布） |
| [docs/architecture.md](docs/architecture.md) | 系统分层、多主机 DockerClient 工厂、SDK 隔离、通信机制 |
| [docs/api-spec.md](docs/api-spec.md) | RPC 风格 API 约定 + 错误码分段 + SignalR Hub + WebSocket 终端 |
| [docs/ui-guidelines.md](docs/ui-guidelines.md) | UI 布局规范（布局图 / 断点 / 组件分层 / 快捷键 / 主题 / 路由） |
| [CHANGELOG.md](CHANGELOG.md) | 变更记录（英文）｜ [CHANGELOG.zh-CN.md](CHANGELOG.zh-CN.md)（中文） |
| [AGENTS.md](AGENTS.md) | AI 代理协作指南（构建命令 / 架构铁律 / 踩坑清单） |

---

## 🛠 技术栈一览

**后端**：.NET 10 LTS / ASP.NET Core 10 / SQLite + Dapper / SignalR / Serilog / `Docker.DotNet.Enhanced`

**前端**：Vue 3.5+ / TypeScript 5.7+ / Vite 7+ / UnoCSS / Element Plus 2.14+ / Pinia 4 / vue-i18n 11 / xterm.js 5.5+

**配套**：Central Package Management（NuGet）/ pnpm（前端）/ xUnit + Testcontainers（集成测试）/ Vitest（前端测试）

完整版本约束见 [docs/constraints.md](docs/constraints.md)。

---

## 🤝 参与贡献

欢迎贡献代码、报告 Bug、提出功能建议。

- 提 Issue：[GitHub Issues](https://github.com/blue-cloud-net/whalefleet/issues)
- 提 PR：遵循 [AGENTS.md](AGENTS.md#提交规范) 的 Conventional Commits 规范
- 报告安全漏洞：请**不要**在公开 Issue 中披露，请私信维护者

> 仓库**有意不提供** `CONTRIBUTING.md` / `SECURITY.md` / `CODE_OF_CONDUCT.md`；
> 适用于贡献者的规则写在 [AGENTS.md](AGENTS.md) 里。

开发环境：.NET 10 SDK + Node.js 20+ + pnpm。

---

## 📋 版本与发布

| 版本段 | 含义 | 打 tag | 推 GHCR？ |
|--------|------|:---:|---------|
| `0.0.x` | 已完成的开发迭代 | ❌ | ❌ |
| `0.x.0` | 功能迭代 | ❌ 只作迭代编号 | ❌ |
| `1.0.0-rc.N` | 首个对外灰度（只修 bug） | ✅ | ✅ Pre-release，**不打 `latest`** |
| `1.0.0` | **首个正式版**——公开 API 冻结自此生效 | ✅ | ✅ 含 `latest` |
| `1.x.y` | 修复版本（自这一级起，小版本用于发补丁） | ✅ | ✅ |

- 镜像仓库：`ghcr.io/blue-cloud-net/whalefleet`
- 标签策略：正式版推四个（`v1.2.3` / `1.2.3` / `1.2` / `latest`）；候选版只推两个精确版本
  （`v1.0.0-rc.1` / `1.0.0-rc.1`）
- CHANGELOG：**中英双语全部人工维护**。[Release Please](https://github.com/googleapis/release-please)
  配了 `skip-changelog`，它的版本号 PR 只改版本号；小节必须在**该 PR 内、合并之前**写好——
  合并它就是产生 tag 的动作，两者之间没有时间窗口
- 完整策略见 [docs/overview.md#发布模型与版本号策略](docs/overview.md#发布模型与版本号策略)

---

## ⚠️ 重要局限

- **当前处于 0.x 功能迭代期**，未正式发布；公开 API 在 `1.0.0` 之前可能破坏性变更
- **单实例部署**：SQLite 不支持多实例水平扩展（详见 [docs/constraints.md](docs/constraints.md#sqlite-部署细节)）
- **HTTPS**：镜像内仅监听 HTTP；**生产部署必须前置 HTTPS 反代**——可直接改用的 nginx 与 Traefik 示例见 [examples/reverse-proxy/](examples/reverse-proxy/)，且必须同时设置 `ReverseProxy__*`（见 [.env.example](.env.example)）
- **资源授权粒度**：主机可见性按主机粒度；容器级 RBAC 见 [0.12.0](docs/roadmap.md#0120--资源粒度-rbac)
- **无原生包**：只提供容器化与独立进程两种部署形态；systemd 原生包记录在 [docs/backlog.md](docs/backlog.md)

---

## 📄 许可证

[MIT](LICENSE) © 2026 blue-cloud-net
