# 架构设计

## 系统分层概览

```
┌─────────────────────────────────────────────────────────┐
│                  浏览器 / 客户端                          │
│       Vue 3 + TypeScript + UnoCSS + Element Plus        │
│   Pinia · vue-i18n · @microsoft/signalr · xterm.js      │
└──────────────┬──────────────────────┬───────────────────┘
               │ HTTP (RPC API)       │ WebSocket (SignalR / 独立)
               ▼                      ▼
┌─────────────────────────────────────────────────────────┐
│              ASP.NET Core 10 宿主进程                    │
│                                                         │
│  ┌────────────────┐      ┌──────────────────────────┐  │
│  │  API 层        │      │  SignalR Hub 层           │  │
│  │  /api/v1/...   │      │  ContainerLogHub         │  │
│  │  Controllers   │      │  ContainerStatsHub       │  │
│  └───────┬────────┘      │  NotificationHub         │  │
│          │               └─────────────┬─────────────┘  │
│  ┌───────▼─────────────────────────────▼──────────────┐ │
│  │                 Service 层                          │ │
│  │  ContainerService  ImageService  VolumeService    │ │
│  │  NetworkService    HostService   UserService      │ │
│  │  AuthService       PermissionService              │ │
│  └───────┬─────────────────────────────┬──────────────┘ │
│          │                             │                │
│  ┌───────▼────────┐      ┌─────────────▼────────────┐ │
│  │  Docker 接入层  │      │  数据访问层                │ │
│  │  Core 抽象     │      │  Dapper + SQLite           │ │
│  │  Infra 实现    │      │  + WAL 模式                │ │
│  │  DockerClient  │      │  + Argon2id                │ │
│  │  Builder 风格  │      └────────────────────────────┘ │
│  └───────┬────────┘                                     │
└──────────┼──────────────────────────────────────────────┘
           │ unix:///var/run/docker.sock 或 tcp://host:port
           ▼
┌─────────────────────────┐
│   Docker 守护进程        │
│   多主机（0.0.8 起）    │
└─────────────────────────┘
```

---

## 各层职责

> 0.0.1 起采用 **DDD 分层（4 个后端项目）**。本节后续描述保持与原文档兼容，仅关键术语对齐 DDD：
> - "API 层" → **接口层（Api / Presentation）**
> - "Service 层" → **应用层（Application）**
> - "Docker 接入层 Core 抽象" → **应用层 Abstractions**
> - "数据访问层" → 仓储接口在 **Domain**，实现在 **Infrastructure/Persistence**
>
> 完整 DDD 规范见 [ddd-guide.md](./ddd-guide.md)。

### DDD 依赖方向

项目级依赖图（箭头方向 = 引用方向；箭头尾部依赖箭头头部）：

```
                              ┌──────────────────────────────┐
                              │  Docker.Manager.Domain        │
                              │  ───────────────────────────  │
                              │  Entity / ValueObject /       │
                              │  IAggregateRoot / IDomainEvent│
                              │  DomainException              │
                              │  仓储接口（0.0.3 起）          │
                              │  仅依赖 BCL，无 NuGet          │
                              └──────────────┬───────────────┘
                                             │ ▲
                  ┌──────────────────────────┘ │ ┌──────────────────────────┐
                  │                            │ │                          │
                  ▼                            │ ▼                          │
       ┌─────────────────────────┐             │       ┌─────────────────────────┐
       │  Application            │             │       │  Infrastructure          │
       │  ─────────────────────  │             │       │  ─────────────────────  │
       │  应用服务（用例编排）    │             │       │  Docker.DotNet 适配      │
       │  外部资源接口抽象       │             │       │  仓储实现                │
       │  (IDockerClientFactory) │             │       │  第三方 SDK 集成         │
       │  DTO 映射              │             │       │                          │
       └──────────┬──────────────┘             │       └──────────┬──────────────┘
                  │                            │                  │
                  │     ┌──────────────────────┘                  │
                  │     │                                          │
                  ▼     ▼                                          ▼
       ┌──────────────────────────────────────────────────────────────────┐
       │                       Docker.Manager.Api                          │
       │  ─────────────────────────────────────────────────────────────  │
       │  ASP.NET Core 10 宿主进程                                          │
       │  Controllers / SignalR Hubs / 静态前端托管                        │
       │  Serilog / Kestrel / 配置 / DI 编排                                │
       └──────────────────────────────────────────────────────────────────┘
```

**依赖方向规则**：

