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
| `1303` | 400 | 主机名已被占用 | 0.0.8 |
| `1401` | 404 | 容器 / 镜像 / Volume / Network 不存在 | 0.0.9 |
| `1402` | 500 | Docker API 调用失败 | 0.0.9 |
| `1403` | 408 | Docker 操作超时 | 0.0.9 |
| `1404` | 409 | 资源当前状态不允许该操作（如不带 `force` 删除运行中的容器） | 0.0.9 |
| `1500` | 403 | 无权限访问此资源 | 0.0.8 |
| `1501` | 403 | 无权限访问此主机 | 0.0.8 |
| `1502` | 403 | 无权限执行此操作（write 权限缺失） | 0.3.0 |
| `9999` | 500 | 服务器内部错误 | 0.0.2 |

> 各代码段保留充足空间，0.3.0 / 0.4.0 起按需分配更细的错误码。

### Mock 与契约同步

前端有一层 MSW mock（`src/Docker.Manager.UI/src/mocks/`）用于**隔离开后端开发**，
它让本表变成**五处**同步而非四处：

1. 本表（唯一事实源）
2. 后端 `ErrorCodes`
3. 后端 `ErrorCodeCatalog`
4. 前端 `types/api.ts` 的 `ErrorCode`
5. **mock 的 `src/mocks/envelope.ts`**（含错误码 → HTTP 状态映射与默认文案）

> 新增或修改错误码时，**五处都要改**；`tests/mock-contract.spec.ts` 会逐条比对
> 错误码→HTTP 状态的映射，漏改则测试失败。

mock 的 HTTP 状态码**逐条拄自后端代码**（`ErrorCodeCatalog.Classify` → `ErrorHttpStatus.ToHttpStatus`），
而不是拄自本表的表格——表格可能滞后，代码不会。未登记的错误码一律兜底 500，
**不按数值分段推断**（同段内既有 404 也有 502、429）。

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

```jsonc
// POST /api/v1/auth/refresh
// Request
{ "refreshToken": "VmFkaUQxMjM0NTY3ODkw..." }
// Response：结构与 login 完全一致（新 access + 新 refresh + user）
{
  "success": true,
  "code": 0,
  "message": "ok",
  "data": {
    "accessToken": "…",
    "refreshToken": "…",
    "expiresIn": 900,
    "user": { "id": "...", "username": "admin", "role": "admin" }
  }
}
```

```jsonc
// POST /api/v1/auth/logout
// Request
{ "refreshToken": "VmFkaUQxMjM0NTY3ODkw..." }
// Response（幂等——令牌不存在或已撤销同样返回成功；无数据接口不含 data 字段）
{ "success": true, "code": 0, "message": "ok" }
```

#### 刷新令牌的轮换语义（0.0.5）

刷新令牌是**一次性**凭据，轮换与重放的处理是安全设计的一部分：

| 场景 | 服务端行为 | 客户端收到 |
|------|-----------|-----------|
| 刷新成功 | 旧令牌立即撤销，并用 `replaced_by_token_hash` 指向新令牌；同时签发新 refresh | 新 access + 新 refresh |
| **已被轮换的旧令牌再次使用** | 判定为**重放**（该令牌很可能已泄漏）→ **撤销该用户的全部刷新令牌** | `1102` / 401 |
| 已登出的令牌再次使用 | 仅拒绝（不牵连其他会话） | `1102` / 401 |
| 令牌未知 / 已过期 / 用户已被删除 / 角色缺失 | 拒绝，且**不**签发新令牌 | `1102` / 401 |
| 请求体缺 `refreshToken` | 参数校验失败 | `1000` / 400 |
| `logout` 重复调用 | 幂等成功 | `200` |

