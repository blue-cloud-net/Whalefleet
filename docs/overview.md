# 项目概述

## 背景

容器化技术已成为现代应用部署的主流方式，但 Docker 原生 CLI 对于日常运维人员和开发团队来说门槛较高。Portainer 是目前最流行的 Docker Web 管理界面，但其功能复杂、商业版受限，且二次开发难度较大。

Whalefleet 旨在提供一个**轻量、可自托管、易于二次开发**的 Docker 管理平台，以满足中小团队的容器日常运维需求。

## 项目定位

```
Whalefleet vs Portainer
─────────────────────────────────────────────────────────────
特性                  Whalefleet          Portainer
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
3. **安全可控** — 通过 JWT 认证与按资源粒度授权，确保用户只能操作被授权的资源；首日即具备登录认证，0.1.0 起落地主机可见性、0.12.0 起落地容器级 RBAC。
4. **实时反馈** — 通过 SignalR 提供实时日志流、容器状态变更通知（0.5.0 起上线）。
5. **易于部署** — 支持 Docker 容器化部署（推荐 socket-proxy 模式）与独立进程两种方式，一行命令启动。
6. **可观测** — 平台主动报告异常：告警规则体系（0.11.0）把「等人打开页面发现机器挂了」变成「主动推送」。

## 非目标

- **不替代 Kubernetes 管理工具**（如 Lens、Rancher）——专注 Docker 单机与小规模多节点场景。
- **不提供 CI/CD 编排功能**——容器构建与部署编排超出本项目范围。
- **不支持 Docker Swarm 高可用集群管理**——仅针对独立 Docker 主机。
- **不内置监控存储**——实时监控数据通过 SignalR 推送，不持久化历史指标。
- **不做宿主机级实时指标**——Docker API 无此端点，只做容器级（宿主机级需 Agent，1.0.0 之后）。
- **不内置 LLM / 不做 AI 对话界面**——只提供 MCP 服务器（0.18.0 起），AI 部分由客户端承担。
- **不提供应用模板 / GitOps / 计划任务**——推迟到 1.0.0 之后，需求记录在 [backlog.md](./backlog.md)。
- **不在 1.0.0 提供 Agent 模式 / OAuth / 多实例外部数据库**——明确推迟到 1.0.0 之后。
- **不提供公开 demo 实例**——0.x 与 1.0.0 阶段不启动；1.0.0 之后再考虑。

---

## 发布模型与版本号策略

严格遵循**语义化版本**（SemVer），版本号格式 `MAJOR.MINOR.PATCH`。
**唯一目标版本是 `1.0.0`**——`0.x` 全程不对外发布：

| 版本段 | 含义 | 打 tag | 推 GHCR | 何时发布 |
|--------|------|:---:|:---:|---------|
| `0.0.x` | 已完成的开发迭代（快速演进、API 可能破坏性变更） | ❌ | ❌ | 0.0.1 – 0.0.15 已完成 |
| `0.x.0` | **功能迭代**（按模块成块交付） | ❌ 只作迭代编号 | ❌ | 0.1.0 → 0.19.0 |
| `1.0.0-rc.N` | **首个对外灰度**（仅修 bug、不加新功能） | ✅ | ✅ Pre-release，**不打 `latest`** | 0.19.0 走完准入门槛后 |
| `1.0.0` | **首个正式版**（公开 API 冻结承诺从这一版开始） | ✅ | ✅ 含 `latest` | RC 通过后 |
| `1.x.y` | **修复版本**（自这一级起小版本用于发补丁） | ✅ | ✅ | 正式版之后 |

**CI 规则**：`v0.*.*` tag 触发构建但不推 GHCR（并留下可查记录）；
`v1.0.0-rc.*` 推 GHCR 但不打 `latest`；`v1.*.*` 推 GHCR 并含 `latest`。

**镜像标签策略**：正式版推四个（`v1.2.3` / `1.2.3` / `1.2` / `latest`），
候选版只推两个精确版本（`v1.0.0-rc.1` / `1.0.0-rc.1`），不推 sha。

**仓库与发布标识**：

| 项 | 值 |
|---|---|
| GitHub repo | `blue-cloud-net/whalefleet` |
| GitHub Container Registry | `ghcr.io/blue-cloud-net/whalefleet` |
| 主分支 | `main`（仅稳定发布）+ `dev`（集成分支） |
| 许可证 | MIT |

详细路线图见 [roadmap.md](./roadmap.md)。

---

## 镜像分发策略

- **开发期**：前后端分离运行。前端通过 Vite dev server（`src/Whalefleet.UI/`），后端通过 `dotnet run`，通过 Vite 代理 `/api` 与 `/hubs` 到 `http://localhost:8080`。
- **生产期**：**一体化镜像**——单个 GHCR 镜像同时托管前端构建产物（`dist/`）与后端运行时（.NET runtime + ASP.NET Core）。多阶段 Dockerfile 构建，非 root 用户 `app`（UID 10001）运行，根文件系统只读，可写路径仅 `/app/data` 卷。

