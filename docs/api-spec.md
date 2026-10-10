# API 规范

## RPC 风格约定

本项目 API 采用 **RPC 风格**（非 RESTful）：

- 所有请求使用 `POST` 方法
- URL 表达动作，格式为 `/api/v1/{resource}/{action}`
- 参数统一放在请求 Body（JSON）中，不使用路径参数与查询字符串（分页除外）
- 响应体统一使用 `ApiResult<T>` 格式（见「统一响应格式」）

**示例对比**

| RESTful（不采用） | RPC 风格（本项目） |
|---|---|
| `PUT /api/v1/containers/{id}` | `POST /api/v1/container/start` |
| `DELETE /api/v1/containers/{id}` | `POST /api/v1/container/remove` |
| `GET /api/v1/containers` | `POST /api/v1/container/list` |

---

## 统一响应格式

```json
{
  "success": true,
  "code": 0,
  "message": "ok",
  "data": null
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| `success` | bool | 请求是否成功 |
| `code` | int | 业务错误码（0 = 成功） |
| `message` | string | 错误描述（成功时为 "ok"） |
| `data` | object / null | 返回数据主体 |

### 响应约定（0.0.2 起）

| 项 | 约定 |
|----|------|
| 序列化 | camelCase；字段固定为 `success` / `code` / `message` / `data` 四项 |
| 字符编码 | `JavaScriptEncoder.Create(UnicodeRanges.All)`——中文等非 ASCII 字符**原样输出**（不是 `\uXXXX` 转义），但 `<` `>` `&` `'` `"` `+` 仍被转义，以消除 JSON 被嵌入 HTML 时的注入风险。MVC 与错误响应序列化器共用 `ApiJsonDefaults`，不得各自配置 |
| `data` 字段 | **始终存在**。失败响应显式输出 `"data": null`（不得为 JSON 序列化开启 `WhenWritingNull`） |
| Content-Type | `application/json; charset=utf-8` |
| 构造方式 | 统一使用 `ApiResults.Ok(...)` / `ApiResults.Fail<T>(...)`，不要直接调用构造函数（避免 `success` 与 `code` 不自洽） |
| 无数据接口 | 使用非泛型 `ApiResult`（不含 `data` 字段）；需要显式 `data: null` 的场景用 `ApiResults.Fail<T>` |
| 错误信息安全 | 错误响应**绝不**包含堆栈、内部异常消息、异常类型名或源码位置；`code = 9999` 的消息被强制替换为固定文案 |

### 错误码 → HTTP 状态码

`success = false` 时，HTTP 状态码由业务错误码**逐码显式映射**（实现见 `Docker.Manager.Api.Errors.ErrorHttpStatus`，完整对照见下文「当前已分配的错误码」表格的 HTTP 列）。

> ⚠️ **不能按错误码数值分段推断状态码**：同一分段内语义并不统一——`1002`（段 1000-1099）是 500、`1103`（段 1100-1199）是 429、`1301`（段 1300-1399）是 502。分类与映射分两层：错误码 → 语义分类在应用层（`ErrorCodeCatalog`），语义分类 → HTTP 状态码在接口层（`ErrorHttpStatus`）。

### 无响应体状态码的包装

框架在管道末端直接产生、**没有响应体**的 4xx/5xx（未知路由 404、方法不允许 405、不支持的媒体类型 415 等）不会被 Controller 或异常中间件处理，由 `UseStatusCodePages` 统一包装为上述 `ApiResult` 结构。反查规则：

| HTTP | 业务错误码 |
|------|-----------|
| `401` | `1100` |
| `403` | `1500` |
| `404` | `1001` |
| `408` | `1003` |
| `>= 500` | `9999` |
| 其他 4xx（400 / 405 / 415 等） | `1000` |

请求体模型校验失败（ModelState）走同一套结构，固定返回 `1000` / HTTP 400，`message` 聚合字段级错误。

### 例外：基础设施端点

`GET /healthz` **不适用**上述信封——它属于基础设施探针（Docker `HEALTHCHECK` 等），成功响应为裸 JSON：

```json
{ "status": "ok", "timestamp": "2026-09-14T04:05:35.6405225+00:00" }
```

仅当它**抛出异常**时，才由全局异常中间件按统一结构返回 `ApiResult`。

---

## 业务错误码

**按范围分段**分配，每段预留 100 个代码：

