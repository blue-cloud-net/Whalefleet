# Docker.Manager

> 一个类 Portainer 的轻量化 Docker 管理平台。基于 Web 的可视化界面，用于管理本地及远程 Docker 主机上的容器、镜像、Volume、Network 等资源。
>
> **后端** .NET 10 LTS + Dapper + SQLite + SignalR ｜ **前端** Vue 3.5+ + TypeScript 5.7+ + UnoCSS + Element Plus 2.14+ ｜ **Docker SDK** [Docker.DotNet.Enhanced](https://github.com/testcontainers/Docker.DotNet) 4.3.3（Docker Engine API v29.4.1）

[![MIT License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![CI](https://github.com/blue-cloud-net/docker-manager/actions/workflows/ci.yml/badge.svg)](https://github.com/blue-cloud-net/docker-manager/actions/workflows/ci.yml)
[![.NET 10 LTS](https://img.shields.io/badge/.NET-10_LTS-512BD4)](https://dotnet.microsoft.com)
[![Vue 3.5+](https://img.shields.io/badge/Vue-3.5+-42b883)](https://vuejs.org)
[![Docker Engine API v29.4.1](https://img.shields.io/badge/Docker_Engine_API-v29.4.1-2496ED)](https://github.com/testcontainers/Docker.DotNet)
[![GHCR](https://img.shields.io/badge/GHCR-blue--cloud--net%2Fdocker--manager-2496ED)](https://ghcr.io/blue-cloud-net/docker-manager)

> 当前阶段：**0.0.x 开发迭代期**（未正式发布，0.0.* tag 不推 GHCR）。首个 semver 稳定版 `v0.1.0` 路线图见 [docs/roadmap.md](docs/roadmap.md)。

---

## ✨ 核心特性

- 🐳 **容器全生命周期管理**：创建 / 启动 / 停止 / 重启 / 删除 / 批量删除 / 日志查看 / 详情（0.0.9 + 0.2.0）
- 🖼 **镜像管理**：拉取（含进度）/ 删除 / 批量删除 / 详情；0.2.0 起支持 Dockerfile 构建；0.4.0 起支持 push 到私有 Registry
- 💾 **Volume / Network 完整 CRUD**（0.2.0）
- 🖥 **多主机管理**：本地 socket + 远程 TCP（可选 TLS / Basic 认证），顶栏切换（0.0.8 起）
- 🔐 **首日鉴权**：JWT + refresh token + Argon2id 密码哈希 + 登录速率限制（0.0.4 起）
- 👥 **admin / user 二角色 + 主机粒度可见性**（0.1.0）；**资源粒度 RBAC**（0.3.0）
- 🔔 **实时推送**（0.3.0）：SignalR 实时日志流 / CPU 内存网络监控 / 容器异常通知
- 💻 **Web 终端**（0.4.0）：xterm.js + 独立短时 terminal token
- 📦 **Docker Compose / Stack 管理**（0.4.0）
- 🎨 **现代 UI**：UnoCSS + Element Plus + 命令面板（`Ctrl/Cmd+K`）+ `g` 前缀全局快捷键 + 亮/暗主题 + 三色主题色

---

## ⚠️ 安全声明：Docker Socket 挂载

> **挂载 `/var/run/docker.sock` 等于赋予容器内进程对宿主机 Docker 的 root 权限**——单容器漏洞 = 宿主机沦陷。

**强烈推荐使用 socket-proxy 模式**部署：

```yaml
services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro

  docker-manager:
    image: ghcr.io/blue-cloud-net/docker-manager:latest
    depends_on: [socket-proxy]
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - JWT__SECRET=<your-strong-secret-≥32-bytes>
      - ADMIN__USER=admin
      - ADMIN__PASSWORD=<initial-strong-password>
      # 主机不再是配置项（0.0.8 起为 hosts 表）：默认只有 local（/var/run/docker.sock），
      # 指向 socket-proxy 等其他主机请在启动后通过管理界面添加。
    volumes:
      - ./data:/app/data
      - ./scripts:/app/scripts
    restart: unless-stopped
```

如必须**直挂 socket**，请确保 Docker.Manager 实例仅暴露给可信网络，并优先前置 HTTPS 反代（镜像内仅监听 HTTP）。完整的部署约束与安全边界见 [docs/constraints.md](docs/constraints.md)。

---

## 🚀 本地启动

> **本项目当前处于 0.0.x 开发迭代期，尚未发布容器镜像。**
> 下面的 socket-proxy 示例描述的是**目标形态**（`Dockerfile` 与 `docker-compose.yml`
> 随 [0.0.15](docs/roadmap.md#015--部署底座与发布流水线) 一起交付）。
> 在那之前，请用「方式一：从源码运行」。

### 方式一：从源码运行（当前唯一可用方式）

```bash
# 1. 克隆并进入仓库
git clone https://github.com/blue-cloud-net/docker-manager.git
cd docker-manager

# 2. 后端（需要 .NET 10 SDK）
dotnet run --project src/Docker.Manager.Api --urls http://localhost:8080

# 3. 前端（另开一个终端，需要 Node.js 20+ 与 pnpm）
cd src/Docker.Manager.UI
pnpm install
pnpm dev:mock   # 用内置假数据体验界面，无需后端与 Docker
#   或 pnpm dev   # 连真实后端（Vite 会把 /api 代理到 :8080）
```

首次启动时若 `users` 表为空**且未配置管理员**，则不会创建账号（只打一条 Warning）。
要登录必须显式设置 `ADMIN__USER` / `ADMIN__PASSWORD` 后重启；密码不满足强度要求会
直接让启动失败——这是「系统里还没有任何账号」的唯一时机，放行弱密码等于把门开着不管。

### 环境变量

| 变量 | 默认 | 说明 |
|------|------|------|
| `ASPNETCORE_ENVIRONMENT` | `Production` | 设为 `Development` 会输出详细错误 |
| `JWT__SECRET` | 开发环境有内置值 | **生产必须显式设置**，至少 32 字节；缺失或过短会导致启动失败 |
| `ADMIN__USER` | （无默认） | 首次启动创建的管理员用户名；未配置则不创建账号 |
| `ADMIN__PASSWORD` | （无默认） | 同上，且必须满足密码强度要求 |

### 方式二：容器化启动（0.0.15 起可用）

届时一条命令即可启动，镜像推送到 `ghcr.io/blue-cloud-net/docker-manager`：

```bash
docker run -d \
  --name docker-manager \
  -p 8080:8080 \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v $(pwd)/data:/app/data \
  -e JWT__SECRET=$(openssl rand -hex 32) \
  -e ADMIN__USER=admin \
  -e ADMIN__PASSWORD='StrongP@ss123' \
  --restart unless-stopped \
  ghcr.io/blue-cloud-net/docker-manager:latest
```

**强烈建议改为 socket-proxy**（见上方安全声明）——直挂 socket 等于把宿主机的
Docker root 权限交给容器内的任何进程。

> 主机**不再是配置项**：0.0.8 起主机存在 `hosts` 表里，远程 Docker 主机在启动后
> 通过管理界面添加。

---

## 📸 功能截图

> _占位：0.1.0-rc.1 阶段补充截图_

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
| [docs/roadmap.md](docs/roadmap.md) | 17 个版本迭代（0.0.1 ~ 1.0.0）的完整路线图 |
| [docs/architecture.md](docs/architecture.md) | 系统分层、多主机 DockerClient 工厂、SDK 隔离、通信机制 |
| [docs/api-spec.md](docs/api-spec.md) | RPC 风格 API 约定 + 错误码分段 + SignalR Hub + WebSocket 终端 |
| [docs/ui-guidelines.md](docs/ui-guidelines.md) | UI 布局规范（布局图 / 断点 / 组件分层 / 快捷键 / 主题 / 路由） |

---

## 🛠 技术栈一览

**后端**：.NET 10 LTS / ASP.NET Core 10 / SQLite + Dapper / SignalR / Serilog / `Docker.DotNet.Enhanced`

**前端**：Vue 3.5+ / TypeScript 5.7+ / Vite 7+ / UnoCSS + Element Plus 2.14+ / Pinia 4 / vue-i18n 11 / xterm.js 5.5+

**配套**：Central Package Management（NuGet）/ pnpm（前端）/ xUnit + Testcontainers（集成测试）/ Vitest（前端测试）

完整版本约束见 [docs/constraints.md](docs/constraints.md)。

---

## 🤝 参与贡献

欢迎贡献代码、报告 Bug、提出功能建议。

- 提 Issue：[GitHub Issues](https://github.com/blue-cloud-net/docker-manager/issues)
- 提 PR：请先开 Issue 讨论变更方向
- 报告安全漏洞：请**不要**在公开 Issue 中披露（这等于告知攻击者具体漏洞细节）

开发环境：.NET 10 SDK + Node.js 20+ + pnpm。

---

## 📋 版本与发布

| 版本段 | 含义 | 公开发布？ |
|--------|------|-----------|
| `0.0.x` | 未正式发布的开发迭代 | ❌ 不发布到 GHCR |
| `0.1.0-rc.N` | MVP 候选发布 | ⚠️ Pre-release |
| `0.1.0` | **MVP 首个 semver 稳定版** | ✅ 正式发布 |
| `0.2.0` / `0.3.0` / `0.4.0` | 功能里程碑版本 | ✅ 正式发布 |
| `1.0.0` | **生产就绪版** | ✅ 正式发布 |

- 镜像仓库：`ghcr.io/blue-cloud-net/docker-manager`
- 标签策略：仅 semver（`v0.1.0` / `0.1.0` / `0.1` / `latest`）
- CHANGELOG：由 Release Please 自动生成（0.0.15 起）
- 完整策略见 [docs/overview.md#发布模型与版本号策略](docs/overview.md)

---

## ⚠️ 重要局限

- **当前处于 0.0.x 开发迭代期**，未正式发布；API 可能破坏性变更
- **单实例部署**：SQLite 不支持多实例水平扩展（详见 [docs/constraints.md](docs/constraints.md#sqlite-部署细节)）
- **HTTPS**：镜像内仅监听 HTTP；**生产部署必须前置 HTTPS 反代**（Caddy / Traefik / nginx）
- **资源授权粒度**：0.1.0 仅按主机粒度；容器级资源粒度 RBAC 见 [0.3.0](docs/roadmap.md#03.0--实时推送与资源粒度-rbac)

---

## 📄 许可证

[MIT](LICENSE) © 2026 blue-cloud-net