| 项目 | 可依赖 | 不可依赖 |
|------|--------|---------|
| `Domain` | BCL | Application、Infrastructure、Api、任何 NuGet |
| `Application` | Domain | Infrastructure、Api |
| `Infrastructure` | Domain、Application | Api |
| `Api` | Application、Infrastructure | （已是最外层） |
| `UnitTests` | Domain、Application | Infrastructure、Api |

**违反依赖方向的检测手段**：

```bash
# 1) Domain 项目应无任何 NuGet 依赖
dotnet list src/Docker.Manager.Domain/Docker.Manager.Domain.csproj package --include-transitive

# 2) Application 应仅依赖 Domain（一个 ProjectReference，无 NuGet）
dotnet list src/Docker.Manager.Application/Docker.Manager.Application.csproj package
```

### API 层（Controllers）

- 接收 HTTP 请求，解析入参，调用 Service 层
- 统一响应格式封装（`ApiResult<T>`）
- JWT 鉴权 + `[Authorize(Roles = "admin")]` 角色检查
- 不包含业务逻辑

### SignalR Hub 层（0.3.0 起）

| Hub | 职责 |
|-----|------|
| `ContainerLogHub` | 客户端订阅指定容器的实时日志流 |
| `ContainerStatsHub` | 客户端订阅指定容器的实时 CPU/内存/网络统计 |
| `NotificationHub` | 推送系统事件（容器异常退出等通知） |

Hub 鉴权：JWT 通过 `?access_token=...` 查询参数传递（WebSocket 不支持自定义 Header）。

### Service 层

- 封装业务逻辑与 Docker.DotNet 调用细节
- 隔离 Controller 与 Docker SDK 的直接耦合
- 封装 Dapper Repository 调用（用户、授权、主机相关数据）
- 通过 `IDockerHostAccessor` 解析当前请求的 Docker 主机客户端

### Docker 接入层

