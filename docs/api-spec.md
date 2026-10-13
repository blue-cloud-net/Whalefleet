# API 规范

## 请求风格约定

本项目 API 采用 **RPC 风格**（非 RESTful），并叠加**主机作用域路径**：

- 所有请求使用 `POST` 方法
- URL 表达动作，格式为 `/api/v1/{作用域}/{resource}/{action}`
- 除 `hostId` 外，参数统一放在请求 Body（JSON）中，不使用查询字符串（分页除外）
- 响应体统一使用 `ApiResult<T>` 格式（见「统一响应格式」）

### 两条作用域

| 作用域 | 路径形态 | 示例 |
|---|---|---|
| **主机作用域** | `/api/v1/hosts/{hostId}/{resource}/{action}` | `POST /api/v1/hosts/local/containers/start` |
| **全局作用域** | `/api/v1/auth/{action}`、`/api/v1/admin/{resource}/{action}` | `POST /api/v1/auth/login`、`POST /api/v1/admin/users/list` |

### 不变式：`hostId` 只出现在路径中

**任何请求 Body 都不得包含 `hostId` 字段**——主机标识一律由路径承载（作用域），或由路径段指定（被管理的资源）。这条不变式彻底消除了「URL 说一台机器、Body 说另一台」的可能。

> **为什么主机标识走路径而不是 Body 或请求头**（0.0.8 定案）：前端路由本身已是 `/hosts/:hostId/...`（可深链、可分享），API 路径与之同构可消除两处真相；且目标主机直接出现在访问日志里，排查「操作打到了哪台机器」的成本最低。旧 `X-Host-Id` 请求头方案已**废弃**，不再接受。

**示例对比**

| RESTful（不采用） | 本项目（RPC 风格 + 主机作用域路径） |
|---|---|
| `PUT /api/v1/hosts/local/containers/{id}` | `POST /api/v1/hosts/local/containers/start` |
| `DELETE /api/v1/hosts/local/images/{id}` | `POST /api/v1/hosts/local/images/remove` |
| `GET /api/v1/hosts/local/containers` | `POST /api/v1/hosts/local/containers/list` |

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

> **本表是错误码的唯一事实源**。后端常量（`Docker.Manager.Application.Common.ErrorCodes`）、
> 后端语义分类（`ErrorCodeCatalog`）与前端引用（`src/Docker.Manager.UI/src/types/api.ts` 的 `ErrorCode`）
> 三处必须与本表**逐码一致**（含常量名）。新增一个错误码需要同步改四处：本表 → `ErrorCodes` →
> `ErrorCodeCatalog` → 前端 `ErrorCode`，并补上错误码守卫测试，否则会落入「未登记」兜底分类。

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
| `1302` | 400 | 主机已停用 | 0.0.8 |
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

### 未认证 / 无权限的响应形态（0.0.4 起）

需要认证的端点（`[Authorize]` / `[Authorize(Roles = …)]`）在鉴权失败时，**同样返回统一的 `ApiResult`**，
而不是框架默认的「空 401 响应体」——否则客户端无法区分「该跳登录页」还是「该先静默刷新」
（0.0.5 的 401 → refresh 重发依赖这一区分）：

| 场景 | HTTP | `code` |
|------|:---:|------|
| 未携带 Token / 签名或格式错误 / issuer 或 audience 不符 | 401 | `1100` |
| **Token 已过期**（签名与结构都对，只是过了 `exp`） | 401 | `1101` |
| 已认证但角色不足（`[Authorize(Roles = …)]` 不通过） | 403 | `1500` |

> 除「已过期」外的签名/格式失败一律归入 `1100` 而不细分原因，避免把内部失败细节泄漏给客户端。
> 过期判定带 **30 秒时钟容差**，以免客户端与服务端时钟轻微不一致时把刚签发的令牌判为无效。

---

## 多主机路径

主机作用域的接口把 `hostId` 放在路径首段：

```
POST /api/v1/hosts/{hostId}/containers/list
                     ↑
                主机名（hostname）
```

| 项 | 约定 |
|----|------|
| `hostId` 取值 | `DockerHost.Name`（**主机名**，如 `local` / `prod-01`），**不是** `hosts.id`（Guid 只用于权限外键与审计） |
| 字符集 | `[A-Za-z0-9._-]`，长度 1–64——保证可直接进 URL，无需百分号编码 |
| 大小写 | 主机名唯一性**不区分大小写**，`/hosts/LOCAL/...` 与 `/hosts/local/...` 等价 |
| 可变性 | 主机名**不可修改**——它同时是 URL 的一部分与对外契约 |
| 解析 | Api 中间件从路由值取 `hostId` → 写入 Scoped `ICurrentHost` → `IDockerHostAccessor` 据其解析主机实体并取 Docker 客户端 |
| 不存在 / 已停用 | `1300`（404）/ `1302`（400） |
| 缺省值 | **无**。全局作用域端点不含该段；主机作用域端点必须显式给出 |