| 范围 | 段含义 |
|------|--------|
| `0` | 成功 |
| `1000-1099` | **通用**——参数校验、资源不存在、Docker 操作失败等 |
| `1100-1199` | **鉴权/会话**——未认证、Token 无效、Token 过期等 |
| `1200-1299` | **用户管理**——用户不存在、用户名重复等 |
| `1300-1399` | **主机管理**——主机不存在、连通性失败等 |
| `1400-1499` | **资源操作**（容器 / 镜像 / Volume / Network）——状态不允许、镜像不存在等 |
| `1500-1599` | **资源权限**——无权限访问此主机 / 容器 / 资源等 |
| `9000-9999` | **服务器内部错误**——未捕获异常、数据库错误等 |

### 当前已分配的错误码

| code | HTTP | 含义 | 阶段 |
|------|------|------|------|
| `0` | 200 | 成功 | 所有 |
| `1000` | 400 | 参数校验失败 | 0.0.2 |
| `1001` | 404 | 资源不存在 | 0.0.9 |
| `1002` | 500 | 通用 Docker 操作失败（`DockerApiException`） | 0.0.9 |
| `1003` | 408 | 操作超时（`TaskCanceledException`） | 0.0.9 |
| `1100` | 401 | 未认证（Token 缺失或无效） | 0.0.4 |
| `1101` | 401 | Token 已过期 | 0.0.5 |
| `1102` | 401 | Refresh token 已撤销或无效 | 0.0.5 |
| `1103` | 429 | 登录速率限制（5 次/分钟） | 0.0.4 |
| `1104` | 401 | Terminal token 无效或已过期 | 0.4.0 |
| `1200` | 400 | 用户名已存在 | 0.0.8 |
| `1201` | 404 | 用户不存在 | 0.0.8 |
| `1202` | 400 | 密码强度不满足要求 | 0.0.5 |
| `1300` | 404 | 主机不存在 | 0.0.8 |
| `1301` | 502 | 主机连通性失败 | 0.0.13 |
| `1401` | 404 | 容器 / 镜像 / Volume / Network 不存在 | 0.0.9 |
| `1402` | 500 | Docker API 调用失败 | 0.0.9 |
| `1403` | 408 | Docker 操作超时 | 0.0.9 |
| `1500` | 403 | 无权限访问此资源 | 0.0.8 |
| `1501` | 403 | 无权限访问此主机 | 0.0.8 |
| `1502` | 403 | 无权限执行此操作（write 权限缺失） | 0.3.0 |
| `9999` | 500 | 服务器内部错误 | 0.0.2 |

> 各代码段保留充足空间，0.3.0 / 0.4.0 起按需分配更细的错误码。

---

## 分页约定

需要分页的列表接口，请求和响应格式如下：

**请求 Body**（追加字段）：
```json
{
  "pageIndex": 1,
  "pageSize": 20
}
```

**响应 `data` 字段**：
```json
{
  "total": 100,
  "items": [ ]
}
```

---

## 认证

- 登录成功后返回 `accessToken`（JWT，15 分钟）+ `refreshToken`（7 天）
- 后续所有请求需在 Header 中携带：`Authorization: Bearer <accessToken>`
- Token 过期后通过 `POST /api/v1/auth/refresh` 刷新

---

## 多主机 Header

业务接口请求 Body 内携带 `hostId`；前端 axios 拦截器自动从 Pinia store 读取并注入到 `X-Host-Id` Header：

```
X-Host-Id: <hostId>     # 默认 "local"（本地 socket）
```

后端 `IDockerHostAccessor` 从 Header 解析 hostId；缺省回退到 `local`。

---

## API 接口列表

### 认证（0.0.4 - 0.0.5 / 0.4.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 登录 | `POST /api/v1/auth/login` | 0.0.4 | 返回 accessToken + refreshToken |
| 刷新 Token | `POST /api/v1/auth/refresh` | 0.0.5 | 用 refreshToken 换新 accessToken |
| 登出 | `POST /api/v1/auth/logout` | 0.0.5 | 撤销当前 refreshToken |
| 终端短时 Token | `POST /api/v1/auth/terminal-token` | 0.4.0 | 返回 5 分钟过期的 terminalToken，仅用于 WebSocket `/ws/container/exec` |

**请求/响应示例**：

```jsonc
// POST /api/v1/auth/login
// Request
{
  "username": "admin",
  "password": "StrongP@ss123"
}
// Response
{
  "success": true,
  "code": 0,
  "message": "ok",
  "data": {
    "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "refreshToken": "VmFkaUQxMjM0NTY3ODkw...",
    "expiresIn": 900,
    "user": { "id": "...", "username": "admin", "role": "admin" }
  }
}
```

---