> **为什么「重放」与「登出后误用」要区别对待**：合法客户端在轮换后**不再持有**旧令牌（它已经被换成新的了），
> 所以「已轮换的旧令牌被使用」只可能是令牌被窃取或客户端实现有 bug——两种情况下最安全的动作都是
> 作废该用户全部会话、强制重新登录。
> 而「已登出的令牌被使用」只说明对方拿着一个已经作废的凭据（例如用户在多设备场景下的旧副本），
> 牵连其他会话没有安全收益，反而会误伤。两者的区别由 `replaced_by_token_hash` 是否为空表达。

---

### 用户管理（0.0.8 / 0.0.13）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/admin/users/list` | admin | 分页 |
| 创建用户 | `POST /api/v1/admin/users/create` | admin | Body: `{ username, password, role?: "admin"\|"operator"\|"viewer"\|"auditor"\|<自定义> }`；`role` 省略时取**默认角色**（当前为 `auditor`，可由管理员修改） |
| 删除用户 | `POST /api/v1/admin/users/delete` | admin | Body: `{ userId }` |
| 修改密码 | `POST /api/v1/admin/users/update-password` | 任意（自身） / admin（他人） | Body: `{ userId?, oldPassword?, newPassword }` |

**修改密码语义**：

| 场景 | `userId` | `oldPassword` | 结果 |
|------|----------|---------------|------|
| 改自己 | 省略，或等于当前登录用户 | **必填**且须正确 | 成功；该用户**全部刷新令牌被撤销**（需重新登录） |
| 管理员重置他人 | 他人主键 | 忽略 | 成功；同样撤销该用户全部令牌 |
| 非管理员改他人 | 他人主键 | — | `1500` / 403 |
| 原密码不正确 | 省略 | 错误 | `1000` / 400 |

> **为什么自助改密必须验原密码**：只凭一个被盗的 access token 就能改密码，攻击者可以把真正的主人锁在门外。
> **为什么管理员重置不需要原密码**：那本来就是「替忘记密码的人恢复」的流程。
> **为什么改密码要撤销全部会话**：改密码最常见的动因就是「疑似泄漏」，不撤销等于把旧凭据留在原地。
>
> ⚠️ **原密码错误刻意返回 `1000`/400 而不是 `401`**：前端拦截器把 401 当作「会话过期」会自动刷新并重放，
> 用在这里会把「输错原密码」表现成「被登出」，用户看到的是一连串莫名其妙的跳转。

**删除用户的两道护栏**：不能删除当前登录的账号；不能删除最后一个管理员（后者在 HTTP 侧总会先被前者拦下，
保留它是为了让「系统至少有一个管理员」这一**不变式**对非 HTTP 调用者同样成立）。删除后同样撤销其全部令牌。

**列表分页**：请求 `{ pageIndex?, pageSize? }`；`pageSize` 上限 100（超出**夹紧**而非报错），
响应 `{ items, total, pageIndex, pageSize }`，其中 `pageIndex` / `pageSize` 是夹紧后的**实际**取值——
调用方传 9999 却只拿到 100 条时能看出服务端用了多少，不必猜是否丢数据。

---

### 主机查询（0.0.8）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 可见主机列表 | `POST /api/v1/hosts/list` | 任意已登录 | 返回**当前用户可见**的主机（含 `status`）；admin 调用时返回全部 |
| 单台概览 | `POST /api/v1/hosts/{hostId}/overview` | 任意已登录 | 返回单台主机的 `version` / `runtime`（运行摘要）；失败形态 `1300`/404、`1302`/400、`1501`/403。**0.0.8 / 0.0.9 已实现** |

**`overview` 的响应形态**（0.0.9 已实现）：

```jsonc
// POST /api/v1/hosts/local/overview
// Request: 无 Body
// Response.data
{
  "version": {                                  // 来自 /version
    "version": "27.3.1",
    "apiVersion": "1.47", "minApiVersion": "1.24",
    "os": "linux", "arch": "x86_64",
    "goVersion": "go1.22.7", "kernelVersion": "6.8.0-45-generic",
    "experimental": false
  },
  "runtime": {                                  // 来自 /info
    "name": "dev-01", "serverVersion": "27.3.1",
    "startedAt": "2026-01-02T03:04:05.678+00:00",  // 拿不到为 null
    "uptimeSeconds": 86400,                        // startedAt 为 null 时同样为 null
    "containers": 12, "containersRunning": 8,
    "containersPaused": 0, "containersStopped": 4,
    "images": 30, "cpus": 8, "memoryTotalBytes": 16803856384,
    "operatingSystem": "Ubuntu 24.04.1 LTS"
  }
}
```

