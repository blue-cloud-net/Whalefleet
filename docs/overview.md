# 项目概述

## 背景

容器化技术已成为现代应用部署的主流方式，但 Docker 原生 CLI 对于日常运维人员和开发团队来说门槛较高。Portainer 是目前最流行的 Docker Web 管理界面，但其功能复杂、商业版受限，且二次开发难度较大。

Docker.Manager 旨在提供一个**轻量、可自托管、易于二次开发**的 Docker 管理平台，以满足中小团队的容器日常运维需求。

## 项目定位

```
Docker.Manager vs Portainer
─────────────────────────────────────────────────────────────
特性                  Docker.Manager          Portainer
─────────────────────────────────────────────────────────────
开源协议              MIT（完全开放）          CE 版 zlib，EE 商业
技术栈               .NET 10 + Vue 3          Go + React
二次开发             易于定制                 改动成本高
功能范围             聚焦核心运维场景          功能全面但复杂
多主机               分阶段支持               CE 版受限
─────────────────────────────────────────────────────────────
```

## 核心目标

1. **降低容器管理门槛** — 通过直观的 Web UI，让不熟悉 Docker CLI 的人员也能轻松管理容器。
2. **覆盖核心运维场景** — 容器生命周期管理、镜像管理、日志查看是最高频的使用场景，优先保障。
3. **安全可控** — 通过 JWT 认证与按资源粒度授权，确保用户只能操作被授权的资源；首日即具备登录认证，0.3.0 起落地资源粒度 RBAC。
4. **实时反馈** — 通过 SignalR 提供实时日志流、容器状态变更通知（0.3.0 起上线）。
5. **易于部署** — 支持 Docker 容器化部署（推荐 socket-proxy 模式）与独立进程两种方式，一行命令启动。

## 非目标

- **不替代 Kubernetes 管理工具**（如 Lens、Rancher）——专注 Docker 单机与小规模多节点场景。
- **不提供 CI/CD 流水线功能**——容器构建与部署编排超出本项目范围。
- **不支持 Docker Swarm 高可用集群管理**——仅针对独立 Docker 主机。
- **不内置监控存储**——实时监控数据通过 SignalR 推送，不持久化历史指标（v1.0 范围）。
- **不在 v1.0 提供 Agent 模式 / OAuth / 审计日志 / 多实例外部数据库**——明确推迟到 1.0.0 之后。
- **不提供公开 demo 实例**——0.x 与 1.0.0 阶段不启动；1.0.0 之后再考虑。

---

## 发布模型与版本号策略

严格遵循**语义化版本**（SemVer），版本号格式 `MAJOR.MINOR.PATCH`：

| 版本段 | 含义 | 公开发布？ | 何时发布 |
|--------|------|-----------|---------|
| `0.0.x` | 未正式发布的开发迭代（快速演进、API 可能破坏性变更） | ❌ 不发布到 GHCR，仅自测 | 每个迭代完成即打 tag 内部记录 |
| `0.1.0-rc.N` | MVP 候选发布（仅修 bug、不加新功能） | ⚠️ 灰度发布到 GHCR（Pre-release） | 0.0.13 后跑通验收后启动 |
| `0.1.0` | **MVP 首个 semver 稳定版**（公开 API 冻结承诺从这一版开始） | ✅ 正式发布 | RC 通过后 |
| `0.2.0` / `0.3.0` / `0.4.0` | 功能里程碑版本 | ✅ 正式发布 | 每 Phase 完成后 |
| `1.0.0` | **生产就绪版**（经过真实环境压测与安全审计） | ✅ 正式发布 | 0.4.0 稳定运行后 |

**CI 规则**：`v0.0.*` tag 触发构建但不推 GHCR；`v0.1.0-rc.*` / `v0.x.0` / `v1.0.0` tag 触发构建 + 推 GHCR + 创建 GitHub Release。

**镜像标签策略**：仅 semver（`v0.1.0` / `0.1.0` / `0.1` / `latest`），简洁优先。

**仓库与发布标识**：

| 项 | 值 |
|---|---|
| GitHub repo | `blue-cloud-net/docker-manager` |
| GitHub Container Registry | `ghcr.io/blue-cloud-net/docker-manager` |
| 主分支 | `main`（仅稳定发布）+ `develop`（集成分支） |
| 许可证 | MIT |

详细路线图见 [roadmap.md](./roadmap.md)。

---

## 镜像分发策略

- **开发期**：前后端分离运行。前端通过 Vite dev server（`src/web/`），后端通过 `dotnet run`，通过 Vite 代理 `/api` 与 `/hubs` 到 `http://localhost:8080`。
- **生产期**：**一体化镜像**——单个 GHCR 镜像同时托管前端构建产物（`dist/`）与后端运行时（.NET runtime + ASP.NET Core）。多阶段 Dockerfile 构建，非 root 用户 `app`（UID 10001）运行，根文件系统只读，可写路径仅 `/app/data` 卷。

---

## ⚠️ 安全声明：Docker Socket 挂载

> **Docker.Manager 容器挂载 `/var/run/docker.sock` 等于具备宿主机 Docker 的 root 权限**。单容器漏洞 = 宿主机沦陷。

**强烈推荐使用 socket-proxy 模式**（详见 [constraints.md](./constraints.md#docker-socket-挂载-⚠️-高危)）：

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
    environment:
      # 主机不再是配置项（0.0.8 起为 hosts 表）：默认只有 local（/var/run/docker.sock），
      # 指向 socket-proxy 等其他主机请在启动后通过管理界面添加。
      - PERSISTENCE__DATABASEPATH=/app/data/docker-manager.db
```

socket-proxy 作为只读代理，将 Docker API 权限限定到最小子集。如必须直挂 socket，请确保 Docker.Manager 实例仅暴露给可信网络。

完整安全策略见仓库根 [SECURITY.md](../SECURITY.md)。

---

## 文档矩阵

| 文档 | 说明 |
|------|------|
| [overview.md](./overview.md) | 项目背景、定位、目标与非目标（本文档） |
| [constraints.md](./constraints.md) | 技术栈版本基线、安全 / CI/CD 约束、部署约束 |
| [features.md](./features.md) | 按模块分组的完整功能列表（按版本标注） |
| [roadmap.md](./roadmap.md) | 17 个版本迭代（0.0.1 ~ 1.0.0）的路线图 |
| [architecture.md](./architecture.md) | 系统分层、多主机 DockerClient 工厂、通信机制 |
| [api-spec.md](./api-spec.md) | RPC 风格 API 约定 + SignalR Hub + WebSocket 规范 |
| [ui-guidelines.md](./ui-guidelines.md) | UI 布局规范（整体布局、断点、组件分层、交互约定） |
| [README.md](../README.md) | 项目入口（徽章、`docker run` 一键启动、文档链接） |

---

## 许可证

[MIT](../LICENSE) © 2026 blue-cloud-net