> **Hub 与 WebSocket 路径**（0.3.0 / 0.4.0）同样遵循「主机进路径」；`NotificationHub` 是**唯一**的全局 Hub，不含 `hostId`。

---

## API 接口列表

### 认证（0.0.4 - 0.0.5 / 0.4.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 登录 | `POST /api/v1/auth/login` | 0.0.4 | 返回 accessToken + refreshToken |
| 当前用户 | `POST /api/v1/auth/me` | 0.0.4 | 需认证；返回当前登录用户摘要。前端刷新页面时用它校验持久化的 token 是否**仍然有效**（签名、过期、角色以服务端为准），比前端自行解码 JWT 更可靠 |
| 刷新 Token | `POST /api/v1/auth/refresh` | 0.0.5 | 用 refreshToken 换新 accessToken |
| 登出 | `POST /api/v1/auth/logout` | 0.0.5 | 撤销当前 refreshToken |
| 终端短时 Token | `POST /api/v1/auth/terminal-token` | 0.4.0 | 返回 5 分钟过期的 terminalToken，仅用于 WebSocket `/ws/hosts/{hostId}/containers/exec` |

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

```jsonc
// POST /api/v1/auth/me    （Header: Authorization: Bearer <accessToken>）
// Request: 无 Body
// Response
{
  "success": true,
  "code": 0,
  "message": "ok",
  "data": { "id": "...", "username": "admin", "role": "admin" }
}
```

> **为何是 `POST` 而非 `GET`**：本 API 全量用 `POST`（见「请求风格约定」），
> `GET /healthz` 是唯一的基础设施例外。为了一个只读查询破例会让「哪些端点用哪种方法」失去唯一规则。

---

### 用户管理（0.0.8 / 0.0.13）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/admin/users/list` | admin | 分页 |
| 创建用户 | `POST /api/v1/admin/users/create` | admin | Body: `{ username, password, role?: "admin"\|"operator"\|"viewer"\|"auditor"\|<自定义> }`；`role` 省略时取**默认角色**（当前为 `auditor`，可由管理员修改） |
| 删除用户 | `POST /api/v1/admin/users/delete` | admin | Body: `{ userId }` |
| 修改密码 | `POST /api/v1/admin/users/update-password` | 任意（自身） / admin（他人） | Body: `{ userId?, oldPassword?, newPassword }` |

---

### 主机管理（0.0.8 / 0.0.13）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/admin/hosts/list` | admin | 包含所有主机的连通状态 |
| 创建主机 | `POST /api/v1/admin/hosts/create` | admin | Body: `{ name, type: "socket"\|"tcp", endpoint, tlsConfig?, basicAuth? }` |
| 更新主机 | `POST /api/v1/admin/hosts/{hostId}/update` | admin | Body: `{ endpoint?, tlsConfig?, basicAuth?, enabled? }`——**不含 `name`**（主机名不可改，它是对外标识） |
| 删除主机 | `POST /api/v1/admin/hosts/{hostId}/delete` | admin | 无 Body |
| 测试主机 | `POST /api/v1/admin/hosts/test` | admin | Body: `{ type, endpoint, tlsConfig?, basicAuth? }`——测试**未保存**的配置，故不带 `hostId` |

---

### 容器管理（0.0.9 / 0.2.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| **创建容器** | `POST /api/v1/hosts/{hostId}/containers/create` | 0.0.9 | Body: `{ image, name, ports, env, volumes, restart, ... }`（完整 `CreateContainerParameters` 字段映射） |
| 获取列表 | `POST /api/v1/hosts/{hostId}/containers/list` | 0.0.9 | Body: `{ all?, pageIndex, pageSize }` |
| 启动 | `POST /api/v1/hosts/{hostId}/containers/start` | 0.0.9 | Body: `{ containerId }` |
| 停止 | `POST /api/v1/hosts/{hostId}/containers/stop` | 0.0.9 | Body: `{ containerId, waitSeconds? }` |
| 重启 | `POST /api/v1/hosts/{hostId}/containers/restart` | 0.0.9 | Body: `{ containerId }` |
| 删除 | `POST /api/v1/hosts/{hostId}/containers/remove` | 0.0.9 | Body: `{ containerId, force? }` |
| **批量删除** | `POST /api/v1/hosts/{hostId}/containers/batch-remove` | 0.2.0 | Body: `{ containerIds: [...] }`，返回每个的成功/失败状态 |
| 获取日志 | `POST /api/v1/hosts/{hostId}/containers/logs` | 0.0.9 | Body: `{ containerId, tail?, since?, timestamps? }` |
| 获取详情 | `POST /api/v1/hosts/{hostId}/containers/inspect` | 0.0.9 | Body: `{ containerId }` |

