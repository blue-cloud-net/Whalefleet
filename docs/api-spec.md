# API 规范

## API 稳定性

> ⚠️ **1.0.0 之前没有兼容性承诺。** `0.x` 全程不对外发布，路径与请求 / 响应结构
> **可能在下一次迭代里就改变**（见 [roadmap.md](./roadmap.md#版本段与发布模型)）。
>
> **自 `1.0.0` 起，公开 API 冻结**：路径与请求 / 响应结构保持向后兼容，直到 `2.0.0`。
> 这条承诺的可执行手段是 **0.19.0 落地的路由清单与响应 DTO 快照测试**——
> 改动任一响应 DTO 都会让测试失败。冻结承诺若没有测试支撑，只是文档里的一句话。
>
> 三个仍可变更的例外（不计入冻结范围）：
> - **`/api/v1/dev/throw`**：仅开发环境注册，生产不存在。
> - **`/scalar/v1`** 与 OpenAPI 描述：文档端点，不参与业务契约。
> - **`/mcp`**：MCP 端点是 **JSON-RPC 2.0 协议面**（0.18.0 起），工具清单随权限与配置变化，
>   不返回 `ApiResult` 信封。

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

`success = false` 时，HTTP 状态码由业务错误码**逐码显式映射**（实现见 `Whalefleet.Api.Errors.ErrorHttpStatus`，完整对照见下文「当前已分配的错误码」表格的 HTTP 列）。

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
| `1600-1699` | **操作审计**——审计查询、导出等（0.9.0 起，**预留**） |
| `1700-1799` | **告警与通知**——告警规则、通知渠道等（0.11.0 起，**预留**） |
| `1800-1899` | **仓库与镜像构建 / 推送**——仓库配置、凭据、构建与推送错误（0.4.0 起） |
| `9000-9999` | **服务器内部错误**——未捕获异常、数据库错误等 |

### 当前已分配的错误码

> **本表是错误码的唯一事实源**。后端常量（`Whalefleet.Application.Common.ErrorCodes`）、
> 后端语义分类（`ErrorCodeCatalog`）与前端引用（`src/Whalefleet.UI/src/types/api.ts` 的 `ErrorCode`）
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
| `1104` | 401 | Terminal token 无效或已过期 | 0.8.0 |
| `1105` | 401 | 个人访问令牌（PAT）无效或已撤销 | 0.18.0 |
| `1106` | 401 | 个人访问令牌已过期 | 0.18.0 |
| `1107` | 404 | 个人访问令牌不存在 | 0.18.0 |
| `1200` | 400 | 用户名已存在 | 0.0.8 |
| `1201` | 404 | 用户不存在 | 0.0.8 |
| `1202` | 400 | 密码强度不满足要求 | 0.0.5 |
| `1203` | 400 | 团队名已占用 | 0.13.0 |
| `1204` | 404 | 团队不存在 | 0.13.0 |
| `1300` | 404 | 主机不存在 | 0.0.8 |
| `1301` | 502 | 主机连通性失败 | 0.0.13 |
| `1302` | 400 | 主机已停用 | 0.0.8 |
| `1303` | 400 | 主机名已被占用 | 0.0.8 |
| `1304` | 400 | 分组名已被占用 | 0.0.13 |
| `1305` | 404 | 分组不存在 | 0.0.13 |
| `1306` | 404 | 主机标签不存在 | 0.6.0 |
| `1307` | 400 | 主机标签名已占用 | 0.6.0 |
| `1401` | 404 | 容器 / 镜像 / Volume / Network 不存在 | 0.0.9 |
| `1402` | 500 | Docker API 调用失败 | 0.0.9 |
| `1403` | 408 | Docker 操作超时 | 0.0.9 |
| `1404` | 409 | 资源当前状态不允许该操作（如不带 `force` 删除运行中的容器） | 0.0.9 |
| `1500` | 403 | 无权限访问此资源 | 0.0.8 |
| `1501` | 403 | 无权限访问此主机 | 0.0.8 |
| `1502` | 403 | 无权限执行此操作（write 权限缺失） | 0.12.0 |
| `1503` | 404 | 授权记录不存在 | 0.1.0 |
| `1504` | 403 | MCP 写工具未启用（`Mcp:AllowWrite=false`） | 0.18.0 |
| `1510` | 404 | 角色不存在 | 0.1.0 |
| `1511` | 400 | 角色标识已被占用 | 0.1.0 |
| `1512` | 400 | 角色仍有有效分配，拒绝删除 | 0.1.0 |
| `1513` | 400 | 内置角色受保护，不可修改或删除 | 0.1.0 |
| `1514` | 400 | 未知权限标识（不在权限目录中） | 0.1.0 |
| `1515` | 404 | 角色分配不存在 | 0.1.0 |
| `1800` | 404 | 仓库配置不存在 | 0.4.0 |
| `1801` | 400 | 仓库别名已被占用 | 0.4.0 |
| `1802` | 400 | 仓库服务地址已被占用 | 0.4.0 |
| `1803` | 400 | 未登记该地址的仓库凭据 | 0.4.0 |
| `1804` | 400 | 仓库拒绝了该凭据 | 0.4.0 |
| `1805` | 502 | 仓库不可达 | 0.4.0 |
| `1806` | 500 | 镜像构建失败 | 0.4.0 |
| `1807` | 500 | 镜像推送失败 | 0.4.0 |
| `9999` | 500 | 服务器内部错误 | 0.0.2 |

> 各代码段保留充足空间：0.9.0 起按需分配更细的错误码（`1600-1699` **预留**给操作审计、
> `1700-1799` **预留**给告警）。

### Mock 与契约同步

前端有一层 MSW mock（`src/Whalefleet.UI/src/mocks/`）用于**隔离开后端开发**，
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

需要认证的端点（`[Authorize]` / 按**权限**判定的授权策略）在鉴权失败时，**同样返回统一的 `ApiResult`**，
而不是框架默认的「空 401 响应体」——否则客户端无法区分「该跳登录页」还是「该先静默刷新」
（0.0.5 的 401 → refresh 重发依赖这一区分）：

| 场景 | HTTP | `code` |
|------|:---:|------|
| 未携带 Token / 签名或格式错误 / issuer 或 audience 不符 | 401 | `1100` |
| **Token 已过期**（签名与结构都对，只是过了 `exp`） | 401 | `1101` |
| 已认证但**权限不足**（授权策略不通过） | 403 | `1500` |

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

> **Hub 与 WebSocket 路径**（0.5.0 / 0.8.0）同样遵循「主机进路径」；`NotificationHub` 是**唯一**的全局 Hub，不含 `hostId`。

---

## API 接口列表

### 认证（0.0.4 - 0.0.5 / 0.5.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 登录 | `POST /api/v1/auth/login` | 0.0.4 | 返回 accessToken + refreshToken |
| 当前用户 | `POST /api/v1/auth/me` | 0.0.4 | 需认证；返回当前登录用户摘要（`roles` + **全局**有效权限集 `permissions`）。前端刷新页面时用它校验持久化的 token 是否**仍然有效**（签名、过期、角色与权限以服务端为准），比前端自行解码 JWT 更可靠 |
| 刷新 Token | `POST /api/v1/auth/refresh` | 0.0.5 | 用 refreshToken 换新 accessToken |
| 登出 | `POST /api/v1/auth/logout` | 0.0.5 | 撤销当前 refreshToken |
| 终端短时 Token | `POST /api/v1/auth/terminal-token` | 0.8.0 | 返回 5 分钟过期的 terminalToken，仅用于 WebSocket `/ws/hosts/{hostId}/containers/exec` |

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
    "user": {
      "id": "…uuid…",
      "username": "admin",
      "roles": ["admin"],                    // 角色名数组；未被分配角色时是空数组
      "permissions": ["host.read", "host.admin", "container.read", "…"]   // **全局**有效权限集
    }
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
  "data": {
    "id": "…uuid…",
    "username": "admin",
    "roles": ["admin"],
    "permissions": ["host.read", "host.admin", "container.read", "…"]
  }
}
```

> **返回的 `permissions` 是「全局」有效权限集**，主机级权限不在这里——它随主机而变，
> 由主机列表接口按需带回；`roles` 为角色名数组（新建用户到被分配角色之间是空数组，属正常状态）。
> **⚠️ 这份数据只供界面显隐，不是安全边界**：服务端一律按权限目录查库判定（见「资源权限管理」）。

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
    "user": {
      "id": "…uuid…",
      "username": "admin",
      "roles": ["admin"],
      "permissions": ["host.read", "host.admin", "container.read", "…"]
    }
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
| 令牌未知 / 已过期 / 用户已被删除 | 拒绝，且**不**签发新令牌 | `1102` / 401 |
| 请求体缺 `refreshToken` | 参数校验失败 | `1000` / 400 |
| `logout` 重复调用 | 幂等成功 | `200` |

> **为什么「重放」与「登出后误用」要区别对待**：合法客户端在轮换后**不再持有**旧令牌（它已经被换成新的了），
> 所以「已轮换的旧令牌被使用」只可能是令牌被窃取或客户端实现有 bug——两种情况下最安全的动作都是
> 作废该用户全部会话、强制重新登录。
> 而「已登出的令牌被使用」只说明对方拿着一个已经作废的凭据（例如用户在多设备场景下的旧副本），
> 牵连其他会话没有安全收益，反而会误伤。两者的区别由 `replaced_by_token_hash` 是否为空表达。

### MCP 与访问令牌（0.18.0）

> MCP 端点是 **JSON-RPC 2.0 协议面**，不返回 `ApiResult` 信封（与 `GET /healthz` 同类例外）。

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 访问令牌列表 | `POST /api/v1/auth/tokens/list` | 0.18.0 | 只返回前缀与元数据（`lastUsedAt` / `expiresAt`），**不返回明文** |
| 创建访问令牌 | `POST /api/v1/auth/tokens/create` | 0.18.0 | Body: `{ name, expiresAt? }`；**明文仅在本次响应里出现一次** |
| 撤销访问令牌 | `POST /api/v1/auth/tokens/revoke` | 0.18.0 | Body: `{ tokenId }`；撤销**即时生效**，不等过期 |
| MCP 端点 | `POST /mcp`（Streamable HTTP） | 0.18.0 | 与 `/api/v1` 并列，**同进程同端口**；返回 JSON-RPC 2.0 |

> **PAT 不走 `auth/refresh` / `auth/logout` 语义**：它没有会话，只有「有效 / 已撤销 / 已过期」三态。
> **`/mcp` 必须与 `/api`、`/healthz` 一同排除在 SPA 回退之外**，否则打错的子路径会变成 200 + HTML。
> **`Mcp:AllowedHosts` 不匹配返回 421**（DNS-rebinding 防护）——这是 MCP 客户端配置里最容易漏的一项。

### 0.6.0 起的新增端点（索引）

> 这些迭代沿用既有约定（RPC 风格 + 主机作用域路径 + `ApiResult` 信封），此处只列路径与阶段；
> 请求 / 响应形态在各迭代落地时补全。

| 能力 | 端点 | 阶段 |
|------|------|------|
| 主机标签 | `POST /api/v1/host-tags/list` + `POST /api/v1/admin/host-tags/{create,update,remove}` | 0.6.0 |
| 主机打标 / 去标 | `POST /api/v1/admin/hosts/tags/{assign,revoke}` | 0.6.0 |
| 磁盘用量 | `POST /api/v1/hosts/{hostId}/system/df` | 0.6.0 |
| 容器批量启停 | `POST /api/v1/hosts/{hostId}/containers/batch-{start,stop,restart}` | 0.7.0 |
| 资源级清理 | `POST /api/v1/hosts/{hostId}/{images,volumes}/prune` | 0.7.0 |
| 容器接入 / 断开网络 | `POST /api/v1/hosts/{hostId}/networks/{connect,disconnect}` | 0.7.0 |
| 认证日志列表 | `POST /api/v1/admin/logs/authentication/list` | 0.9.0 |
| 活动日志列表 | `POST /api/v1/admin/logs/activity/list` | 0.9.0 |
| 审计导出 | `POST /api/v1/admin/logs/activity/export` | 0.9.0 |
| Stack 管理 | `POST /api/v1/hosts/{hostId}/stacks/{list,deploy,update,stop,remove}` | 0.10.0 |
| 告警规则 | `POST /api/v1/admin/alert-rules/{list,update}` | 0.11.0 |
| 通知中心 | `POST /api/v1/notifications/{list,read}` | 0.11.0 |
| 通知渠道测试 | `POST /api/v1/admin/notifications/test` | 0.11.0 |
| 团队 CRUD 与成员 | `POST /api/v1/admin/teams/*` | 0.13.0 |
| **团队批量授权** | `POST /api/v1/admin/teams/{teamId}/grant` | 0.13.0 |
| 镜像 / 容器导出导入 | `POST /api/v1/hosts/{hostId}/{images,containers}/{export,import,commit}` | 0.14.0 |
| 容器 / 卷文件浏览 | `POST /api/v1/hosts/{hostId}/{containers,volumes}/files/{list,download}` | 0.14.0 |
| 资源 Webhook | `POST /api/v1/webhooks/{token}` | 0.15.0 |
| 镜像自动更新策略 | `POST /api/v1/hosts/{hostId}/images/auto-update` | 0.15.0 |

> 审计写入由**应用服务层统一埋点**（非每个 Controller 各写一遍），中间件兜底记录 4xx / 5xx。
> **Webhook 端点无需认证头**——URL 里的 token 即凭据；因此令牌必须可撤销、可重置，且随库备份。

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

### 主机查询（0.0.8 / 0.0.9，**已实现**）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 可见主机列表 | `POST /api/v1/hosts/list` | 任意已登录 | 返回**当前用户可见**的主机（含 `status` 与 `tags`）；全局持有内置 `admin` 角色者返回全部，其余按**带作用域的角色分配**过滤（判定标准是是否具备 `host.read`）。可选 body 见下方「主机列表过滤」 |
| 单台概览 | `POST /api/v1/hosts/{hostId}/overview` | 任意已登录 | 返回单台主机的 `version` / `counts` / `daemon`（`startedAt` **0.6.0 移除**）；失败形态 `1300`/404、`1501`/403 |

**两个端点共用同一个响应形状**（列表里取不到的字段为 `null`，对应界面上的「—」占位）：

```jsonc
// POST /api/v1/hosts/local/overview     （列表接口返回同形状的数组）
// Request: 无 Body
// Response.data
{
  "id": "local",                       // **等于主机名**，也是路径里的 hostId（不是 hosts.id 那个 Guid）
  "name": "local",
  "type": "socket",                    // socket | tcp
  "endpoint": "/var/run/docker.sock",
  "enabled": true,
  "status": "online",                  // online | offline | unknown
  "counts": {                          // 列表接口为 null（离线时也为 null）
    "containers": 37, "images": 30, "volumes": 5, "networks": 4,
    "containersRunning": 35,           // 取自 /info 的 ContainersRunning
    "containersStopped": 2,            // 取自 /info 的 ContainersStopped
    "containersHealthy": 15,           // 容器列表 Status 文本里的 (healthy)
    "containersUnhealthy": 1,          // 同上，(unhealthy)
    "imagesSizeBytes": 4294967296      // /system/df?type=image 的 TotalSize；查不到为 null
  },
  "version": {                         // 列表接口为 null（离线时也为 null）
    "version": "27.3.1", "apiVersion": "1.47",
    "os": "linux", "arch": "x86_64", "goVersion": "go1.22.7"
  },
  "daemon": {                          // 列表接口为 null（离线时也为 null）；**0.6.0 新增**
    "operatingSystem": "Ubuntu 24.04.5 LTS", "osVersion": "24.04", "osType": "linux",
    "architecture": "x86_64", "kernelVersion": "6.17.0-40-generic",
    "cpuCount": 24, "memoryBytes": 67161847808,
    "storageDriver": "overlayfs", "storageDriverStatus": [["driver-type", "io.containerd.snapshotter.v1"]],
    "loggingDriver": "json-file", "cgroupDriver": "systemd", "cgroupVersion": "2",
    "defaultRuntime": "runc", "runtimes": ["runc", "io.containerd.runc.v2"],
    "initBinary": "docker-init",
    "containerdVersion": "b7a25261e029d68978bbf290df0a62fc819b3c51",
    "runcVersion": "v1.5.2-0-g29dd3dc2", "initVersion": "de40ad0",
    "plugins": { "volume": ["local"], "network": ["bridge", "host"], "log": ["json-file"], "authorization": [] },
    "registryMirrors": ["https://dockerhubcache.huansky.online/"],
    "insecureRegistries": ["127.0.0.0/8"], "indexServerAddress": "https://index.docker.io/v1/",
    "dockerRootDir": "/var/lib/docker",
    "securityOptions": ["name=apparmor,profile=default", "name=seccomp,profile=builtin"],
    "liveRestoreEnabled": false, "firewallBackend": "iptables",
    "cdiSpecDirs": ["/etc/cdi", "/var/run/cdi"],
    "httpProxy": null, "httpsProxy": null, "noProxy": null,
    "warnings": []
  }
}
```

> **`startedAt` 0.6.0 移除**：该字段自 `0.0.8` 起一直返回 `/info` 的 `SystemTime`，
> 而 `SystemTime` 是守护进程的**当前时间**（moby `daemon/info.go`：
> `SystemTime: time.Now().Format(time.RFC3339Nano)`），**不是启动时刻**——
> 官方 API 文档写的是 "Current system-time"。前端拿它算「已运行 x」恒为 0 秒。
> Docker Engine API 不提供守护进程启动时刻，也不提供宿主 boot time，
> 因此该字段在 `0.6.0` **移除**而不是换个口径继续显示一个假的。
>
> **`daemon` 0.6.0 新增**（字段清单与取舍见 [roadmap.md](roadmap.md#060---监控与可观测性)）：
> - **不重复 `version` 已有字段**：`os` / `arch` 已由 `/version` 给出，不在此重列
> - **不返回 Swarm 相关**：Swarm 属非目标（见 [overview.md](./overview.md)）
> - **`cpuCount` / `memoryBytes` 是静态上限，不是实时指标**（`/info` 只给 `NCPU` / `MemTotal`）；
>   界面上不得标成「当前 CPU / 内存」
> - **代理 URL 强制遮蔽 userinfo**：`http://user:pass@host` 里的密码不得进入响应体
> - **`firewallBackend` 是驱动名字符串**：SDK 4.3.3 里 `/info` 的该字段是对象
>   （`{ Driver, Info }`），这里只取 `Driver`——`Info` 是驱动内部细节，换一个后端就没有对应含义
> - **不新增 Docker 调用**：全部字段来自已有的那次 `/info`

> **两个端点为什么共用一个形状**：前端把「主机列表」与「单台概览」都装进同一个 `HostOverview` 类型，
> 列表里缺的字段本来就是「这趟没取」。分成两个类型只会让前端每处都写两次字段访问。
>
> **`id` 是主机名而不是 `hosts.id`（Guid）**：前端的路由、主机切换与深链用的都是主机名；
> 把 Guid 放进来会立刻多出一个「界面上的 id 与 URL 里的 hostId 不是同一个东西」的坑。
> Guid 只用于权限外键与审计。
>
> **列表刻意不含 `counts` / `version` / `daemon`**：取它们要额外调用守护进程（版本一次、计数若干次），
> 主机一多列表就会明显变慢甚至超时。列表只对每台主机做一次轻量探测（`/version`），
> 那两项由概览接口按需取。
>
> **`counts` 里的细分项口径**：`containersRunning` / `containersStopped` 取自 `/info`（一次调用）；
> 健康与不健康只能从容器列表的 `Status` 文本里读（`Up 2 hours (healthy)`），因此概览会多列一次容器；
> `imagesSizeBytes` 取自 `/system/df?type=image` 的 `TotalSize`（镜像层**去重后**的实际占用），
> 查不到时是 `null` 而非 `0`——前端必须把它渲染成「—」，0 字节会被读成「没有镜像」。
> `containersHealthy + containersUnhealthy` **通常小于** `containers`：没配健康检查的容器两边都不计。
>
> **`status` 有三态**：`online` / `offline` / `unknown`。探测失败但原因不属于「连不上」
> （凭据过期、TLS 握手异常、守护进程返回无法解释的错误）记 `unknown`——
> 一律报 `offline` 会让人去查网络，而真正的病因在别处。
>
> **主机离线不是错误**：概览对离线 / 已停用主机返回 `200` + `status: "offline"` + 空字段，
> 而不是 `502`——界面要把离线显示成一个状态，不是一个错误页。只有「主机本身不存在」才是 `1300`/404。
>
> **非管理员只看到被授权的主机**（0.1.0 起）：过滤由 `IPermissionChecker.FilterVisibleHostsAsync`
> 一次查库完成（数据来自带作用域的角色分配，判定标准是是否具备 `host.read`）。
> 没被授权时返回**空数组**而不是 403——列表接口一报错，前端的主机选择器整块就红了，
> 而它本该显示「暂无可用主机」。
> 单台概览则走 403/`1501`（那是明确的一次越权访问）。
>
> **管理员不受这条限制**：全局持有内置 `admin` 角色者由 `PermissionChecker` 短路放行且**不查库**——
> 管理员的语义本就是「全部可见」，为它插 N 条分配行意味着每加一个管理员就要同步维护 N 条数据，
> 而一旦漏同步，那个管理员就莫名看不见某台机器，比「表里没有他的记录」更难排查。

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
  "authKind": "none",         // none | tls | basic；只有方式，**不含任何凭据内容**
  "groupId": "…0000a3"        // 所属分组主键；null = 未分组
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
### 主机分组（0.0.13）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/host-groups/list` | 任意已登录 | 总览页展示用；无分页 |
| 获取列表 | `POST /api/v1/admin/host-groups/list` | admin | 管理页用；与上者**同形状** |
| 创建分组 | `POST /api/v1/admin/host-groups/create` | admin | Body: `{ name, description?, sortOrder? }`；名称重复 → `1304`/400 |
| 更新分组 | `POST /api/v1/admin/host-groups/{groupId}/update` | admin | Body: `{ name?, description?, sortOrder? }`；未提供表示不改 |
| 删除分组 | `POST /api/v1/admin/host-groups/{groupId}/delete` | admin | 组内主机**自动回到未分组**，不连带删主机 |
| 调整归属 | `POST /api/v1/admin/hosts/{hostId}/group` | admin | Body: `{ groupId: "<主键>" \| null }`；`null` = 移出分组；分组不存在 → `1305`/404 |

```jsonc
// POST /api/v1/host-groups/list  →  Response.data
[
  {
    "id": "00000000-0000-0000-0000-0000000000a1",
    "name": "production",           // 管理员可改的展示名
    "description": "线上生产负载",    // 可空
    "sortOrder": 10,
    "hostCount": 2
  }
]
```

> 分组是**展示与选择 / 过滤的边界**（0.6.0 起：总览按分组聚合、总览可按分组过滤），
> 但**不参与授权**——「谁能碰哪些主机」只由带作用域的角色分配回答。
> 两者混在一起会让「把主机挪进某个分组」变成一次隐式提权。理由同
> [architecture.md](./architecture.md) 的实体清单说明。
>
> **管理端与展示端分成两个列表端点**：总览页对**所有角色**可见，而管理端点带写能力。
> 混用会让非 admin 的总览页整块报错（与查询/管理主机列表分离同因）。
>
> **归属调整单设端点而不是塞进 `hosts/{hostId}/update`**：`update` 的语义是「未提供表示不改」，
> 于是「移到未分组」就没有表达方式——JSON 分不清「没给 `groupId`」与「`groupId: null`」。
> 这与凭据那边需要 `clearCredentials` 是**同一个坑**，那次踩过，这次直接给显式端点。
>
> **删除分组不删主机**：分组只是标签。级联删主机是灾难性的（配置与凭据一起没了），
> 而「主机还在、只是回到未分组」既安全又能被管理员立刻纠正。
>
> **名称重复用独立错误码 `1304`**（分组不存在用 `1305`）：与 `1303`（主机名重复）区分开，
> 前端才能把提示精确落在「分组名」这个字段上。
>
> **不做分页**：分组由人工维护（现实里是个位数）。加 `pageIndex` 只会让调用方多一层
> 「要不要翻页」的判断，而真实数据永远在第一页。
---

### 监控与可观测性（0.6.0）

> **容器指标的数据源是 `0.5.0` 的 `ContainerStatsHub`**，本档只负责**消费侧**
> （列表指标列、健康检测、订阅调度）。因此本节**不新增 Hub**。
>
> **宿主机级实时指标明确不做**（见本节末尾），所以也不存在 `HostStatsHub`。

#### 主机标签（Tag）

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/host-tags/list` | 任意已登录 | 供过滤与打标用；无分页 |
| 创建标签 | `POST /api/v1/admin/host-tags/create` | admin | Body: `{ name, description?, color? }`；名称重复 → `1307`/400 |
| 更新标签 | `POST /api/v1/admin/host-tags/{tagId}/update` | admin | Body: `{ name?, description?, color? }`；未提供表示不改 |
| 删除标签 | `POST /api/v1/admin/host-tags/{tagId}/delete` | admin | 关联关系一并清除，**不删主机** |
| 批量打标 | `POST /api/v1/admin/hosts/tags/assign` | admin | Body: `{ hostIds: string[], tagIds: string[] }`；**幂等** |
| 批量清标 | `POST /api/v1/admin/hosts/tags/revoke` | admin | Body: `{ hostIds: string[], tagIds: string[] }`；**幂等** |

> **与分组的分工**：Tag 是**多对多**打标（一个主机多个标签、一个标签多台主机），
> Group 是**单一归属**（一个主机一个分组）。两者都**不参与授权判定**。
> 标签不存在 → `1306`/404；标签名已占用 → `1307`/400。
>
> **删除标签不删主机**，与删除分组的处理同因（标签只是标记）。
>
> **打标 / 清标是幂等的**：重复打同一个标签不报错——前端允许「全选 + 批量打标」
> 这种必然包含重复的操作，非幂等会让第二次点击变成失败。

#### 总览过滤

`POST /api/v1/hosts/list` 与 `POST /api/v1/hosts/{hostId}/overview` 支持以下**可选**过滤参数：

| 参数 | 类型 | 说明 |
|------|------|------|
| `groupId` | `string?` | 按分组过滤；`null` / 空串 = **未分组** |
| `tagIds` | `string[]?` | 按标签过滤（**任一命中**）；与 `groupId` 是 AND 关系 |
| `status` | `string?` | `online` / `offline` / `unknown` |
| `search` | `string?` | 按名称 / endpoint 模糊匹配，不区分大小写 |

> **「未分组」必须是一个能传的值**：前端把它做成一个可点击的过滤项，
> 而「未分组」在数据上就是 `group_id IS NULL`——于是它只能用**空值**表达。
> 这就要求调用方能区分「没传 `groupId`」（不过滤）与「传了空串」（只看未分组），
> 因此约定：**字段缺失 = 不过滤，空串 / `null` = 未分组**。

#### 磁盘用量

| 操作 | 路径 | 说明 |
|------|------|------|
| 磁盘用量 | `POST /api/v1/hosts/{hostId}/system/df` | 无 Body；**`type` 可选**（`image` / `container` / `volume` / `build-cache`），省略则返回全部 |

```jsonc
// POST /api/v1/hosts/{hostId}/system/df?type=volume  →  Response.data
// （不带 type 时四类全查；带 type 时**未查询的类为 null**）
{
  "images": {
    "totalCount": 30, "activeCount": 12,
    "totalSize": 10737418240, "reclaimable": 3221225472,
    "items": [ { "id": "sha256:…", "name": "nginx:latest", "size": 123456, "inUse": true } ]
  },
  "containers": { "totalCount": 0, "activeCount": 0, "totalSize": 0, "reclaimable": 0, "items": [] },
  "volumes": {
    "totalCount": 5, "activeCount": 3,
    "totalSize": 3221225472, "reclaimable": 0,
    "items": [ { "id": "app-data", "name": "app-data", "size": 1048576, "inUse": true } ]
  },
  "buildCache": { "totalCount": 2, "activeCount": 0, "totalSize": 536870912, "reclaimable": 536870912, "items": [] }
}
```

> **`type` 白名单**：`image` / `container` / `volume` / `build-cache`（注意带连字符）。
> 其它值 → `1000`/400，**不**静默忽略（忽略会让用户以为自己筛选了）。
>
> **`type` 过滤不能省**：不指定时守护进程会同时统计四类，在镜像多的机器上要多等好几秒。
>
> **未查询的类是 `null` 而不是空桶**：空桶会被读成「这台机器上没有卷」，
> 而真相是「这趟没查」——与 `imagesSizeBytes` 查不到时给 `null` 同源。
>
> **`totalSize` / `reclaimable` 可为 `null`**：`null` = 守护进程没给用量
> （老版本守护进程、第三方驱动，或用量查询失败），`0` = 确实是 0。
> 界面必须分别渲染——统一显示「—」会丢掉「确实是 0」这个信息。
>
> **条目里的 `inUse` 也可为 `null`**（未知）：镜像按「被至少一个容器引用」判，
> 容器恒为 `true`，卷看 `UsageData.RefCount`，构建缓存看缓存记录的 `InUse`。

#### 容器健康与退出原因

`POST /api/v1/hosts/{hostId}/containers/inspect` 的响应**新增**三个字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `healthStatus` | `string?` | `healthy` / `unhealthy` / `starting` / `none`；**未配置健康检查**是 `none`，不是 `null` |
| `restartCount` | `number` | 守护进程累计的重启次数 |
| `oomKilled` | `boolean` | 上一次退出是否被 OOM 杀死 |

> **健康状态只从 inspect 取**：列表接口不返回它——`docker ps` 没有健康检查列，
> 要拿到就得逐个 inspect，与「每 5 秒刷一次列表」的代价不成比例。
> 因此列表页的健康列走**单独的批量端点**（见下），查询失败时降级为「—」而不是报错。
>
> **`healthStatus` 不用 `null` 表示「未配置」**：那是两种不同的事——
> `none`（没配健康检查，永远不会变）与 `null`（取不到）。混成一个值，
> 界面就没法把「没配」与「查不到」分开表达。

#### 批量健康查询

| 操作 | 路径 | 角色 | 说明 |
|------|------|------|------|
| 批量健康与退出原因 | `POST /api/v1/hosts/{hostId}/containers/health` | 任意已登录（需 `container.read`） | Body: `{ containerIds: string[] }`（≤100 项，服务端去重） |

```jsonc
// POST /api/v1/hosts/local/containers/health
// Request: { "containerIds": ["abc123…", "def456…"] }
// Response.data
{
  "items": [
    {
      "containerId": "abc123…",
      "name": "web",
      "healthStatus": "unhealthy",   // none | starting | healthy | unhealthy
      "restartCount": 7,
      "exitCode": 137,              // 运行中恒为 null
      "oomKilled": true,
      "running": false
    }
  ]
}
```

> **查不到的容器直接缺席**（不从 `items` 返回）：容器可能在两次刷新之间被删掉，
> 那是列表页的正常竞态；单条失败不该让整批请求变成错误。前端对缺失项显示「—」。
>
> **上限 100 项**（= 列表一页上限），服务端并发 inspect 8 条：串行时 100 个容器
> 在远程主机上要等好几秒，并发太高又会把小型守护进程打满。
>
> **为什么不塞进 `list`**：列表每 5 秒刷一次，把 N 次 inspect 绑进去等于把刷新成本放大 N 倍。
> 拆成独立端点后，界面可以只在「健康列开着、且只查当前页」时才去查。

#### 明确不做

- **宿主机级实时 CPU / 内存 / 网络 / 负载**：Docker Engine API **没有**宿主机级指标端点——
  `/info` 只给 `NCPU` / `MemTotal` 这样的静态上限，`docker stats` 只针对容器。
  用容器聚合值冒充宿主机指标，会让同一个页面出现两套口径，比不给更糟。
  需要它就得在目标主机上部署 Agent（1.0.0 之后）。
- **一次性 stats API**：`POST /api/v1/hosts/{hostId}/containers/stats`（单次 `docker stats --no-stream`）
  明确不做，实时值走 `0.5.0` 的 Hub。
- **历史指标持久化**：进程内只留最近 60 点环形缓冲供 sparkline，**不落库**。

---

### 容器管理（0.0.9 / 0.3.0 / 0.7.0 / 0.14.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| **创建容器** | `POST /api/v1/hosts/{hostId}/containers/create` | 0.0.9 | Body: `{ image, name?, cmd?, entrypoint?, env?, labels?, ports?, volumes?, restartPolicy?, autoRemove?, network?, hostname?, workingDir?, tty?, stdinOpen? }`（**精选子集**，非全部 `CreateContainerParameters` 字段，理由见下文）。**0.3.0** 新增高级参数 `user?`、`capAdd?`、`capDrop?`、`healthCheck?`、`devices?`、`sysctls?`、`ulimits?`、`logDriver?`、`memoryLimitBytes?`、`nanoCpus?`（见下） |
| 获取列表 | `POST /api/v1/hosts/{hostId}/containers/list` | 0.0.9 | Body: `{ all?, pageIndex?, pageSize?, sortBy?, sortDir? }`；分页在服务端**内存**完成（Docker 无分页参数），`pageSize` 上限 100 |
| 启动 | `POST /api/v1/hosts/{hostId}/containers/start` | 0.0.9 | Body: `{ containerId }` |
| 停止 | `POST /api/v1/hosts/{hostId}/containers/stop` | 0.0.9 | Body: `{ containerId, waitSeconds? }`；`waitSeconds` 取值 0–300（默认 10，与 Docker 一致） |
| 重启 | `POST /api/v1/hosts/{hostId}/containers/restart` | 0.0.9 | Body: `{ containerId }` |
| 删除 | `POST /api/v1/hosts/{hostId}/containers/remove` | 0.0.9 | Body: `{ containerId, force? }`；不带 `force` 删运行中的容器返回 `1404`/409 |
| **批量删除** | `POST /api/v1/hosts/{hostId}/containers/batch-remove` | **0.1.0** | Body: `{ containerIds: string[], force? }`；响应为汇总结果，**部分成功也是 200**（见下） |
| **批量启停** | `POST /api/v1/hosts/{hostId}/containers/batch-start` | **0.7.0** | Body: `{ containerIds: string[] }`；响应与 `batch-remove` **同形状**（`succeeded` / `failed` / `failures`），**部分成功也是 200** |
| **批量启停** | `POST /api/v1/hosts/{hostId}/containers/batch-stop` | **0.7.0** | Body: `{ containerIds: string[], waitSeconds? }`；`waitSeconds` 取值 0–300（默认 10） |
| **批量启停** | `POST /api/v1/hosts/{hostId}/containers/batch-restart` | **0.7.0** | Body: `{ containerIds: string[] }`；响应形状同上 |
| **重命名** | `POST /api/v1/hosts/{hostId}/containers/rename` | **0.3.0** | Body: `{ containerId, name }`；名称规则同 `create`（字母/数字开头，其后含 `_ . -`）；名称已被占用返回 `1404`/409 |
| **热更新** | `POST /api/v1/hosts/{hostId}/containers/update` | **0.3.0** | Body: `{ containerId, restartPolicy?, memoryLimitBytes?, nanoCpus? }`；**只开放真能热改的字段**，镜像/端口/环境变量/挂载/网络**不支持**。省略 = 保持当前值，**传 0 是非法**（见下） |
| **复制** | `POST /api/v1/hosts/{hostId}/containers/duplicate` | **0.3.0** | Body: `{ containerId, name?, start? }`；默认名「原名 + `-copy`」，被占用时自动递增 `-2`/`-3`。**原容器不受影响**（不停止、不改名、不删除） |
| **追加挂载** | `POST /api/v1/hosts/{hostId}/containers/add-volumes` | **0.3.0** | Body: `{ containerId, volumes, start? }`；⚠️ **会重建容器**，容器 ID 会变。`start` 省略时**沿用原容器的运行状态** |
| 获取日志 | `POST /api/v1/hosts/{hostId}/containers/logs` | 0.0.9 / 0.5.0 | Body: `{ containerId, tail?, since?, until?, includeStderr? }`；`tail` 取值 0–5000，省略表示全部；**不支持** `follow`（实时流走 SignalR） |
| 下载日志 | `POST /api/v1/hosts/{hostId}/containers/logs/download` | 0.5.0 | Body: `{ containerId, since?, includeStderr? }`；返回 `text/plain` 全文，**不受 `tail` 上限约束** |
| 获取详情 | `POST /api/v1/hosts/{hostId}/containers/inspect` | 0.0.9 | Body: `{ containerId }` |

#### 高级创建参数（0.3.0 已实现）

界面按「常用 / 高级」两级折叠。高级参数与普通参数在服务端**没有任何处理差异**——
Docker 的创建接口只有一个，差别只在界面上，因此不是两套端点。

```jsonc
{
  "image": "nginx:latest",                  // 必填
  "name": "app",                            // 常用

  // —— 以下为 0.3.0 新增的「高级」组 ——
  "user": "1000:1000",
  "capAdd": ["NET_ADMIN"],
  "capDrop": ["ALL"],                       // 与 capAdd 同项 → 1000
  "healthCheck": {
    "test": ["CMD-SHELL", "curl -f http://localhost/ || exit 1"],
    "intervalSeconds": 30,                  // 至少 1 秒
    "timeoutSeconds": 5,
    "startPeriodSeconds": 10,
    "retries": 3                            // 至少 1；0 的含义是「永不判定失败」→ 1000
  },
  "devices": [{ "hostPath": "/dev/snd", "containerPath": "/dev/snd", "permissions": "rwm" }],
  "sysctls": { "net.core.somaxconn": "1024" },
  "ulimits": [{ "name": "nofile", "soft": 1024, "hard": 4096 }],  // 软/硬缺一时互相补齐
  "logDriver": { "type": "json-file", "options": { "max-size": "10m" } },
  "memoryLimitBytes": 536870912,           // 下限 6 MiB（见下）；创建时 0 = 不限制
  "nanoCpus": 1500000000                    // 10^9 = 1 核，上限 1024 核
}
```

> **`healthCheck.test` 是数组而不是字符串**：Docker 有三种形态（`CMD-SHELL` / `CMD` / `NONE`），
> 而它们在 API 上是同一个字段的不同写法。压成字符串就得靠分隔符再解析回来，
> 而命令里本身带引号与空格——解析规则与 Docker 不一致时，症状是「配置看着对但没生效」。
>
> **`test: ["NONE"]` 表示关闭检查，且不能再带其他字段**：Docker 见到 `NONE` 就关闭检查，
> 此时再给 interval / retries 是没有意义的组合，放行会让用户以为自己配了宽限期而实际一条都不会执行。
>
> **`logDriver.type` 的存在性无法在本层校验**：驱动名是守护进程插件注册表里的字符串
> （`journald` 需要 systemd、`syslog` 需要 rsyslog）。传了本机没有的驱动时，
> 守护进程会在**启动容器时**才失败。本层只做非空校验，其余如实交给守护进程。
>
> **列表项里的空串是错误而不是「跳过」**：`capAdd: ["", "NET_ADMIN"]` 里的空串几乎总是
> 前端把一个空输入框也提交了；静默跳过会让用户以为「我勾的那项生效了」，
> 而真正的问题（多了一个空输入框）永远不会被发现。
>
> **`memoryLimitBytes` 下限是 6 MiB**（`6291456`）：Docker 把它从历史上的 4 MiB 提上来了，
> 实测 `6291455` 会被拒。取**两者中更严的 6 MiB**，对新旧两种守护进程都成立。

#### 热更新（`update`）的能力边界（0.3.0 已实现）

| 字段 | 省略时 | 合法值 | 备注 |
|------|--------|--------|------|
| `restartPolicy` | 保持当前值 | `no` / `always` / `on-failure` / `unless-stopped` | — |
| `memoryLimitBytes` | 保持当前值 | ≥ 6 MiB | **`0` 是非法的**（见下） |
| `nanoCpus` | 保持当前值 | (0, 1024×10⁹] | **`0` 是非法的**（见下） |

> ⚠️ **`0` 不等于「取消限制」，而是会被拒绝（`1000`）**。实测 `docker update --memory=0`
> **返回 200，而容器上的限制根本没变**——守护进程对 update 的处理是「字段非 0 才应用」，
> 于是 `0` 与「不传这个字段」走同一条路。放行 `0` 会让用户看到「保存成功」
> 而容器仍跑在旧限制下，那正是这类限制本该避免的后果。要真的取消限制只能重建容器。
>
> **先 inspect 再 update**：Docker 的 update 对「未传的字段」在不同 API 版本上行为不一致
> （老版本会把未传的数值字段一并写回默认值）。服务端先取当前值、把未修改的字段按当前值回填，
> 再回带**更新后实际生效**的值——前端拿到的永远是真实结果，而不是它请求的值。
>
> **响应 `data` 是更新后的值**：`{ containerId, restartPolicy, memoryLimitBytes, nanoCpus }`。
> 未设限时后两项为 `null`（不是 0）。
>
> **镜像、端口、环境变量、挂载、网络不在本端点能力范围内**：Docker 不支持热改它们，
> 改了就得重建容器。界面上不应给这些字段「保存」按钮。

#### 复制与追加挂载的响应形态（0.3.0 已实现）

两者共用 `ContainerRecreatedResult`：

```jsonc
{
  "containerId": "…",              // 新容器的 ID
  "name": "app",                   // 新容器名
  "replacedContainerId": null,     // ⚠️ 追加挂载时指向**被让位的旧容器**；复制时为 null
  "running": false,
  "warnings": [],
  "recreated": true                // 复制为 false（原容器毫发无损）；追加挂载恒为 true
}
```

> **追加挂载本质是重建，容器 ID 一定变**：Docker 没有「给已存在容器加挂载」的接口。
> 界面**必须**把这一点写在二次确认里。
>
> **旧容器让位而不是删除**：重建要用回原来的名字，而旧容器占着这个名字。
> 服务端把旧容器**改名**成 `whalefleet-replaced-<短ID>`，而不是删掉——
> 删除不可逆，而重建失败时我们需要它还在。新建成功后旧容器以让位名**保留在列表里**，
> 用户确认新容器没问题后自己删；`replacedContainerId` 让前端能指到它，
> 否则界面上会出现一个来历不明的容器而没人敢删。
>
> **失败回滚**：重建失败时把旧容器改回原名并恢复它原本的运行状态，
> 然后把**原始异常**原样抛出——回滚本身失败只记日志，否则会让真正的失败原因消失。
>
> **追加是追加不是覆盖**：请求里给的是「要新增的挂载」，原有挂载全部保留；
> 同一目标路径重复挂载 → `1000`（几乎总是配置错了）。
>
> **`start` 省略时沿用原容器的运行状态**而不是「总是启动」：
> 对一个本来就没运行的容器擅自启动它，是用户没要求的副作用。

#### 批量删除的响应形态（0.1.0 已实现）

容器与镜像的 `batch-remove` 共用同一个响应形状，因此前端的批量操作条可以用同一套逻辑处理两者：

```jsonc
{
  "requested": 3,        // ⚠️ 是**去重后**的个数：传两次同一个容器不会显示成「成功 1、失败 1」
  "succeeded": 2,
  "failures": [
    { "resourceId": "abc123", "code": 1404, "message": "容器正在运行，需指定 force" }
  ]
}
```

> **部分成功返回 200 而不是 4xx**：HTTP 状态码表达不了「3 个里成了 2 个」。
> 整批失败**也**是 200——逐条失败的原因通常彼此不同（有的 `1404`、有的 `1401`），
> 没有哪一个能代表整批的状态码。
>
> **真正的错误响应只有两类**，且都是**整批失败**（没有任何一个条目被处理）：
> 入参非法（列表为空 / 全是空白 / 超过 100 条 → `1000`/400）与
> 主机不可达或已停用（`1301`/502、`1302`/400）。界面必须把这两种显示成
> 「操作无效」而不是「部分成功」。
>
> **`failures[].code` 与单条端点抛出的错误码完全一致**，因此界面能复用同一套提示文案，
> 而不必为批量场景另写一张错误码表。
>
> **服务端去重**：同一个容器 ID 出现两次时，第二次必然是「已删除」的 `1401`。
> 用户看到「成功 1、失败 1，失败的还是同一个容器」只会觉得系统在胡说；
> 去重之后「删 1 个、成功 1 个」正是他期望的语义。
>
> **主机客户端只取一次、串行处理**：取一次是因为 N 个容器取 N 次就是 N 次可达性探测，
> 而它抛的 `1301`/`1302` 本身就是整批失败，必须发生在循环之外；
> 串行是因为删除会触发网络断开、卷解绑等清理动作，并发时守护进程内部的锁会让失败原因不可预测。
>
> **镜像批量响应不含逐条的解绑标签明细**（那是单条端点的职责）：
> 批量场景下用户需要知道的只有「删没删掉」，逐条明细只会让响应体随选中数量膨胀。

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

> **`sortBy` / `sortDir`（排序）**
>
> | `sortBy` | 排序依据 |
> |---|---|
> | `name` | 容器名（与响应里的 `name` 取值一致，已去前导斜杠） |
> | `image` | 镜像名；缺失时回退到 `imageId` |
> | `state` | Docker 原始状态文本 |
> | `created` | 创建时间 |
>
> `sortDir` 取 `asc` 为升序，其余（含省略）按降序。两者**都省略时保持守护进程返回的顺序**
> （创建时间降序），不额外重排。
>
> 未知取值不报错（`sortBy` 退回 `created`、`sortDir` 退回降序）；排序在分页之前执行，
> `total` 是排序后的总条数；同值项以容器 ID 作次级键，保证翻页顺序稳定——
> `docker compose up` 拉起的一批容器创建时间戳常常完全相同，没有次级键它们会在翻页间跳位。
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
    "healthStatus": "none",                   // 未配置健康检查是 none，不是 null；有则为 healthy / unhealthy / starting
    "restartCount": 3                         // 守护进程累计的重启次数
  },
  "config": {
    "image": "nginx:latest",
    "cmd": ["nginx", "-g", "daemon off;"],
    "entrypoint": ["/docker-entrypoint.sh"],
    "env": ["PATH=…"],                        // ⚠️ 常用于存放密码 / Token；逐资源授权从 0.12.0 起；MCP 工具返回时此项的值默认遮蔽（0.18.0）
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
{ "containerId": "nginx", "tail": 200, "since": 1735785600, "until": 1735789200 }
// Response.data（0.5.0 起为结构化行）
{
  "containerId": "nginx",
  "lines": [
    { "seq": 1, "timestamp": "2026-01-02T03:04:05.123456789+00:00", "line": "nginx: [emerg] …" }
  ],
  "truncated": false      // 是否**可能**还有更早的日志没返回
}
```

> ⚠️ **0.5.0 的破坏性变更**：`logs` 的 `data` 从 `{ containerId, content }`
> （一整段文本）变成 `{ containerId, lines[], truncated }`。
> 原因有三：① 前端要**按行**渲染并显示行号与时间戳；② 要能**向上加载更早的日志**
> （整段文本没法增量拼接）；③ 旧实现把 Docker 的逐行回调直接拼接，
> 所有日志会挤成一行（`"helloworld"`）——结构化行数组顺带修掉了它。
>
> `seq` 是**展示序号**（从 1 起，等于行在列表里的位置），不是服务端流内序号。
>
> **`until` 是「含边界」的**（Docker 的语义是「时间戳 ≤ until 的都返回」），
> 因此用它加载更早的一页时，边界那一秒的行会**重复出现**——调用方需要自己去重。
>
> `tail` 上限为 **5000**（0.5.0 起从 10000 下调），越界返回 `1000`/400。
> `truncated` 的判据是「请求了 N 行且正好拿回 N 行」，因此它只表示
> **可能**还有更早的日志；再往上翻一页拿到 0 行才知道到底了。

```jsonc
// POST /api/v1/hosts/local/containers/logs/download
// Request
{ "containerId": "nginx", "includeStderr": true }
// Response：200 + text/plain; charset=utf-8 + Content-Disposition
// 正文是**原始文本**（逐行输出，不带时间戳前缀）
nginx: [emerg] …
nginx: [emerg] …
```

> **为什么下载要单独一个端点**：一次性接口受 `tail` 上限（5000 行）约束，
> 导出的文件会缺头；而用户点「下载」要的通常就是**完整**日志。
> 下载端点在服务端跟着守护进程的日志流边读边写，**不受上限约束**。
>
> **错误响应仍是 JSON**（`ApiResult`）：出错时正文还没开始写，
> 响应头还能正常返回，前端因此要**先看 `Content-Type`** 再决定按文本还是按 JSON 解析。

> **分页语义**：Docker 不提供容器列表的分页参数，因此 `total` 是**一次性拉全量后**的条数，
> 切片在服务端内存完成。`pageIndex` 从 0 开始、负数按 0 处理；`pageSize` 默认 20、上限 100，
> **夹紧后的实际值一并返回**（传 5000 时能从响应里看出服务端用了 100）。页码越界返回空 `items`
> 而**不是**自动回到最后一页——后者会让调用方以为「这就是第 100 页的内容」。
>
> **默认只列运行中的容器**（与 `docker ps` 一致）；`all: true` 等价于 `docker ps -a`。
>
> **日志不做实时跟随**：一次性 HTTP 响应跟着日志流只会把连接挂到容器退出；
> 实时流是 SignalR 推送要解决的事（见下方「SignalR Hub 规范」）。
> `tail` 越界（负数或 > 5000）返回 `1000`/400 而**不是**静默截断——
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
  "hostname": "web-1", "workingDir": "/app", "tty": false, "stdinOpen": false,

  // —— 0.3.0 高级参数（界面按「常用 / 高级」两级折叠）——
  "user": "1000:1000",
  "capAdd": ["NET_ADMIN"], "capDrop": ["ALL"],
  "healthCheck": { "test": ["CMD-SHELL", "curl -f http://localhost/ || exit 1"], "intervalSeconds": 30, "retries": 3 },
  "devices": [{ "hostPath": "/dev/snd", "permissions": "rwm" }],
  "sysctls": { "net.core.somaxconn": "1024" },
  "ulimits": [{ "name": "nofile", "soft": 1024, "hard": 4096 }],
  "logDriver": { "type": "json-file", "options": { "max-size": "10m" } },
  "memoryLimitBytes": 536870912, "nanoCpus": 1500000000
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

```jsonc
// 0.3.0：rename / update / duplicate / add-volumes

// rename  →  { containerId, name }
// Response.data
{ "containerId": "3f2c…", "name": "web-1-renamed" }

// update  →  { containerId, restartPolicy?, memoryLimitBytes?, nanoCpus? }
// Response.data（**更新后实际生效**的值，未设限的项为 null）
{ "containerId": "3f2c…", "restartPolicy": "unless-stopped", "memoryLimitBytes": 536870912, "nanoCpus": null }

// duplicate → { containerId, name?, start? }
// add-volumes → { containerId, volumes, start? }
// Response.data（两者共用）
{
  "containerId": "9a8b…",              // 新容器
  "name": "web-1",
  "replacedContainerId": null,         // add-volumes 时指向被让位的旧容器
  "running": false,
  "warnings": [],
  "recreated": true
}
```

> **`update` 的 `0` 被拒绝而不是当「取消限制」**：实测 `docker update --memory=0` **返回 200
> 而限制没变**（守护进程对 update 是「字段非 0 才应用」，`0` 与「不传」同义）。
> 放行 `0` 会让用户看到「保存成功」而容器仍跑在旧限制下。要取消限制只能重建。
>
> **`add-volumes` 是重建，容器 ID 一定变**，旧容器被**改名**成 `whalefleet-replaced-<短ID>`
> 而非删除；重建失败时自动改回原名并恢复原运行状态。界面**必须**在二次确认里写明这一点。

```jsonc
// 0.3.0：images/tag
// Request
{ "imageId": "nginx:latest", "repository": "registry.example.com/team/app", "tag": "v1.2.3" }
// Response.data
{
  "imageId": "sha256:…",              // ⚠️ 与源镜像**同一个** ID：tag 不复制镜像
  "reference": "registry.example.com/team/app:v1.2.3",
  "repoTags": ["nginx:latest", "registry.example.com/team/app:v1.2.3"]
}
```

> **`tag` 不复制镜像**：只是给同一个镜像 ID 再加一个引用，磁盘占用不变。
> 界面应把行更新成「同一镜像的另一个标签」而不是「多了一行镜像」——
> 后者会让用户以为副本可以随便删（删掉最后一个标签时镜像确实会消失）。
>
> **`repository` 不得含标签**：用户很容易照着「源镜像」那一栏把 `nginx:1.27` 整个粘进来。
> 本服务在本地拒掉（`1000`）并说清该改哪个字段，而不是拼成 `nginx:1.27:alpine`——
> 那会打到一个谁都没要求过的镜像上。
>
> **`tag` 省略或空串都取 `latest`**（与 `pull` 端点一致）。
> 「同一个概念在两个端点上有两种含义」比「悄悄给默认值」更难排查。

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

### 镜像管理（0.0.10 / 0.4.0 / 0.5.0 / 0.7.0）

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/hosts/{hostId}/images/list` | 0.0.10 | Body: `{ all?, search?, pageIndex?, pageSize?, sortBy?, sortDir? }`；分页在服务端**内存**完成（Docker 无分页参数），`pageSize` 上限 100 |
| 拉取 | `POST /api/v1/hosts/{hostId}/images/pull` | 0.0.10 | Body: `{ image, tag?, platform? }`；0.0.10 一次性返回结果，0.5.0 改 SignalR 实时进度 |
| 删除 | `POST /api/v1/hosts/{hostId}/images/remove` | 0.0.10 | Body: `{ imageId, force? }`；被容器占用的镜像返回 `1404`/409 |
| **批量删除** | `POST /api/v1/hosts/{hostId}/images/batch-remove` | **0.1.0** | Body: `{ imageIds: string[], force? }`；响应为汇总结果，**部分成功也是 200**（容器侧同形状） |
| **打标签** | `POST /api/v1/hosts/{hostId}/images/tag` | **0.3.0** | Body: `{ imageId, repository, tag? }`；对应 `docker tag`。`repository` **不得含标签**（否则 `1000`），`tag` 省略或空串取 `latest` |
| 获取详情 | `POST /api/v1/hosts/{hostId}/images/inspect` | 0.0.10 | Body: `{ imageId }` |
| **构建镜像** | `POST /api/v1/hosts/{hostId}/images/build` | **0.4.0** | `multipart/form-data`：文件字段 `context`（tar 包，**必须带 filename**）+ 表单字段 `tag` / `dockerfile` / `buildArgs` / `labels` / `target` / `platform` / `noCache`；本版一次性返回（进度推送随 0.5.0 改 SignalR） |
| **Push 镜像** | `POST /api/v1/hosts/{hostId}/images/push` | **0.4.0** | Body: `{ image, repositoryId?, platform? }`；`repositoryId` 省略时按镜像引用里的地址段自动匹配已登记的仓库 |
| **清理** | `POST /api/v1/hosts/{hostId}/images/prune` | **0.7.0** | Body: `{ danglingOnly? }`；返回回收量；**不是** `system/prune` |

#### 查询端点的请求 / 响应形态（0.0.10 已实现）

```jsonc
// POST /api/v1/hosts/local/images/list
// Request
{ "search": "nginx", "pageIndex": 0, "pageSize": 20 }
// Response.data
{
  "items": [
    {
      "id": "sha256:3f2c…",
      "repoTags": ["nginx:latest", "nginx:1.27"],   // 悬挂镜像（<none>）为空数组
      "repoDigests": ["nginx@sha256:abc…"],
      "created": "2026-01-02T03:04:05+00:00",
      "size": 187654321,                              // 字节
      "sharedSize": 0,                                // 与其他镜像共享的字节数
      "containerCount": 2                            // 引用该镜像的容器数
    }
  ],
  "total": 5, "pageIndex": 0, "pageSize": 20
}
```

> **`sortBy` / `sortDir`（排序）**
>
> | `sortBy` | 排序依据 |
> |---|---|
> | `repository` | 首个 `repoTags`（悬挂镜像按空串参与比较） |
> | `size` | 字节大小 |
> | `created` | 创建时间 |
> | `usedBy` | `containerCount` |
>
> `sortDir` 取 `asc` 为升序，其余（含省略）按降序。两者**都省略时保持守护进程返回的顺序**
> （创建时间降序），不额外重排。
>
> **未知取值不报错**：无法识别的 `sortBy` 按 `created` 处理，无法识别的 `sortDir` 按降序处理。
> 排序字段由客户端决定，版本错配时旧客户端 / 新服务端组合应该退回默认排序，而不是整个列表 400。
>
> **排序在分页之前**：`total` 是排序后的总条数，翻页不会漏项也不会重项。
> 同值项以镜像 ID 作次级键，保证顺序稳定——否则同一批时间戳相同的镜像在两次翻页间会跳位。

```jsonc
// POST /api/v1/hosts/local/images/inspect
// Request
{ "imageId": "nginx:latest" }      // 可传镜像 ID、short ID、仓库名或仓库名:标签
// Response.data
{
  "id": "sha256:3f2c…",
  "repoTags": ["nginx:latest"],
  "repoDigests": ["nginx@sha256:abc…"],
  "created": "2026-01-02T03:04:05+00:00",
  "size": 187654321,
  "architecture": "amd64", "os": "linux", "osVersion": "",
  "author": "", "comment": "",
  "env": ["PATH=/usr/local/sbin:…"],
  "cmd": ["nginx", "-g", "daemon off;"],
  "entrypoint": ["/docker-entrypoint.sh"],
  "exposedPorts": ["80/tcp", "443/tcp"],           // 去重后的键列表（Docker 那两个字段的值是空的）
  "volumes": ["/var/cache/nginx"],
  "labels": { "maintainer": "NGINX Docker Maintainers" },
  "workingDir": "",
  "layerCount": 7
}
```

```jsonc
// POST /api/v1/hosts/local/images/pull
// Request
{ "image": "nginx", "tag": "latest", "platform": "linux/amd64" }
// Response.data
{
  "imageId": "sha256:3f2c…",
  "reference": "nginx:latest",
  "size": 187654321,
  "repoTags": ["nginx:latest"]
}

// POST /api/v1/hosts/local/images/remove
// Request
{ "imageId": "nginx:latest", "force": false }
// Response.data
{ "untagged": ["nginx:1.27"], "deleted": ["sha256:3f2c…"] }   // 两者均为空数组（只解绑引用）
```

> **`image` + `tag` 的拼接规则**：`tag` 省略或为空时默认 `latest`。
> ⚠️ 若 `image` **已经含标签**（如 `nginx:1.27`）又传了另一个 `tag`，服务端返回 `1000`/400
> 而不是静默拼成 `nginx:1.27:alpine`——后者是 Docker 会接受但语义完全不是用户所想的写法。
>
> **拉取失败有两条路径，都要处理**：
> - 「镜像 / 仓库不存在」→ Docker 返回 404，**SDK 抛异常** → `1401`/404
> - 「仓库不让你拉」「网络中断」→ HTTP 层仍然是 200，错误**只在进度流里**带一条 `error`，
>   SDK 毫不知情 → 服务端检查进度消息后翻译成 `1402`/500，消息里带上守护进程给的原因
>
> 只等 `await` 返回而不检查进度流，第二类会被报成「200 + 空结果」——比报错更难排查。
>
> **0.0.10 的拉取进度只进日志**：拉取层数大时可以到几十秒，一次性 HTTP 响应期间客户端只能干等
> （前端可以先转圈）。进度同时写进 Serilog（Debug 逐层、Information 记完成）。
> **SignalR 实时进度推到 0.5.0**——那时列表页与详情页也顺带接上。
>
> **拉取完成后额外做一次 inspect**：为了让「拉取成功」的响应直接带上镜像 ID 与大小
> （验收标准要求），必须在完成后读一次镜像元数据；否则调用方还要再发一次请求才知道拉了什么。
>
> **`platform` 用于多架构镜像**：省略时由守护进程按主机架构选择；显式传 `linux/arm64` 可以
> 在 amd64 主机上拉 arm64 镜像（随后创建容器时需要配 `platform`）。
>
> **删除会连带解绑多个标签**：同一镜像 ID 可能有多个 `repoTag`，删除时守护进程返回哪些标签被解绑、
> 哪个 ID 被真正删除，上面的 `untagged` / `deleted` 就是这个——界面上要把被解绑的标签告诉用户，
> 否则会出现「我删了镜像，列表里另一个标签还在」的困惑。
>
> **`remove` 永远不传 `noPrune`**：那会连带删掉所有未被引用的父层镜像，
> 而本项目没有暴露任何“清理悬空层”的入口，删镜像顺手清了别的镜像是不可预期的数据损失。
>
> **分页语义**：与容器列表一致（Docker 无分页参数，服务端内存切片，`pageSize` 默认 20 / 上限 100，
> 夹紧后的值回显）。列表顺序沿用守护进程的返回顺序（**创建时间降序**）——它是稳定的，
> 额外重排只会让「翻页时看到的东西变了」更难解释。
>
> **`search` 按仓库名 / 标签模糊匹配**（不区分大小写）：在本地过滤，不走 Docker 的 `filters` 参数，
> 因为那是精确匹配且需要按字段拼 JSON；镜像多了之后它只作用于已拉全量的内存切片，成本可忽略。

#### 构建与推送的请求 / 响应形态（0.4.0 已实现）

```jsonc
// POST /api/v1/hosts/local/images/build
// Content-Type: multipart/form-data
//
// 文件字段：context —— 构建上下文 tar 包
//   ⚠️ 必须带 filename（如 curl -F "context=@context.tar"）：
//      ASP.NET Core 靠 Content-Disposition 里的 filename 判断这一段是「文件」还是「普通表单字段」。
//      不带 filename 时 tar 会被当成普通字段，而服务端报「context 字段是必填的」。
//
// 表单字段：
//   tag        —— 构建产物的镜像引用（必填），如 myapp:v1
//   dockerfile —— tar 包内的 Dockerfile 路径；省略为 Dockerfile
//   buildArgs  —— 构建参数（ARG），以 JSON 对象的**字符串**传入：{"VERSION":"1.0"}
//   labels     —— 镜像标签（LABEL），同上
//   target     —— 多阶段构建的目标阶段（--target）
//   platform   —— 目标平台，如 linux/amd64
//   noCache    —— 是否不使用构建缓存
//
// Response.data
{
  "imageId": "sha256:3f2c…",
  "reference": "myapp:v1",
  "size": 187654321,
  "repoTags": ["myapp:v1"],
  "buildLog": ["Step 1/2 : FROM scratch", "Step 2/2 : COPY payload.txt /payload.txt", …]
}
```

> **构建上下文只收已打包的 tar**：本版<b>不</b>接受多文件上传后由服务端打包——
> 那要在服务端实现一套 tar 写入器并配套路径穿越防护（tar 条目名可含 `../`），
> 而收益仅是界面少一步。用户 `tar -cf context.tar .` 一次比界面上的文件选择器更快。
>
> **构建失败只能从进度流里读出来**：与拉取同理，Docker 构建失败时 HTTP 层仍是 200，
> 错误只在进度消息里。因此服务端必须检查进度流——不检查就会把「Dockerfile 语法错」
> 报成「构建成功但什么也没产出」。
> 但**请求阶段就失败**的情况（Dockerfile 不存在、语法错到无法解析、基础镜像拉不动）
> 由 SDK 抛异常，此时翻译成 `1806`/500 而不是 `1402`——
> 「构建失败」比「Docker API 调用失败」有用得多，后者会让人怀疑后端挂了。
>
> **构建日志回带在响应里**（`buildLog`，最多 500 行，只保留最后 500 行）：
> 构建失败时 Docker 只给一行 `returned a non-zero code: 1`，真正的原因在上面几百行里。
> 日志进响应而不是只进服务端日志，是因为排错的现场在用户手里。
>
> **`buildArgs` / `labels` 用 JSON 字符串传**而不是「同名字段重复出现」：
> 后者还要处理同名多值到底取哪个。

```jsonc
// POST /api/v1/hosts/local/images/push
// Request
{ "image": "harbor.local:5000/myapp:v1", "repositoryId": null, "platform": null }
// Response.data
{
  "imageId": "sha256:3f2c…",
  "reference": "harbor.local:5000/myapp:v1",
  "repositoryServerAddress": "harbor.local:5000",   // 实际推到了哪里
  "authenticated": false,                          // 本次推送是否带了凭据
  "pushLog": ["…"]
}
```

> **推镜像必须带标签**：悬挂镜像（`<none>:<none>`）没有可推的名字，
> 因此提前报 `1000`/400，而不是让守护进程回一句看不懂的 `refusing to push unqualified name`。
>
> **仓库凭据怎么选**：显式给了 `repositoryId` 就用它（配置不存在报 `1800`/404）；
> 否则从镜像引用里切出地址段（`harbor.local:5000/app:v1` → `harbor.local:5000`）自动匹配。
> 两者都找不到就<b>匿名推</b>——公开仓库本来就不需要凭据，而对着私有仓库匿名推，
> 报错会由守护进程给出（`1807`/500）。
>
> **地址段的判据与 Docker 一致**：只有「第一段含 <code>.</code> 或 <code>:</code>，
> 或整段等于 <code>localhost</code>」才算 仓库前缀。
> 否则官方镜像的命名空间（`org/app`）会被误当成 仓库主机——
> 推镜像会因此去连一个不存在的主机。
>
> **`authenticated` 让人能分辨**「用的是我配的凭据」与「这仓库压根不需要凭据」。
> 前者说明凭据配置生效了，后者说明可能配错地址了。
>
> **推送不影响本地镜像**：推成功后本地标签仍在。

---

### Volume 管理（0.2.0 / 0.7.0 / 0.14.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/hosts/{hostId}/volumes/list` | Body: `{ pageIndex?, pageSize? }`；分页在服务端**内存**完成（Docker 无分页参数），`pageSize` 默认 20 / 上限 100 |
| 创建 | `POST /api/v1/hosts/{hostId}/volumes/create` | Body: `{ name, driver?, driverOptions?, labels? }` |
| 删除 | `POST /api/v1/hosts/{hostId}/volumes/remove` | Body: `{ name, force? }` |
| **批量删除** | `POST /api/v1/hosts/{hostId}/volumes/batch-remove` | Body: `{ names: [...], force? }` |
| **清理** | `POST /api/v1/hosts/{hostId}/volumes/prune` | Body: `{ dryRun? }`；返回**将删除的清单**与回收量。`dryRun=true` 时只列不删 |
| 获取详情 | `POST /api/v1/hosts/{hostId}/volumes/inspect` | Body: `{ name }` |

> **列表分页与容器 / 镜像同一套口径**：响应 `{ items, total, pageIndex, pageSize, warnings }`，
> 其中 `pageIndex` / `pageSize` 是夹紧后的**实际**取值（`pageIndex` 负数按 0、`pageSize` 上限 100）。
> 批量删除本身不分页。
> 批量删除与容器 / 镜像同口径：**部分成功也返回 200**，`failures` 逐条带 ID 与错误码。
>
> **占用（`size`）与引用数（`refCount`）来自 `GET /system/df`，不是列表接口**（实测）：
> `GET /volumes` 返回的每一项里**没有** `UsageData`，照它取值永远是 0。
> 服务端另发一次 `GET /system/df?type=volume` 并按卷名合并（`type=volume` 不能省——
> 否则守护进程会顺带统计镜像层，慢好几秒）。
> 两个字段是**可空**的：`null` 表示「守护进程没给用量」（老版本守护进程、第三方驱动、
> 或用量查询失败），界面显示「—」；`0` 是确切答案（空卷 / 没人引用），如实显示。
>
> **`force` 不是「强制删除」**（实测，勿照字面理解）：
> 只要还有容器挂着这个卷，守护进程就拒绝删除，**带不带 `force` 都一样**（`1404`/409），
> 容器在运行或已停止都一样，必须先删掉容器。`force` 唯一的作用是让「卷不存在」变成成功
> （幂等），便于批量重试。因此本字段的语义是「忽略不存在」而非「强行删掉」。

```jsonc
// POST /api/v1/hosts/{hostId}/volumes/list —— Response.data
{
  "items": [
    {
      "name": "nginx-html",              // 按名字标识，没有 ID
      "driver": "local",
      "mountpoint": "/var/lib/docker/volumes/nginx-html/_data",
      "scope": "local",
      "created": "2026-09-20T06:30:00Z", // 守护进程未提供时为 null
      "size": 3221225472,                 // ⚠️ null = 守护进程没给用量；0 = 确实是空的
      "refCount": 1,                      // ⚠️ 同上：null ≠ 0
      "labels": { "app": "nginx" }
    }
  ],
  "total": 1,                             // 分页前总条数（界面不要用 items.length 顶替）
  "pageIndex": 0,                         // 服务端实际使用的页码（夹紧后）
  "pageSize": 20,                         // 服务端实际使用的每页条数（夹紧后）
  "warnings": []                          // 恒为数组：守护进程会用它说「某卷驱动没装」
}
```

---

### Network 管理（0.2.0 / 0.3.0 / 0.7.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/hosts/{hostId}/networks/list` | Body: `{ pageIndex?, pageSize? }`；分页在服务端**内存**完成，`pageSize` 默认 20 / 上限 100 |
| 创建 | `POST /api/v1/hosts/{hostId}/networks/create` | Body: `{ name, driver?, subnet?, gateway?, ipRange?, attachable?, labels? }` |
| 删除 | `POST /api/v1/hosts/{hostId}/networks/remove` | Body: `{ networkId }` |
| **接入网络** | `POST /api/v1/hosts/{hostId}/networks/connect` | Body: `{ containerId, networkId }`；容器已接入时**幂等返回成功** |
| **断开网络** | `POST /api/v1/hosts/{hostId}/networks/disconnect` | Body: `{ containerId, networkId, force? }`；容器未接入时**幂等返回成功** |
| 获取详情 | `POST /api/v1/hosts/{hostId}/networks/inspect` | Body: `{ networkId }` |

> **列表分页**（与卷同口径）：响应 `{ items, total, pageIndex, pageSize }`。
> **没有 `force`**：Docker 的删除网络接口不提供该参数，
> 有容器接入必然 `1404`/409，内置网络同样。内置网络为 `bridge` / `host` / `none`
> （Docker 26 之前叫 `null`），服务端提前拦下以给出可翻译的错误码分支。
>
> **网络 ID 不带 `sha256:` 前缀**（镜像 ID 才有）：`/networks` 返回裸的 64 位十六进制。
> 照搬镜像的剥前缀逻辑会得到一个错 ID，表现为 404。
>
> **列表项**刻意没有「容器数」：Docker 的 `GET /networks`（列表）**不返回** `Containers`，
> 只有 `GET /networks/{id}`（详情）返回。补一个恒为 0 的字段会让界面把正在用的网络
> 显示成「0 个容器」，因此宁可不给。真实的接入情况看详情的 `containers`。
>
> **创建失败有两类**：字段格式不对 → `1000`/400（在本层挡下，否则会落成 `1402`/500，
> 让「自己填的网段打错一个字符」显示成服务端故障）；同名或子网重叠 → `1404`/409，
> 且**消息里保留守护进程给的原因文本**，据此能区分该改名字还是改子网。

```jsonc
// POST /api/v1/hosts/{hostId}/networks/list —— Response.data
{
  "items": [
    {
      "id": "741c9b41…",                 // 裸 64 位十六进制，无 sha256: 前缀
      "name": "bridge",
      "driver": "bridge",
      "scope": "local",
      "attachable": false,
      "internal": false,
      "subnets": ["172.17.0.0/16"],      // 自定义驱动（macvlan）可能为空数组
      "gateways": ["172.17.0.1"],
      "labels": {}
   ,
  "total": 1,                             // 分页前总条数
  "pageIndex": 0,
  "pageSize": 20 }
  ]
}

// POST /api/v1/hosts/{hostId}/networks/inspect —— Response.data（节选）
{
  "name": "app-internal",
  "enableIPv4": true,
  "enableIPv6": false,
  "ipam": [{ "subnet": "172.20.0.0/16", "gateway": "172.20.0.1", "ipRange": null }],
  "containers": [                       // 只有详情才有——判断「谁在用」的唯一权威数据
    {
      "id": "e1f2a3b4…",
      "name": "order-api",
      "macAddress": "02:42:ac:14:00:02",
      "ipv4Address": "172.20.0.2/16",    // 含前缀长度
      "ipv6Address": null
    }
  ]
}
```

> **创建 IPv6 网络时自动打开 `enableIPv6`**：填了 IPv6 网段却不勾选，
> 守护进程会建出一个「有 IPv6 地址池却不通」的网，而界面上看不出任何异常。

---

### Docker Compose / Stack（0.10.0 / 0.15.0）

| 操作 | 路径 | 说明 |
|------|------|------|
| 获取列表 | `POST /api/v1/hosts/{hostId}/stacks/list` | 无 Body |
| 部署 | `POST /api/v1/hosts/{hostId}/stacks/deploy` | Body: `{ name, composeContent }`（YAML 文本） |
| 更新 | `POST /api/v1/hosts/{hostId}/stacks/update` | Body: `{ name, composeContent }` |
| 停止 | `POST /api/v1/hosts/{hostId}/stacks/stop` | Body: `{ name }` |
| 删除 | `POST /api/v1/hosts/{hostId}/stacks/remove` | Body: `{ name }` |

---

### 仓库管理（0.4.0）

> **路径为 `/api/v1/admin/repositories/*`**（全局作用域，与主机无关）。
> 鉴权：读用 `repository.read`，写用 `repository.write`。
> ⚠️ 旧的草案写法（`/api/v1/registry/list`）**未采用**——它与本项目「管理面端点一律在
> `/api/v1/admin/` 下」的既有约定不一致（见 `admin/roles`、`admin/hosts`）。

| 操作 | 路径 | 阶段 | 说明 |
|------|------|------|------|
| 获取列表 | `POST /api/v1/admin/repositories/list` | 0.4.0 | Body: `{ search? }`（按别名 / 地址模糊过滤）；**响应不含任何口令信息** |
| 新增 | `POST /api/v1/admin/repositories/add` | 0.4.0 | Body: `{ name, serverAddress, username?, password?, description? }`；口令加密后落库 |
| 删除 | `POST /api/v1/admin/repositories/remove` | 0.4.0 | Body: `{ id }`；软删除，同名 / 同址可再次使用 |
| 测试连通性 | `POST /api/v1/admin/repositories/test` | 0.4.0 | Body: `{ id? , serverAddress?, username?, password? }`；给了 `id` 则用已保存的凭据（自动解密） |

```jsonc
// POST /api/v1/admin/repositories/list
// Response.data
[
  {
    "id": "3f2c…",
    "name": "内网 Harbor",
    "serverAddress": "harbor.local:5000",   // 规范化后：去协议、去路径、转小写
    "username": "deploy-bot",
    "hasCredentials": true,                 // 只有「有没有」，没有「是什么」
    "description": "CI 用",
    "createdAt": "2026-01-02T03:04:05+00:00",
    "lastModifiedAt": null
  }
]
```

> **响应里绝不含口令，也不含密文**：连密文都不能给——抓到一次响应就等于拿到一把通用钥匙。
> 界面要改口令时<b>重新输入</b>，不做「留空表示不修改」那套（那要求服务端能保留旧值，
> 同样得把旧值读出来）。
>
> **口令在库里是密文**：AES-GCM（随机 nonce，每次加密结果不同），密钥在数据卷下的
> `repository.key`（0600），与 JWT 密钥分开存——「泄漏 JWT 密钥」不等于「能解出口令」。
> 旧部署的 `registry.key` 是它的历史名，程序仍能读，但新部署不再写它。
> ⚠️ 恢复备份时**必须一并带上 `repository.key`**（旧部署是 `registry.key`），
> 否则已保存的口令全部无法解密（表现为「认证失败」，但根因是密钥没了）。
>
> **地址规范化**：`https://Harbor.Local:5000/v2/` → `harbor.local:5000`。
> 同一地址的各种写法必须收敛到<b>同一个</b>值——它同时用于存库、按地址查配置、
> 拼镜像引用与传给 Docker 的 <c>AuthConfig.ServerAddress</c>，
> 四处不一致会让守护进程拿 A 处的地址去请求 B 处的仓库（报出来的是权限错误而不是地址错误）。
>
> **别名与地址各占一个部分唯一索引**（`WHERE is_deleted = 0`）：
> 同名会让管理界面出现两条分不清的记录；<b>同址更危险</b>——它会让「推镜像时用哪条凭据」
> 变成随机行为。
>
> **`test` 用 `repository.write` 而不是 `repository.read`**：测试传的是<b>凭据</b>，
> 能用凭据意味着能读出它。把这道门开给只读角色等于让对方把口令送到任意地址上去
> （把地址改成自己的服务器就能截走）。
>
> **不可达与认证被拒是不同错误码**：`1805`/502（该查网络与防火墙）与 `1804`/400（该重填口令）。
> 归成同一个码的话，用户只能靠猜。

### 资源权限管理（0.1.0 角色与作用域分配 / 0.12.0 容器粒度）

授权模型是**带作用域的 User → Role → Permission**：权限是**静态封闭目录**（`domain.level`，不建表），
角色**携带一组权限**（`role_permissions`），而**分配**（`user_role_assignments`）决定「谁在哪个范围内拥有这个角色」。
因此同一用户可以在 A 主机是 `operator`、在 B 主机只读。

**三组端点**分别回答三个不同的问题：

| 分组 | 路径 | 权限 | 说明 |
|------|------|------|------|
| 角色 CRUD | `POST /api/v1/admin/roles/{list,permissions,create,update,set-permissions,set-default,remove}` | `role.read`（只读） / `role.write`（改状态） | 角色定义与权限矩阵 |
| 主机视角 | `POST /api/v1/admin/hosts/{hostId}/roles/{list,assign,revoke}` | `role.read` / `role.write` | **谁能访问这台机器** |
| 用户视角 | `POST /api/v1/admin/users/{userId}/roles/{list,assign,revoke}` | `role.read` / `role.write` | **这个人拥有什么**（含全局分配） |

**请求 / 响应形态**：

```jsonc
// POST /api/v1/admin/roles/list
// Request
{ "customOnly": false }          // 只返回自定义角色（排除内置角色）时置 true
// Response.data —— 数组，每项：
{
  "id": "…uuid…",
  "name": "operator",
  "displayName": "运维",
  "description": "可操作主机上的容器、镜像、数据卷与网络",
  "isBuiltIn": true,             // 内置角色的权限集不可修改
  "isDefault": false,
  "permissions": ["host.read", "container.read", "container.write", "…"],   // 按目录顺序
  "assignmentCount": 3           // 当前有效分配数；界面据此提示「仍在使用」
}

// POST /api/v1/admin/roles/permissions    —— 权限目录（权限矩阵的列定义）；无请求体
// Response.data —— 数组，每项：
{ "name": "container.write", "domain": "container", "level": "write", "scope": "host" }

// POST /api/v1/admin/roles/create
// Request
{ "name": "ops-zhang", "displayName": "张工", "description": "…", "permissions": ["host.read", "container.write"] }
// POST /api/v1/admin/roles/update          —— 只改可读信息，标识与权限不在此端点
// Request
{ "roleId": "…uuid…", "displayName": "张工", "description": "…" }        // 定位也可用 "role": "ops-zhang"
// POST /api/v1/admin/roles/set-permissions —— 覆盖式，允许空数组
// Request
{ "role": "ops-zhang", "permissions": ["host.read", "container.read"] }
// POST /api/v1/admin/roles/set-default
// Request
{ "role": "auditor" }
// POST /api/v1/admin/roles/remove
// Request
{ "role": "ops-zhang" }
// Response.data —— 前四者返回单个角色（形状同 `list` 的每一项）；`remove` 为无数据接口

// POST /api/v1/admin/hosts/{hostId}/roles/list    —— 本主机上的分配（**不含全局分配**）
// Request
{ "userId": "…uuid…" }           // 省略即列出该主机上的全部分配
// POST /api/v1/admin/users/{userId}/roles/list     —— 该用户的全部分配，无请求体
// Response.data —— 两者同形状的数组，每项：
{
  "id": "…uuid…",
  "userId": "…uuid…",
  "username": "ops-zhang",       // 冗余自用户表：界面要显示人名，不该自己再查一次
  "roleId": "…uuid…",
  "role": "operator",
  "roleDisplayName": "运维",
  "hostId": "…uuid…",            // null = 全局分配
  "hostName": "prod-01",         // null = 全局分配（主机名才是对外标识）
  "lastModificationTime": null   // 从未变更过为 null
}

// POST /api/v1/admin/hosts/{hostId}/roles/assign   —— 作用域由**路径**决定
// Request
{ "userId": "…uuid…", "role": "operator" }         // roleId 亦可
// POST /api/v1/admin/users/{userId}/roles/assign    —— 作用域由**请求体**决定
// Request
{ "role": "operator", "host": "prod-01" }          // 省略 host 即**全局分配**
// Response.data —— 单条分配（形状同上）

// POST /api/v1/admin/hosts/{hostId}/roles/revoke   —— 作用域由**路径**决定
// Request
{ "userId": "…uuid…", "role": "operator" }
// POST /api/v1/admin/users/{userId}/roles/revoke    —— 省略 host 即撤销全局分配
// Request
{ "role": "operator", "host": "prod-01" }
// Response：无数据接口（不含 data 字段）
```

> **主机视角的 `{hostId}` 是主机名**（与 `admin/hosts/{hostId}/update` 一致）；用户视角的 `{userId}` 是
> **用户主键（GUID）**——主机名是对外标识，用户没有「名字进 URL」的诉求。分配表里的 `host_id` 是
> `hosts.id`（Guid），转换在服务端完成，前端不必为了调分配接口先查一遍主机的 Guid。
>
> **主机分配只带来该角色的 Host 域权限，其 Global 权限被忽略**：否则「把 `admin` 分配到某台主机」
> 就会变成一次静默的系统级提权。**全局分配**（`host_id IS NULL`）才贡献该角色的 Global 权限。
> 默认角色（`isDefault`）在创建用户时用于建立一条**全局分配**。
>
> **为什么分成两个视角而不是一个带可空 `hostId` 的端点**：它们回答的是两个不同问题——
> 「谁能访问这台机器」与「这个人拥有什么」。而且只有用户视角能表达「在**所有**主机上都怎样」：
> 把作用域做成请求体里一个可空字段会让同一个端点有两种语义，而「省略」与「填空」的区别在 JSON 里极易出错。
>
> **主机视角的 `list` 不含全局分配**：它对所有主机生效，无法归属到某一台。界面要表达「这些人因为
> 全局分配也能访问这台机器」需要另查一次用户视角的列表——刻意不做隐式合并，因为那会让管理员无法区分
> 「专门给这台机器开的人」与「本来就什么都能看到的人」，而两者的运维含义完全不同。
>
> **分配是幂等 upsert**：同一 `(用户, 角色, 作用域)` 组合重复提交返回**同一条**记录，不换主键——
> 否则界面刷新后会看到一条 id 不同的「新」记录。撤销走**软删除**（保留「谁在什么时候撤的」这条事实），
> 因此同一组合可以重新分配；撤销一条并不存在的分配 → `1515`/404。
>
> **内置角色受保护**：`set-permissions` 与 `remove` 对内置角色（`isBuiltIn`）返回 `1513`/400。
> 一个能被清空权限集的 `operator` 已经不是它的名字所声称的那个角色；更现实的风险是管理员把 `admin`
> 改窄后把自己锁在门外，只能改库恢复。
>
> **删除仍在使用的角色 → `1512`/400，不级联撤销**：级联会悄悄收回没人要求收回的访问权。
> 报这个错是把决定权交还管理员——先看谁在用，再逐个撤销，最后才删角色。只数**有效**分配，
> 已撤销的行不该让角色永远删不掉。
>
> **未知权限名一律 `1514`/400，且不落库**：校验发生在写库之前，非法输入不允许留下「待修正」的角色。
> 校验用权限目录白名单而非解析字符串——目录外的标识（`"read"`、`"ALL"`、`""7""`）一律拒绝，
> 而猜错的方向永远是「权限更多」。
>
> ⚠️ **「未知名」与「空白项」是两个码**：数组里出现空串或纯空白项报 `1000`（参数本身不合法），
> 只有**非空**却不在目录里的名字才报 `1514`。界面据此提示「请从权限目录里选」而非「参数错误」。
> （`1514` 曾一度在实现里被写成 `1000`，而它在 `ErrorCodes`、`ErrorCodeCatalog`、前端 `ErrorCode` 表
> 与本文件四处都登记齐了——漏的是**抛出点**那一处，它不在任何同步清单上。）
>
> **`set-permissions` 是覆盖而不是叠加**，允许空数组（「收回这个角色的全部权限」是合法操作）。
> 实体只有 `SetPermissions`（覆盖式）而没有 `AddPermission`，就是为了让这个决定只有一处。
>
> **权限目录刻意做成独立端点**：它是**编译期事实**，同一页面的每次刷新都拿到同一份，
> 让客户端缓存它比每次拉角色列表时顺带返回更省。


---

## SignalR Hub 规范（0.5.0）

### Hub 路径

| Hub | 路径 | 说明 |
|-----|------|------|
| `ContainerLogHub` | `/hubs/hosts/{hostId}/container-log` | 实时日志流（主机作用域） |
| `ContainerStatsHub` | `/hubs/hosts/{hostId}/container-stats` | 实时监控数据（主机作用域） |
| `NotificationHub` | `/hubs/notification` | 系统事件通知（**全局**，不含 `hostId`） |

**鉴权**：JWT 通过 `?access_token=...` 查询参数传递（WebSocket 握手不支持自定义 Header）。

> ⚠️ **`access_token` 只在 `/hubs` 前缀的路径上被接受**。其它路径带了它会被**忽略**
> （而不是被当成有效凭证）——否则任何带这个查询串的普通接口都会被「顺带」鉴权，
> 让 CORS / 缓存 / 访问日志的行为都变得不可预测。
> 服务端会在收到该查询参数时把它改写成 `Authorization: Bearer` 头（仅当请求尚未带该头）。

> ⚠️ **SignalR 的 `MapHub` 不支持路由参数**：路径里的 `{hostId}` 只参与**路径匹配**，
> **不会**出现在 `HttpContext.Request.RouteValues` 里（实测该字典的键集合为空）。
> 因此 Hub 方法里拿不到主机标识，服务端改为**从请求路径解析**
> （见 `RealtimeHostResolver.ResolveHostId`）。
> 换句话说：**不要在 Hub 里依赖路由值**。

### 通用约定

**订阅 / 退订的返回值**：所有订阅方法返回同一个形状

```jsonc
{ "success": true, "code": null, "message": null }
```

失败时 `success` 为 `false`，`code` 是业务错误码、`message` 是可展示的说明。
**不抛 `HubException`**——`HubException` 只能带一个字符串，客户端拿不到错误码，
而前端要靠 `1502`（缺权限）与 `1401`（容器不存在）给出不同提示。

**流错误事件**：任何 Hub 都可能推送

| 事件 | 数据 | 说明 |
|------|------|------|
| `OnStreamError` | `{ code, message }` | 订阅建立后流本身出错（主机不可达、容器被删…） |

### ContainerLogHub

**客户端 → 服务端**

| 方法 | 参数 | 说明 |
|------|------|------|
| `SubscribeLogs` | `containerId: string`, `startSeq?: number = 1`, `since?: long? = null` | 开始订阅指定容器日志 |
| `UnsubscribeLogs` | `containerId: string` | 取消订阅 |

- `startSeq`：流内序号的起点。客户端拿了 N 行历史后传 `N+1`，服务端接着编。
- `since`：Unix 秒。只推该时刻之后的行；**省略时只推新产生的行**（服务端用 `tail=0`），
  不会把历史重推一遍。

**服务端 → 客户端**

| 事件 | 数据 | 说明 |
|------|------|------|
| `OnLog` | `{ containerId, lines: [{ seq, timestamp, line }] }` | 推送**一批**日志行 |

> **为什么是批量而不是逐行**：逐行推送在高频日志下会把连接变成「每秒几百帧」，
> 而浏览器端的渲染成本与帧数成正比。攒批把帧数压到「行数 / 批量」量级
> （默认 100 行或 200ms 触发，见 `RealtimeOptions`）。
>
> **序号缺口即「丢过行」**：服务端频道满了会丢最旧的行（而不是阻塞推送），
> 丢掉的行**已经占用了序号**，因此下一批的首行序号会跳号。
> 客户端据此提示「已丢弃 N 行」——不静默丢，也不假装没发生。
>
> `seq` 是**流内序号**，与 REST 接口里的**展示序号**不是一回事。

### ContainerStatsHub

**客户端 → 服务端**

| 方法 | 参数 | 说明 |
|------|------|------|
| `SubscribeStats` | `containerId: string` | 开始订阅 |
| `UnsubscribeStats` | `containerId: string` | 取消订阅 |

**服务端 → 客户端**

| 事件 | 数据 | 说明 |
|------|------|------|
| `OnStats` | `{ containerId, stats: { containerId, cpuPercent, memoryUsage, memoryLimit, networkRxBytes, networkTxBytes, timestamp } }` | 推送一次统计快照 |

> CPU 百分比由**服务端**算好（相邻两帧相减），因此所有客户端口径一致。
> 采样间隔默认 1 秒（`RealtimeOptions.StatsSampleIntervalMs`）。

### NotificationHub

**服务端 → 客户端**（主动推送，无需订阅方法）

| 事件 | 数据 | 说明 |
|------|------|------|
| `OnContainerDied` | `{ hostId, containerId, containerName, exitCode, outOfMemory, occurredAt }` | 容器异常退出通知 |

> **可见性由服务端保证**：推送前逐个连接判「该用户对这台主机有没有 `host.read`」，
> 无权的一律不推。前端**不需要**自己按主机过滤。
>
> **「异常退出」只覆盖 `die` 与 `oom`**：容器**正常停止**（`stop`）不是异常，
> 不会推通知——那种情况由页面自己的操作触发刷新。
> `outOfMemory` 为 `true` 时 `exitCode` 通常是 `137`。

### Image Pull / Build 进度频道（0.5.0）

进度是**按请求**的（一个令牌对应一次操作），因此放在全局 Hub 上最自然。

**客户端 → 服务端**

| 方法 | 参数 | 说明 |
|------|------|------|
| `SubscribePull` | `token: string` | 订阅某次镜像操作的进度 |
| `UnsubscribePull` | `token: string` | 取消订阅 |

**令牌由客户端生成**：服务端没法凭空知道「这次拉取对应哪条连接」——
拉取请求是一个独立的 HTTP 请求，与 Hub 连接没有天然关联。
客户端先生成令牌，订阅它，再把它带进拉取 / 构建请求（`progressToken`），三者由此串起来。

- 令牌长度 **≥ 32** 个字符，字符集限 `[A-Za-z0-9-]`（它会出现在服务端日志与异常消息里）。
- 拉取：`POST /api/v1/hosts/{hostId}/images/pull` 的 body 加 `progressToken`。
- 构建：`POST /api/v1/hosts/{hostId}/images/build` 的 multipart 表单加 `progressToken` 字段。

**服务端 → 客户端**

| 事件 | 数据 | 说明 |
|------|------|------|
| `OnPullProgress` | `{ token, operation, percent, status, done, error }` | 推进度 |

> `percent` **可能为 `null`**——算不出总体进度时**不能给 0**（那会被读成「没进度」）。
> `done` 为 `true` 表示操作已成功结束；失败时 `error` 给出原因。

### 订阅上限

单个连接每类订阅默认最多 **5** 个（`RealtimeOptions.MaxSubscriptionsPerKind`），
超出时 `SubscribeXxx` 返回 `success: false`。

> 为什么要上限：每条日志订阅在守护进程侧都是**一条长连接**。
> 一个页面开几十条会拖垮守护进程，也会让服务端的推送任务数失控。

---

## WebSocket 终端（0.8.0）

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
- ❌ `POST /api/v1/system/prune`（一次清四类）——**资源级 prune 已在 0.7.0 交付**，不做的是「一键全清」
- ❌ **宿主机级实时指标**（CPU / 内存 / 网络 / 负载）——Docker Engine API 无此端点，需目标主机上的 Agent（1.0.0 之后）
- ❌ OAuth / OIDC 登录相关 API
- ❌ 应用模板相关 API、GitOps 从 Git 同步 Stack 的 API
- ❌ 计划任务 / cron 相关 API
- ❌ MCP 的 stdio 传输与独立部署形态（只做同进程 `/mcp`）