> **为什么拆成 `version` / `runtime`**：它们来自守护进程的两个不同接口（`/version` 与 `/info`），
> 字段重名也不少（`version` / `serverVersion`、`os` / `operatingSystem`）。分组之后调用方一眼能看出
> 字段来源，0.3.0 给其中一块加缓存也不必动契约。
>
> **`uptimeSeconds` 由服务端算**：让每个客户端各算一次会把「前端时钟不准」变成
> 「主机运行时长是负数」。拿不到 `startedAt` 时一并返回 `null`——给 0 会被读成「刚刚启动」。
>
> **本端点会真的连守护进程**（与列表里的轻量 ping 不同），因此主机离线时返回 `1301`/502。

> **为什么要与 `admin/hosts/list` 分开**：总览页与侧栏主机选择器对**所有角色**可见，
> 而 `admin/hosts/list` 是主机管理页专用（含管理操作所需的完整信息）。
> 两者混用会导致非 admin 用户看不到任何主机。

> **`status` 内置、`version` / `counts` 另设端点**：列表只对每台主机做**轻量 ping**
> 取连通状态（便宜）；`version` / `counts` 需要完整调用 Docker API（贵）。
> 若都塞进列表，主机一多就会明显变慢甚至超时。

### 主机管理（0.0.8 / 0.0.13）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/admin/hosts/list` | admin | 包含所有主机的连通状态 |
| 创建主机 | `POST /api/v1/admin/hosts/create` | admin | Body: `{ name, type: "socket"\|"tcp", endpoint, tlsConfig?, basicAuth? }` |
| 更新主机 | `POST /api/v1/admin/hosts/{hostId}/update` | admin | Body: `{ endpoint?, tlsConfig?, basicAuth?, clearCredentials?, enabled? }`——**不含 `name`**（主机名不可改，它是对外标识） |
| 删除主机 | `POST /api/v1/admin/hosts/{hostId}/delete` | admin | 无 Body |
| 测试主机 | `POST /api/v1/admin/hosts/test` | admin | Body: `{ type, endpoint, tlsConfig?, basicAuth? }`——测试**未保存**的配置，故不带 `hostId` |

**更新语义**（`update`）：未提供的字段表示**不改**；提供 `tlsConfig` / `basicAuth` 则替换凭据，
两者与 `clearCredentials: true`（改为无认证）**三者互斥**。
之所以需要 `clearCredentials`：JSON 无法区分「没给 `tlsConfig`」与「`tlsConfig: null`」，
没有它就答不出「把主机从 TLS 改回明文」——而 TLS 凭据留在明文端点上会被直接拒绝（`1301`），
用户会卡在一个无法自救的配置里。

**主机对象的字段**（`list` / `create` / `update` 的 `data`）：

```jsonc
{
  "id": "3f1e...",            // 主键：仅用于权限外键与审计，不进 URL
  "name": "local",             // 对外标识，也是路径里的 hostId
  "type": "socket",           // 枚举一律以小驼峰字符串出现：socket | tcp
  "endpoint": "/var/run/docker.sock",
  "enabled": true,
  "reachable": true,          // true 可连 / false 连不上 / null **未探测**
  "authKind": "none"          // none | tls | basic；只有方式，**不含任何凭据内容**
}
```

> `reachable` 之所以是可空布尔：`create` / `update` 不探活（写操作不该被网络超时拖住），
> 用 `false` 表示「未探测」会让界面把它显示成「离线」，把没测当成故障。