### 用户管理（0.0.8 / 0.0.13）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/user/list` | admin | 分页 |
| 创建用户 | `POST /api/v1/user/create` | admin | Body: `{ username, password, role: "admin"\|"user" }` |
| 删除用户 | `POST /api/v1/user/delete` | admin | Body: `{ userId }` |
| 修改密码 | `POST /api/v1/user/update-password` | 任意（自身） / admin（他人） | Body: `{ userId?, oldPassword?, newPassword }` |

---

### 主机管理（0.0.8 / 0.0.13）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/host/list` | admin | 包含所有主机的连通状态 |
| 创建主机 | `POST /api/v1/host/create` | admin | Body: `{ name, type: "socket"\|"tcp", endpoint, tlsConfig?, basicAuth? }` |
| 更新主机 | `POST /api/v1/host/update` | admin | Body: `{ hostId, name?, endpoint?, tlsConfig?, basicAuth?, enabled? }` |
| 删除主机 | `POST /api/v1/host/delete` | admin | Body: `{ hostId }` |
| 测试主机 | `POST /api/v1/host/test` | admin | Body: `{ type, endpoint, tlsConfig?, basicAuth? }` |

---

### 容器管理（0.0.9 / 0.2.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| **创建容器** | `POST /api/v1/container/create` | 0.0.9 | Body: `{ hostId, image, name, ports, env, volumes, restart, ... }`（完整 `CreateContainerParameters` 字段映射） |
| 获取列表 | `POST /api/v1/container/list` | 0.0.9 | Body: `{ hostId, all?, pageIndex, pageSize }` |
| 启动 | `POST /api/v1/container/start` | 0.0.9 | Body: `{ hostId, containerId }` |
| 停止 | `POST /api/v1/container/stop` | 0.0.9 | Body: `{ hostId, containerId, waitSeconds? }` |
| 重启 | `POST /api/v1/container/restart` | 0.0.9 | Body: `{ hostId, containerId }` |
| 删除 | `POST /api/v1/container/remove` | 0.0.9 | Body: `{ hostId, containerId, force? }` |
| **批量删除** | `POST /api/v1/container/batch-remove` | 0.2.0 | Body: `{ hostId, containerIds: [...] }`，返回每个的成功/失败状态 |
| 获取日志 | `POST /api/v1/container/logs` | 0.0.9 | Body: `{ hostId, containerId, tail?, since?, timestamps? }` |
| 获取详情 | `POST /api/v1/container/inspect` | 0.0.9 | Body: `{ hostId, containerId }` |

---

### 镜像管理（0.0.10 / 0.2.0 / 0.4.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/image/list` | 0.0.10 | Body: `{ hostId, pageIndex, pageSize }` |
| 拉取 | `POST /api/v1/image/pull` | 0.0.10 | Body: `{ hostId, image, tag }`；0.0.10 一次性返回结果，0.3.0 改 SignalR 实时进度 |
| 删除 | `POST /api/v1/image/remove` | 0.0.10 | Body: `{ hostId, imageId, force? }` |
| **批量删除** | `POST /api/v1/image/batch-remove` | 0.2.0 | Body: `{ hostId, imageIds: [...] }` |
| 获取详情 | `POST /api/v1/image/inspect` | 0.0.10 | Body: `{ hostId, imageId }` |
| **构建镜像** | `POST /api/v1/image/build` | 0.2.0 | multipart: Dockerfile + context tar；通过 SignalR 临时频道推送进度 |
| **Push 镜像** | `POST /api/v1/image/push` | 0.4.0 | Body: `{ hostId, imageId, registryId }` |

---

### Volume 管理（0.2.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/volume/list` | Body: `{ hostId }` |
| 创建 | `POST /api/v1/volume/create` | Body: `{ hostId, name, driver?, driverOpts?, labels? }` |
| 删除 | `POST /api/v1/volume/remove` | Body: `{ hostId, name, force? }` |
| **批量删除** | `POST /api/v1/volume/batch-remove` | Body: `{ hostId, names: [...] }` |
| 获取详情 | `POST /api/v1/volume/inspect` | Body: `{ hostId, name }` |

---

### Network 管理（0.2.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/network/list` | Body: `{ hostId }` |
| 创建 | `POST /api/v1/network/create` | Body: `{ hostId, name, driver?, subnet?, gateway?, ipRange?, attachable?, labels? }` |
| 删除 | `POST /api/v1/network/remove` | Body: `{ hostId, networkId }` |
| 获取详情 | `POST /api/v1/network/inspect` | Body: `{ hostId, networkId }` |

---

### Docker Compose / Stack（0.4.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/stack/list` | Body: `{ hostId }` |
| 部署 | `POST /api/v1/stack/deploy` | Body: `{ hostId, name, composeContent }`（YAML 文本） |
| 更新 | `POST /api/v1/stack/update` | Body: `{ hostId, name, composeContent }` |
| 停止 | `POST /api/v1/stack/stop` | Body: `{ hostId, name }` |
| 删除 | `POST /api/v1/stack/remove` | Body: `{ hostId, name }` |