---

### 镜像管理（0.0.10 / 0.2.0 / 0.4.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/hosts/{hostId}/images/list` | 0.0.10 | Body: `{ pageIndex, pageSize }` |
| 拉取 | `POST /api/v1/hosts/{hostId}/images/pull` | 0.0.10 | Body: `{ image, tag }`；0.0.10 一次性返回结果，0.3.0 改 SignalR 实时进度 |
| 删除 | `POST /api/v1/hosts/{hostId}/images/remove` | 0.0.10 | Body: `{ imageId, force? }` |
| **批量删除** | `POST /api/v1/hosts/{hostId}/images/batch-remove` | 0.2.0 | Body: `{ imageIds: [...] }` |
| 获取详情 | `POST /api/v1/hosts/{hostId}/images/inspect` | 0.0.10 | Body: `{ imageId }` |
| **构建镜像** | `POST /api/v1/hosts/{hostId}/images/build` | 0.2.0 | multipart: Dockerfile + context tar；通过 SignalR 临时频道推送进度 |
| **Push 镜像** | `POST /api/v1/hosts/{hostId}/images/push` | 0.4.0 | Body: `{ imageId, registryId }` |

---

### Volume 管理（0.2.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/hosts/{hostId}/volumes/list` | 无 Body |
| 创建 | `POST /api/v1/hosts/{hostId}/volumes/create` | Body: `{ name, driver?, driverOpts?, labels? }` |
| 删除 | `POST /api/v1/hosts/{hostId}/volumes/remove` | Body: `{ name, force? }` |
| **批量删除** | `POST /api/v1/hosts/{hostId}/volumes/batch-remove` | Body: `{ names: [...] }` |
| 获取详情 | `POST /api/v1/hosts/{hostId}/volumes/inspect` | Body: `{ name }` |

---

### Network 管理（0.2.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/hosts/{hostId}/networks/list` | 无 Body |
| 创建 | `POST /api/v1/hosts/{hostId}/networks/create` | Body: `{ name, driver?, subnet?, gateway?, ipRange?, attachable?, labels? }` |
| 删除 | `POST /api/v1/hosts/{hostId}/networks/remove` | Body: `{ networkId }` |
| 获取详情 | `POST /api/v1/hosts/{hostId}/networks/inspect` | Body: `{ networkId }` |

---

### Docker Compose / Stack（0.4.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/hosts/{hostId}/stacks/list` | 无 Body |
| 部署 | `POST /api/v1/hosts/{hostId}/stacks/deploy` | Body: `{ name, composeContent }`（YAML 文本） |
| 更新 | `POST /api/v1/hosts/{hostId}/stacks/update` | Body: `{ name, composeContent }` |
| 停止 | `POST /api/v1/hosts/{hostId}/stacks/stop` | Body: `{ name }` |
| 删除 | `POST /api/v1/hosts/{hostId}/stacks/remove` | Body: `{ name }` |

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
| 列表 | `POST /api/v1/admin/hosts/{hostId}/permissions/list` | admin | Body: `{ userId? }` |
| 授权 | `POST /api/v1/admin/hosts/{hostId}/permissions/grant` | admin | Body: `{ userId, permissions: ["read", "write", "admin"], containerIds? }` |
| 撤销 | `POST /api/v1/admin/hosts/{hostId}/permissions/revoke` | admin | Body: `{ userId, permissions?, containerIds? }` |

---

## SignalR Hub 规范（0.3.0）

### Hub 路径

| Hub | 路径 | 说明 |
|-----|------|------|
| `ContainerLogHub` | `/hubs/hosts/{hostId}/container-log` | 实时日志流（主机作用域） |
| `ContainerStatsHub` | `/hubs/hosts/{hostId}/container-stats` | 实时监控数据（主机作用域） |
| `NotificationHub` | `/hubs/notification` | 系统事件通知（**全局**，不含 `hostId`） |

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

- **连接路径**：`/ws/hosts/{hostId}/containers/exec?containerId={id}&cmd=/bin/sh&token={terminalToken}`
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

- ❌ `POST /api/v1/hosts/{hostId}/containers/exec`（一次性 exec）
- ❌ `POST /api/v1/hosts/{hostId}/containers/stats`（一次性 stats）
- ❌ `POST /api/v1/system/prune`（容器/镜像/网络/卷清理）
- ❌ OAuth / OIDC 登录相关 API
- ❌ 审计日志相关 API