> **枚举一律序列化为小驼峰字符串**（`DockerHostType.Socket` → `"socket"`）。数值形态会让
> 契约自解释性很差（请求收 `"socket"`、响应回 `0`），且枚举重排序会变成破坏性变更。

**`tlsConfig` 字段**（`auth_kind=tls`，三项路径均必填）：

```jsonc
{
  "caCertificatePath": "/certs/ca.pem",        // 必填：校验服务端证书（否则等于接受任意服务端证书）
  "clientCertificatePath": "/certs/cert.pem",  // 必填：客户端证书
  "clientKeyPath": "/certs/key.pem",           // 必填：客户端私钥
  "clientKeyPassword": "…"                      // 可选：私钥加密口令
}
```

**`basicAuth` 字段**（`auth_kind=basic`）：`{ "username": "...", "password": "..." }`（均必填）。

> `tlsConfig` 与 `basicAuth` **互斥**，且都可省略（省略即无认证）。
> 字段值是**容器内可见的文件路径**而非 PEM 内容——理由见
> [architecture.md](./architecture.md#主机凭据auth_config_json)。路径非法或文件读不到时，
> 创建/测试会返回 `1301`。
>
> **TLS 必须用 `https://` 端点**（默认端口 2376）：在 `tcp://` 上配置 TLS 凭据会被拒绝
> （`1301`）——放行会导致「看起来启用了 TLS、实际是明文未认证连接」。

---

### 容器管理（0.0.9 / 0.2.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| **创建容器** | `POST /api/v1/hosts/{hostId}/containers/create` | 0.0.9 | Body: `{ image, name?, cmd?, entrypoint?, env?, labels?, ports?, volumes?, restartPolicy?, autoRemove?, network?, hostname?, workingDir?, tty?, stdinOpen? }`（**精选子集**，非全部 `CreateContainerParameters` 字段，理由见下文） |
| 获取列表 | `POST /api/v1/hosts/{hostId}/containers/list` | 0.0.9 | Body: `{ all?, pageIndex?, pageSize? }`；分页在服务端**内存**完成（Docker 无分页参数），`pageSize` 上限 100 |
| 启动 | `POST /api/v1/hosts/{hostId}/containers/start` | 0.0.9 | Body: `{ containerId }` |
| 停止 | `POST /api/v1/hosts/{hostId}/containers/stop` | 0.0.9 | Body: `{ containerId, waitSeconds? }`；`waitSeconds` 取值 0–300（默认 10，与 Docker 一致） |
| 重启 | `POST /api/v1/hosts/{hostId}/containers/restart` | 0.0.9 | Body: `{ containerId }` |
| 删除 | `POST /api/v1/hosts/{hostId}/containers/remove` | 0.0.9 | Body: `{ containerId, force? }`；不带 `force` 删运行中的容器返回 `1404`/409 |
| **批量删除** | `POST /api/v1/hosts/{hostId}/containers/batch-remove` | 0.2.0 | Body: `{ containerIds: [...] }`，返回每个的成功/失败状态 |
| 获取日志 | `POST /api/v1/hosts/{hostId}/containers/logs` | 0.0.9 | Body: `{ containerId, tail?, since?, timestamps?, includeStderr? }`；`tail` 取值 0–10000，省略表示全部；**不支持** `follow` |
| 获取详情 | `POST /api/v1/hosts/{hostId}/containers/inspect` | 0.0.9 | Body: `{ containerId }` |

#### 查询端点的请求 / 响应形态（0.0.9 已实现）

三个查询端点（`list` / `inspect` / `logs`）的**成功响应都是 `ApiResult`**，`data` 为下面的投影对象。
响应**不是** SDK 对象的原样透传：那会让「响应里有什么」随 Docker 版本变化，前端无法依赖（详见
`ContainerSummaries.cs` 的类型注释）。

```jsonc
// POST /api/v1/hosts/local/containers/list
// Request
{ "all": true, "pageIndex": 0, "pageSize": 20 }
// Response.data
{
  "items": [
    {
      "id": "3f2c…",                    // 完整容器 ID
      "name": "nginx",                  // 已去掉 Docker 内部的前导斜杠（/nginx → nginx）
      "image": "nginx:latest",          // 镜像名；无名字时退化为容器短 ID
      "imageId": "sha256:…",
      "command": "nginx -g 'daemon off;'",
      "created": "2026-01-02T03:04:05.678+00:00",
      "state": "running",               // Docker 原始状态文本
      "status": "Up 3 hours",
      "ports": [{ "privatePort": 80, "publicPort": 8080, "type": "tcp", "ip": "0.0.0.0" }]
    }
  ],
  "total": 7,        // 分页前的总数
  "pageIndex": 0,    // 服务端实际使用的页码
  "pageSize": 20      // 服务端实际使用的每页条数（夹紧后）
}
```

```jsonc
// POST /api/v1/hosts/local/containers/inspect
// Request
{ "containerId": "nginx" }   // 可传完整 ID、短 ID 或名称
// Response.data
{
  "id": "3f2c…",
  "name": "nginx",
  "created": "2026-01-02T03:04:05.678+00:00",
  "image": "nginx:latest",     // 取 Config.Image（镜像名）；inspect 顶层的 image 字段是**镜像 ID**
  "state": {
    "status": "exited",
    "running": false, "paused": false, "restarting": false,
    "startedAt": "2026-01-02T03:05:00+00:00",  // 「未启动过」为 null（Docker 给的是 0001-01-01）
    "finishedAt": "2026-01-02T04:05:00+00:00",
    "exitCode": 1,                            // **运行中**为 null（运行中固定 0，给出去会被读成「正常退出」）
    "oomKilled": false,
    "healthStatus": null                      // 无健康检查为 null；有则为 healthy / unhealthy / starting
  },
  "config": {
    "image": "nginx:latest",
    "cmd": ["nginx", "-g", "daemon off;"],
    "entrypoint": ["/docker-entrypoint.sh"],
    "env": ["PATH=…"],                        // ⚠️ 常用于存放密码 / Token，仅管理员可见（0.3.0 起逐资源授权）
    "labels": { "com.example.owner": "web" },
    "workingDir": "", "hostname": "3f2c…", "user": "", "tty": false
  },
  "hostConfig": {
    "restartPolicy": "unless-stopped",       // 线上写法（no / always / on-failure / unless-stopped）
    "autoRemove": false,
    "networkMode": "bridge",
    "privileged": false,
    "memoryLimitBytes": null,                 // 未限制为 null（Docker 用 0 表示未限制）
    "nanoCpus": null
  },
  "mounts": [{ "type": "bind", "source": "/srv/html", "destination": "/usr/share/nginx/html", "readWrite": true }],
  "networks": [{ "name": "bridge", "ipAddress": "172.17.0.2", "gateway": "172.17.0.1", "macAddress": "02:42:ac:11:00:02" }]
}
```

```jsonc
// POST /api/v1/hosts/local/containers/logs
// Request
{ "containerId": "nginx", "tail": 200, "since": 1735785600, "timestamps": false }
// Response.data
{ "containerId": "nginx", "content": "2026/01/02 …\n" }   // 整段文本，未按行切分
```

> **分页语义**：Docker 不提供容器列表的分页参数，因此 `total` 是**一次性拉全量后**的条数，
> 切片在服务端内存完成。`pageIndex` 从 0 开始、负数按 0 处理；`pageSize` 默认 20、上限 100，
> **夹紧后的实际值一并返回**（传 5000 时能从响应里看出服务端用了 100）。页码越界返回空 `items`
> 而**不是**自动回到最后一页——后者会让调用方以为「这就是第 100 页的内容」。
>
> **默认只列运行中的容器**（与 `docker ps` 一致）；`all: true` 等价于 `docker ps -a`。
>
> **日志不做实时跟随**：一次性 HTTP 响应跟着日志流只会把连接挂到容器退出；实时流是 0.3.0
> 的 SignalR 推送要解决的事。`tail` 越界（负数或 > 10000）返回 `1000`/400 而**不是**静默截断——
> 调用方要 100 万行却只拿到 1 万行，会误以为「日志就这么多」。

#### 变更端点的请求 / 响应形态（0.0.9 已实现）

```jsonc
// POST /api/v1/hosts/local/containers/create
// Request
{
  "image": "nginx:latest",                    // 必填
  "name": "web-1",                            // 可省略（省略时由 Docker 随机命名）
  "cmd": ["nginx", "-g", "daemon off;"],
  "entrypoint": ["/docker-entrypoint.sh"],
  "env": ["TZ=Asia/Shanghai", "MODE=prod"],     // KEY=VALUE；缺等号会被拒为 1000
  "labels": { "com.example.owner": "web" },
  "ports": [{ "containerPort": 80, "hostPort": 8080, "type": "tcp", "hostIp": "127.0.0.1" }],
  "volumes": [{ "source": "/srv/html", "destination": "/usr/share/nginx/html", "readOnly": true }],
  "restartPolicy": "unless-stopped",           // no / always / on-failure / unless-stopped
  "autoRemove": false,
  "network": "bridge",
  "hostname": "web-1", "workingDir": "/app", "tty": false, "stdinOpen": false
}
// Response.data
{
  "containerId": "3f2c…",
  "warnings": ["…"]    // Docker 的提示（如“端口未映射”）；无提示时为空数组，字段始终存在
}
```

> **`create` 不回带容器名**：未指定名称时名字由守护进程随机生成，要拿到它就得再 `inspect` 一次；
> 创建已经成功，为了补一个展示字段再发一次请求（万一失败还会把成功报成错误）并不划算——
> 名字在列表接口里就能看到。

```jsonc
// 启动 / 停止 / 重启 / 删除
// Request
{ "containerId": "web-1" }            // start / restart
{ "containerId": "web-1", "waitSeconds": 10 }   // stop
{ "containerId": "web-1", "force": true }       // remove
// Response
{ "success": true, "code": 0, "message": "操作成功" }   // 这四个端点无 data 字段
```

> **为什么 `create` 只映射精选字段而不是全部 `CreateContainerParameters`**：那个模型有上百个字段
> （`Ulimits` / `DeviceRequests` / `BlkioWeightDevice` / `Sysctls` …），其中绝大多数在运维面板里
> 永远不会出现。全量透传等于把 SDK 的字段集当成对外契约：SDK 升级就要改前端，而调用方也无法从
> 文档里看出「到底哪些字段真的会被用上」。当前暴露的是**日常创建容器真正会设的**那一组；
> 需要 `--cap-add` / `--device` 这类高级选项时再按需逐个补齐（每个字段都要配语义说明与校验）。
>
> **`force` 与 409**：Docker 不允许删除运行中的容器，不带 `force` 时守护进程返回 409，
> 本服务映射成 `1404`/409 并提示“先停止容器”——归成 `1402`/500 会把「你先停一下」
> 说成「服务端挂了」。若容器**已经不存在**，`remove` 一律返回 `1401`/404（如实报告，
> 不假装删掉了什么）。
>
> **启动 / 停止 / 重启是幂等的**：启动已在运行的容器、停止已停止的容器时，守护进程返回
> `304 Not Modified`；本服务把它当作成功（`200`）。否则前端重复点一下按钮就会看到 500。
> 删除不适用该规则（见上）。
>
> **`restartPolicy` 用 Docker 线上写法**（`no` / `always` / `on-failure` / `unless-stopped`），
> 枚举值非法返回 `1000`；不写等同 `no`。
>
> **端口与挂载的校验**：`containerPort` 必填且为 1–65535；`hostPort` 省略表示**随机映射**
> （仅暴露不绑定）；`volumes[].source` 与 `destination` 均不可为空，`readOnly` 省略为可写。
> 挂载路径的合法性最终由守护进程裁定（它的错误会以 `1000`/400 返回）。

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