---

### Registry 管理（0.2.0 仅存储 / 0.4.0 暴露 API）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/registry/list` | 0.4.0 | — |
| 登录 | `POST /api/v1/registry/login` | 0.4.0 | Body: `{ name, url, username, password }`（凭据加密存储） |
| 删除 | `POST /api/v1/registry/remove` | 0.4.0 | Body: `{ registryId }` |

### 资源权限管理（0.3.0）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 列表 | `POST /api/v1/permission/list` | admin | Body: `{ userId?, hostId? }` |
| 授权 | `POST /api/v1/permission/grant` | admin | Body: `{ userId, hostId, permissions: ["read", "write", "admin"], containerIds? }` |
| 撤销 | `POST /api/v1/permission/revoke` | admin | Body: `{ userId, hostId, permissions?, containerIds? }` |

---

## SignalR Hub 规范（0.3.0）

### Hub 路径

| Hub | 路径 | 说明 |
|-----|------|------|
| `ContainerLogHub` | `/hubs/container-log` | 实时日志流 |
| `ContainerStatsHub` | `/hubs/container-stats` | 实时监控数据 |
| `NotificationHub` | `/hubs/notification` | 系统事件通知 |

**鉴权**：JWT 通过 `?access_token=...` 查询参数传递（WebSocket 不支持自定义 Header）。

### ContainerLogHub

**客户端 → 服务端**

| 方法 | 参数 | 说明 |
|------|------|------|
| `SubscribeLogs` | `containerId: string` | 开始订阅指定容器日志 |
| `UnsubscribeLogs` | `containerId: string` | 取消订阅 |

**服务端 → 客户端**

| 事件 | 数据 | 说明 |
|------|------|------|
| `OnLog` | `{ containerId, line, timestamp }` | 推送单行日志 |

### ContainerStatsHub

**客户端 → 服务端**

| 方法 | 参数 | 说明 |
|------|------|------|
| `SubscribeStats` | `containerId: string` | 开始订阅 |
| `UnsubscribeStats` | `containerId: string` | 取消订阅 |

**服务端 → 客户端**

| 事件 | 数据 | 说明 |
|------|------|------|
| `OnStats` | `{ containerId, cpuPercent, memoryUsage, memoryLimit, networkRx, networkTx, timestamp }` | 推送一次统计快照 |

### NotificationHub

**服务端 → 客户端**（主动推送，无需订阅方法）

| 事件 | 数据 | 说明 |
|------|------|------|
| `OnContainerDied` | `{ containerId, name, exitCode, timestamp }` | 容器异常退出通知 |

### Image Pull 进度频道（0.3.0）

为镜像 pull 操作提供临时频道，仅本请求客户端可见：

- 客户端：`connection.invoke("SubscribePull", "<pullToken>")` → 服务端推送 `OnPullProgress { pullToken, percent, status, error }`
- 后端实现：根据 `pullToken` 关联到具体 pull 请求的进度；完成后自动断开

---

## WebSocket 终端（0.4.0）

容器 exec 终端使用**原生 WebSocket**，不走 SignalR（独立的原始双向流）：

- **连接路径**：`/ws/container/exec?containerId={id}&cmd=/bin/sh&token={terminalToken}`
- **协议**：原始二进制流，前端使用 `xterm.js` + `AttachAddon` 对接
- **认证**：使用**专门的 short-lived terminal token**（5 分钟过期，由 `POST /api/v1/auth/terminal-token` 签发），**不接受 access token**
- **流程**：
  1. 前端打开终端面板 → 调用 `POST /api/v1/auth/terminal-token` 获取 terminalToken
  2. 建立 WebSocket，URL 带 `token=...` 查询参数
  3. 后端验证 terminalToken 有效性 + 检查用户对 containerId 的权限
  4. 建立双向流；Docker.DotNet `ExecCreateContainerAsync` + `StartWithAttachContainerExecAsync` 桥接到 WebSocket
  5. 关闭 WebSocket 时清理 exec 实例

---

## 不在 v1.0 范围的 API

以下 API **v1.0（0.x + 1.0.0）不做**：

- ❌ `POST /api/v1/container/exec`（一次性 exec）
- ❌ `POST /api/v1/container/stats`（一次性 stats）
- ❌ `POST /api/v1/system/prune`（容器/镜像/网络/卷清理）
- ❌ OAuth / OIDC 登录相关 API
- ❌ 审计日志相关 API