---

## ⚠️ 安全声明：Docker Socket 挂载

> **Whalefleet 容器挂载 `/var/run/docker.sock` 等于具备宿主机 Docker 的 root 权限**。单容器漏洞 = 宿主机沦陷。

**强烈推荐使用 socket-proxy 模式**（详见 [constraints.md](./constraints.md#docker-socket-挂载-⚠️-高危)）：

```yaml
services:
  socket-proxy:
    image: tecnativa/docker-socket-proxy:v0.5.0
    environment:
      CONTAINERS: 1
      IMAGES: 1
      NETWORKS: 1
      VOLUMES: 1
      POST: 1       # 写操作总闸；关掉则整个 API 只读（列表能用、启停全 403）
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro

  whalefleet:
    image: ghcr.io/blue-cloud-net/whalefleet:latest
    depends_on: [socket-proxy]
    environment:
      # 主机不再是配置项（0.0.8 起为 hosts 表）：默认只有 local（/var/run/docker.sock），
      # 指向 socket-proxy 等其他主机请在启动后通过管理界面添加。
      - PERSISTENCE__DATABASEPATH=/app/data/whalefleet.db
```

socket-proxy 把 Docker API 权限限定到最小分区集。注意 `POST` 这道写操作总闸必须打开——
它关掉时代理只放行 `GET` / `HEAD`，表现为「容器列表正常，但一启停就 403」。
如必须直挂 socket，请确保 Whalefleet 实例仅暴露给可信网络。

完整安全声明见 [README.zh-CN.md](../README.zh-CN.md#️-安全声明docker-socket-挂载)（[English](../README.md#️-security-notice-docker-socket-mounting)）与
[constraints.md](constraints.md#docker-socket-挂载-️-高危)。

---

## 文档矩阵

| 文档 | 说明 |
|------|------|
| [overview.md](./overview.md) | 项目背景、定位、目标与非目标（本文档） |
| [constraints.md](./constraints.md) | 技术栈版本基线、安全 / CI/CD 约束、部署约束 |
| [features.md](./features.md) | 按模块分组的完整功能列表（按版本标注） |
| [backlog.md](./backlog.md) | 明确非目标与待评估项（含 1.0.0 之后的范围） |
| [roadmap.md](./roadmap.md) | 以 `1.0.0` 为目标的版本规划（0.1.0 – 0.19.0 功能迭代 + RC 与正式发布） |
| [architecture.md](./architecture.md) | 系统分层、多主机 DockerClient 工厂、通信机制 |
| [api-spec.md](./api-spec.md) | RPC 风格 API 约定 + SignalR Hub + WebSocket 规范 |
| [ui-guidelines.md](./ui-guidelines.md) | UI 布局规范（整体布局、断点、组件分层、交互约定） |
| [README.md](../README.md) | 项目入口（英文，徽章、`docker run` 一键启动、文档链接） |
| [README.zh-CN.md](../README.zh-CN.md) | 项目入口（中文，与上表内容一一对应） |
| [CHANGELOG.md](../CHANGELOG.md) / [CHANGELOG.zh-CN.md](../CHANGELOG.zh-CN.md) | 变更记录（0.0.1 ~ 0.0.15 的双语版本） |
| [AGENTS.md](../AGENTS.md) | AI 代理协作指南（构建命令、架构铁律、踩坑清单） |

---

## 许可证

[MIT](../LICENSE) © 2026 blue-cloud-net