- **`Application/Abstractions` 层定义接口**：`IDockerClientFactory`、`IDockerHostAccessor`、`IContainerOperations`、`IImageOperations` 等
- **`Infrastructure/Docker` 层实现**：基于 [Docker.DotNet.Enhanced](https://github.com/testcontainers/Docker.DotNet)（fork），使用 `DockerClientBuilder` 风格
- **0.0.8 起支持多主机**：根据 `hostId` 动态创建并缓存 `DockerClient` 实例
- **后续**：支持 TCP 连接（`tcp://host:port`）与 TLS / Basic 证书，通过 `hosts` 表动态创建

### 数据访问层

- **仓储接口**（`IUserRepository`、`IHostRepository` 等）定义在 `Docker.Manager.Domain/Repositories/`
- **仓储实现**位于 `Docker.Manager.Infrastructure/Persistence/`：使用 `Dapper` 执行手写 SQL
- 数据库：SQLite + `Microsoft.Data.Sqlite`，**WAL 模式**启用（PRAGMA journal_mode=WAL）
- 文件存储：`/app/data/docker-manager.db`
- 应用启动时自动执行建表 SQL（若表不存在）
- 存储内容：用户信息、资源授权关系、Docker 主机配置、Registry 凭据（加密）

---

## 多主机架构（0.0.8 起）

```
HTTP Request                ASP.NET Core               Docker.DotNet
─────────────              ─────────────              ────────────
                          ┌──────────────────┐
X-Host-Id: prod-host-1 →  │ IDockerHost     │  →  ┌──────────────────┐
                          │ Accessor (Scoped)│     │ IDockerClient   │
                          │                  │     │ Factory         │
                          │ 解析 hostId      │     │                  │
                          │ 查 hosts 表      │     │ 缓存 / 按需创建  │
                          │ 校验可见性       │     │ DockerClient    │
                          └────────┬─────────┘     └────────┬─────────┘
                                   │                        │
                                   ▼                        ▼
                          ┌──────────────────┐     ┌──────────────────┐
                          │ IDockerClient    │ →  │ Docker.DotNet    │
                          │ (hostId 对应的   │     │ .Enhanced        │
                          │  缓存实例)       │     │ DockerClient     │
                          └──────────────────┘     └──────────────────┘
```

### `IDockerHostAccessor`（Scoped 服务）

从请求 Header `X-Host-Id`（缺省 `"local"`）解析当前 host：

```csharp
public interface IDockerHostAccessor
{
    string HostId { get; }              // 当前请求的 host id
    Task<Host> GetCurrentAsync();       // 从 hosts 表查当前 host 配置
    Task<bool> CanAccessAsync(string hostId);  // 检查当前用户是否有权限访问
}
```

### `IDockerClientFactory`（Singleton 服务）

按 `hostId` 返回缓存的 `DockerClient`：

```csharp
public interface IDockerClientFactory
{
    Task<DockerClient> GetClientAsync(string hostId);
    Task<bool> TestConnectionAsync(string endpoint, HostType type, AuthConfig? auth);
}
```

**实现使用 `DockerClientBuilder` 风格**：

| 主机类型 | 构造方式 |
|---------|---------|
| **socket** | `new DockerClientBuilder().WithEndpoint(new Uri("unix:///var/run/docker.sock")).Build()` |
| **tcp（无认证）** | `new DockerClientBuilder().WithEndpoint(new Uri("tcp://host:2375")).Build()` |
| **tcp + TLS** | `WithEndpoint(uri).WithAuthProvider(new CertificateCredentials(cert))` |
| **tcp + Basic** | `WithEndpoint(uri).WithAuthProvider(new BasicAuthCredentials(user, pass))` |

**多传输支持**（`Docker.DotNet.Enhanced` 提供）：

| 传输 | 用途 |
|------|------|
| `LegacyHttpTransportOptions` | 默认 HTTP 传输 |
| `NativeHttpTransportOptions` | 启用 `.NET Native HTTP Handler`（实验性，性能更好） |
| `UnixSocketTransportOptions` | Linux/macOS Unix domain socket |
| `NPipeTransportOptions` | Windows named pipe |

### 前端集成

- **顶栏 `HostSelector` 组件**：显示当前主机 + 在线状态点；列出可访问主机；admin 可见「管理主机」入口
- **切换主机**：写入 Pinia `useHostStore`，所有后续 axios 请求通过 `composables/useDockerRequest.ts` 拦截器自动带 `X-Host-Id` Header

### 默认主机（0.0.8 启动时）

启动时若 `hosts` 表为空，自动插入默认主机：

```sql
INSERT INTO hosts (id, name, type, endpoint, enabled, created_at)
VALUES ('00000000-0000-0000-0000-000000000001', 'local', 'socket', '/var/run/docker.sock', 1, datetime('now'));
```

---

## 鉴权架构

### JWT 流程

```
                    ┌──────────────┐
                    │ LoginView    │
                    │ (前端)        │
                    └──────┬───────┘
                           │ POST /api/v1/auth/login
                           │ {username, password}
                           ▼
                    ┌──────────────┐
                    │ AuthController│
                    │ + AuthService│
                    └──────┬───────┘
                           │ Argon2id 校验
                           │ 生成 access (15min) + refresh (7d)
                           ▼
                    ┌──────────────┐
                    │ 数据库        │
                    │ refresh_tokens│
                    │ (可撤销表)    │
                    └──────┬───────┘
                           │
                           ▼ accessToken + refreshToken 返回前端
                    ┌──────────────┐
                    │ Pinia        │
                    │ useUserStore │
                    │ localStorage │
                    └──────────────┘
```

后续请求自动带 `Authorization: Bearer <accessToken>`。

**Access Token 过期时**：前端 axios 拦截器捕获 401 → 调用 `POST /api/v1/auth/refresh` → 用 refresh token 换新 access token → 重发原请求。

### SignalR Hub 鉴权（0.3.0）

WebSocket 不支持自定义 Header，JWT 通过 `?access_token=...` 查询参数传递：

```typescript
const connection = new HubConnectionBuilder()
  .withUrl(`/hubs/container-log?access_token=${accessToken}`)
  .build()
```

### WebSocket Terminal 鉴权（0.4.0）

为最小化 access token 泄漏风险，**独立短时 terminal token**：

```
1. 前端请求 POST /api/v1/auth/terminal-token
   → 返回 5 分钟过期 terminalToken（仅能用于 /ws/container/exec）

2. 前端建立 WebSocket:
   new WebSocket(`ws://host/ws/container/exec?containerId=xxx&cmd=/bin/sh&token=${terminalToken}`)

3. 后端验证 terminalToken 有效性 + 检查用户对 containerId 的权限

4. 建立双向流；Docker.DotNet exec + attach 桥接到 WebSocket
```

---

## SDK 隔离设计

为避免 Docker SDK 类型泄漏到上层，并支持未来切换：

```
┌──────────────────────────────────────────┐
│ API 层 (Docker.Manager.Api)              │
│   - Controllers 只引用 Core 接口          │
└──────────────────┬───────────────────────┘
                   │
┌──────────────────▼───────────────────────┐
│ Core 层 (Docker.Manager.Core)            │
│   - IDockerClientFactory (接口)          │
│   - IDockerHostAccessor  (接口)          │
│   - 业务模型 (Host, ContainerDto, ...)   │
│   - 错误码 + ApiResult<T>                 │
└──────────────────┬───────────────────────┘
                   │
┌──────────────────▼───────────────────────┐
│ Infrastructure 层 (Docker.Manager.Infra) │
│   - DockerClientFactoryImpl (实现)        │
│   - DockerHostAccessorImpl  (实现)        │
│   - ContainerRepository, ...             │
│   - 引用 Docker.DotNet.Enhanced          │
└──────────────────────────────────────────┘
```

**关键收益**：
- Controller / Service 仅依赖 `Core` 接口，**与 SDK 解耦**
- 未来切换 SDK（如回退到上游 `Docker.DotNet` 3.x 或迁移到其他 Docker SDK）只需重写 `Infrastructure` 层
- 单测可 Mock `IDockerClientFactory`，无需真实 Docker

---

## 容器镜像构建（一体化）

```dockerfile
# Stage 1: 前端构建
FROM node:20-alpine AS frontend
WORKDIR /src/web
COPY src/web/package*.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build  # 产物 → dist/

# Stage 2: 后端构建
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish src/Docker.Manager.Api -c Release -o /app/publish

# Stage 3: 运行时镜像
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN useradd -u 10001 -m app
WORKDIR /app
COPY --from=backend --chown=app:app /app/publish ./
COPY --from=frontend --chown=app:app /src/web/dist ./wwwroot
COPY scripts/backup.sh /app/scripts/
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
  CMD wget -qO- http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Docker.Manager.Api.dll"]
```

---

## 前端架构

### 目录结构

```
src/web/src/
├── api/                   # API 封装（RPC 风格方法）
├── assets/                # 静态资源
├── components/
│   ├── common/            # 通用组件（EmptyState、StatusBadge、JsonTree、CommandPalette）
│   └── business/          # 业务组件（ContainerStatusBadge、PortMappingEditor、EnvVarEditor）
├── composables/           # 复用 Composition 函数
│   ├── useTheme.ts
│   ├── useShortcuts.ts
│   └── useDockerRequest.ts  # 自动注入 X-Host-Id
├── layouts/
│   ├── AppLayout.vue
│   └── AuthLayout.vue
├── locales/               # i18n 语言文件
├── router/
│   ├── index.ts
│   └── guards.ts
├── stores/                # Pinia
├── styles/
│   ├── tokens.css         # CSS 变量
│   ├── uno.css            # UnoCSS 入口
│   └── overrides.css      # Element Plus 主题覆盖
├── types/                 # TypeScript 类型
├── utils/
└── views/                 # 路由对应的页面组件
```

### 通信方式

| 场景 | 方式 |
|------|------|
| 普通数据请求 | HTTP POST（axios），RPC 风格路径 |
| 实时日志（0.3.0） | SignalR WebSocket，`ContainerLogHub` |
| 实时监控（0.3.0） | SignalR WebSocket，`ContainerStatsHub` |
| 页面内通知（0.3.0） | SignalR WebSocket，`NotificationHub` |
| 容器终端（0.4.0） | 原生 WebSocket `/ws/container/exec` + terminal token |

---

## 统一响应格式

所有 HTTP API 均返回统一结构：

```json
{
  "success": true,
  "code": 0,
  "message": "ok",
  "data": { }
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `success` | bool | 请求是否成功 |
| `code` | int | 业务错误码（0 = 成功） |
| `message` | string | 错误描述（成功时为 "ok"） |
| `data` | object / null | 返回数据主体 |

完整错误码分段见 [api-spec.md](./api-spec.md#业务错误码)。

---

## 部署架构

### Docker Socket Proxy 模式（推荐）

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
    restart: unless-stopped

  docker-manager:
    image: ghcr.io/blue-cloud-net/docker-manager:latest
    depends_on: [socket-proxy]
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - JWT__Secret=<your-strong-secret-≥32-bytes>
      - ADMIN__USER=admin
      - ADMIN__PASSWORD=<initial-strong-password>
      - DEFAULT_HOST__ENDPOINT=tcp://socket-proxy:2375
    volumes:
      - ./data:/app/data
      - ./scripts:/app/scripts
    restart: unless-stopped
```

### 独立进程部署

```
dotnet Docker.Manager.Api.dll --urls http://0.0.0.0:8080
```

要求宿主机当前用户对 `/var/run/docker.sock` 有读写权限（通常需要加入 `docker` 用户组）。
