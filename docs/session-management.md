# 会话管理（0.13.0）

> 本文是**会话与账号状态**的事实源：一个登录态如何被标识、如何被校验、如何被吊销，
> 以及它与团队权限上限的关系。后续调整先改本文，再改实现与测试。

## 1. 目标与非目标

**目标**

1. 会话可枚举：管理员能看到某用户当前所有有效会话（创建时间、过期时间、IP、UA、指纹）。
2. 会话可单条吊销，也可全部吊销；吊销后该会话的 access token **下一次请求**即失效。
3. 账号停用后，该用户已签发的所有会话立即失效（跨实例最坏 60 秒收敛）。
4. 过期会话自动清理，不长期堆积。
5. 保持既有安全语义：refresh 只存哈希、轮换、重放检测、登录限流、失败响应不可区分。
6. 热点路径不反复查库：每请求的会话校验走内存缓存。

**非目标**

- 不做「同一账号只允许一个会话」。
- 不做最后活跃时间、地理定位、设备名称识别。
- 不做多实例共享缓存（单实例自托管；多实例靠 TTL 收敛）。
- 不改变 JWT 签名算法与 access / refresh 的默认有效期。
- 不让 terminal token 跟随会话即时失效（它是独立的 5 分钟短时令牌）。

## 2. 核心模型

- **一个会话 = `refresh_tokens` 的一行**。不新建 `sessions` 表。
- 该行的 `id`（UUID）既是**会话 id**，也是当前 refresh token 的 id。
- access token 增加 `sid` 声明，值就是该会话 id。
- **轮换不插新行**：同一行更新 `token_hash` 与 `expires_at`，旧哈希写入
  `previous_token_hash`，`rotated_at` 记录轮换时刻，`id` 保持不变。
  因此「同一台设备的会话」在列表里始终是一条，access token 在轮换前后指向同一会话。
- 吊销会话 = 写 `revoked_at`；过期会话 = `expires_at <= now`。

> ⚠️ 轮换 SQL 写成 `SET previous_token_hash = token_hash, token_hash = @New, …`
> 依赖 SQLite **用旧行的值计算右值**。把它改成「先读后写」或换数据库时要重新确认。

## 3. 数据模型

迁移 `0013_accounts_teams_security.sql`：

| 表 / 列 | 说明 |
|---|---|
| `refresh_tokens.id` | 会话 id |
| `refresh_tokens.token_hash` | **当前** refresh token 的哈希（唯一） |
| `refresh_tokens.previous_token_hash` | 上一次轮换前的哈希，仅用于重放检测 |
| `refresh_tokens.expires_at` | 当前 refresh token 的过期时间 |
| `refresh_tokens.revoked_at` | 非空表示会话已吊销 |
| `refresh_tokens.rotated_at` | 最近一次轮换时间 |
| `refresh_tokens.device_fingerprint` | 最近一次上报的浏览器指纹（只展示） |
| `refresh_tokens.created_ip` / `user_agent` | 登录时的来源快照 |
| `users.is_disabled` | 停用标记（登录与每请求校验都读它） |
| `users.must_change_password` | 首登 / 重置后置位，改密后清除 |
| `users.password_changed_at` | 用于密码有效期判定 |

- 活跃会话：`revoked_at IS NULL AND expires_at > now`。
- 过期清理：`DELETE FROM refresh_tokens WHERE expires_at < now`。
- 旧模型遗留的 `replaced_by_token_hash` 保留但**不再写入**（避免重建表）。

## 4. 令牌契约

**access token**（HS256，默认 15 分钟）

| 声明 | 含义 |
|---|---|
| `sub` | 用户主键 |
| `unique_name` | 用户名 |
| `jti` | 本次 access token 的唯一标识 |
| `sid` | **会话 id**（`refresh_tokens.id`） |
| `role` | 角色名（可多个，仅供展示，不参与判定） |

**refresh token**：32 字节随机数 Base64url；库中只存 `base64url(SHA-256(原文))`；
原文只在登录 / 刷新响应中出现一次。

## 5. 生命周期

### 5.1 登录

1. 限流（IP + 用户名，5 次/分钟）。
2. 校验凭据：用户不存在也执行哑元哈希；失败统一 `1100`。
3. `is_disabled=1` → `1108`（**先于密码校验**，停用账号不泄露「密码对不对」）。
4. 创建会话行（`id`、哈希、过期、指纹、IP、UA），再签发带 `sid` 的 access token。
5. 判定 `mustChangePassword`：首登标记，或 `password_changed_at + 有效期 < now`。
6. 返回令牌对 + 用户摘要（含 `mustChangePassword` 与**完整全局有效权限**）+ 认证审计。

### 5.2 刷新（原地轮换）

1. 按 `token_hash` 查会话行。
2. 未命中时再按 `previous_token_hash` 查：命中即**重放**——撤销该用户全部会话、
   写审计、返回 `1102`。
3. 命中但已吊销 / 已过期 / 用户已删 → `1102`；用户已停用 → `1108`。
4. 同一行更新 `previous_token_hash`、`token_hash`、`expires_at`、`rotated_at`。
5. 签发新 access token（`sid` 不变），返回**完整权限**与 `mustChangePassword`。

> ⚠️ 轮换必须走 `IRefreshTokenRepository.RotateAsync`。
> 通用的 `UpdateAsync` 只写吊销字段，用它「轮换」等于没换——旧令牌仍然可用。

> **并发**：多标签页同时刷新时，第二个请求会命中 `previous_token_hash` 被判重放。
> 前端已有单飞（single-flight）去重；跨标签页仍可能竞争，这是既有模型行为。

### 5.3 登出

幂等吊销当前会话；access token 在下一次请求即被校验拒绝。

### 5.4 管理员侧的会话操作

| 操作 | 端点 | 语义 |
|---|---|---|
| 列表 | `POST /admin/users/{userId}/sessions/list` | 只列活跃会话 |
| 单条吊销 | `POST /admin/users/{userId}/sessions/revoke` | `{ sessionId }`，会话不存在也成功 |
| 全部吊销 | `POST /admin/users/{userId}/sessions/revoke-all` | 返回吊销条数 |
| 启用 / 停用 | `POST /admin/users/{userId}/set-enabled` | `{ enabled }`；停用会撤销全部会话 |

- **停用不恢复旧会话**：重新启用后用户必须重新登录。
- 不能停用自己（`1000`）。

### 5.5 改密 / 重置 / 删号

- 自助改密：校验策略 → 写 `password_changed_at` → 清 `must_change_password` →
  撤销该用户全部会话（含当前会话，前端需重新登录）。
- 管理员重置：置位 `must_change_password` → 撤销全部会话。
- 删号：软删除 + 撤销全部会话。

### 5.6 强制改密

`mustChangePassword` 期间服务端只放行：

- `POST /api/v1/auth/me`
- `POST /api/v1/auth/refresh`
- `POST /api/v1/auth/logout`
- `POST /api/v1/admin/users/update-password`

其余端点返回 `403` / `1205`。前端收到 `1205` 直接跳 `/change-password`，
不弹普通错误 toast；改密成功后清会话并回登录页。

## 6. 每请求校验

`AccountSessionMiddleware` 位于 `UseAuthentication()` 之后、`UseAuthorization()` 之前：
JWT 签名与有效期已由框架验完，它只判「令牌有效但会话/账号已失效」。

1. 未认证 → 跳过（匿名端点照常）。
2. 读 `sid`；缺失 → `401`/`1100`（0.x 无正式发布，旧令牌直接要求重登）。
3. 查 `ISessionStatusCache`（键 = `sid`，TTL 60 秒）；未命中才查库并回填。
4. 判定顺序：
   - 会话不存在 / 已吊销 → `401`/`1100`
   - 已过期 → `401`/`1100`，**移出缓存 + 投递清理队列**
   - 用户已停用 → `401`/`1108`
   - `mustChangePassword` 且路径不在白名单 → `403`/`1205`
5. **指纹只覆盖、不拒绝**：请求头 `X-Client-Fingerprint`（或 `?fingerprint=`）与记录
   不同就覆写。它是排查线索，不是第二因素。

**长连接**

- SignalR `/hubs`：握手走同一套校验；令牌来自 `?access_token=`，指纹来自 `?fingerprint=`。
  已建立的连接不因吊销立即断开（下一次权限复核按新状态处理）。
- `/ws` 终端：用独立 terminal token（不同 audience），在中间件眼里是**匿名**，
  必须显式跳过——否则「一开终端就 403」。
- SPA 回退必须排除 `/api`、`/healthz`、`/hubs`、`/ws`（见 AGENTS.md 陷阱 17）。

## 7. 缓存与清理

- `SessionStatusCache`（Singleton，`IMemoryCache`）：键 = `sid`，值 = 会话状态 +
  用户停用/改密标记；绝对值 60 秒。另维护 `userId → sids` 索引（TTL 3×60 秒）
  用于「按用户批量失效」。
- 主动失效：单条吊销删该键；全部吊销 / 停用 / 改密 / 删号删该用户的全部键。
- 过期清理：`ISessionCleanupQueue`（有界 Channel，满时 `DropWrite`）+
  `SessionCleanupWorker`（自建作用域、`CancellationToken.None` 之外的宿主停止令牌）；
  **每小时兜底**扫一遍 `expires_at < now`。
- 清理失败只记 Warning，不影响请求。

## 8. 与团队权限的关系

- 会话只回答「这个人还能不能用这个登录态」，**不存权限**。
- 权限仍由 `IPermissionChecker` / `IContainerPermissionChecker` 每请求解析。
- 团队上限在权限解析层生效：**有效权限 = 个人授权 ∩ 团队上限（多团队按作用域取并集）**；
  内置全局管理员豁免上限。加入团队**不授予**任何权限，要显式「同步到成员」。
- 刷新响应必须返回完整权限，否则静默刷新后侧边栏会少菜单。

## 9. 错误码

| 错误码 | HTTP | 含义 |
|---|---|---|
| `1100` | 401 | 未认证（无令牌 / 无效 / `sid` 缺失 / 会话不存在、已吊销、已过期） |
| `1101` | 401 | 令牌已过期（JwtBearer 产生） |
| `1102` | 401 | 刷新令牌无效（未知 / 吊销 / 过期 / 重放） |
| `1108` | 401 | 账号已停用 |
| `1205` | 403 | 必须先修改密码 |

## 10. 前端契约

- 请求拦截器注入 `Authorization` 与 `X-Client-Fingerprint`（`utils/fingerprint.ts`：
  `localStorage` 里的稳定 client-id + 浏览器特征，FNV-1a 摘要）。
- `401`：认证端点自身不重放；普通请求单飞刷新一次 → 成功重放，失败清会话并跳登录。
- `1108`：清会话 + 提示「账号已停用」。
- `1205`：跳 `/change-password`。
- 会话列表展示创建 / 过期时间、IP、UA、指纹**前 12 位**；界面文案不得暗示
  「指纹不匹配会被拒绝」。

## 11. 测试锚点

| 层 | 文件 |
|---|---|
| 单元 | `UserTests`（改密 / 停用）、`RefreshTokenTests`（轮换 / 指纹）、`JwtTokenServiceTests`（`sid`）、`SqliteRefreshTokenRepositoryTests` |
| 集成 | `AuthEndpointsTests`（登录写会话、刷新驻留同一行）、`AdminUsersEndpointsTests`（会话列表 / 吊销 / 启停 / 首登改密）、`ErrorHttpStatusTests`（1108/1205 映射） |
| 前端 | `tests/mock-contract.spec.ts`（团队 / 会话 / 策略 / 首登）、`tests/routes.spec.ts`（团队菜单与权限门）、`tests/i18n.spec.ts`（词条渲染） |

## 12. 已定稿取舍

1. **轮换后的旧 access token**：`sid` 不变，旧 token 仍可用到过期；不引入 `tid` 比对。
2. **过期清理**：校验命中即入队 + 每小时兜底。
3. **缓存 TTL 60 秒**：单实例主动失效即时生效；多实例最坏 60 秒收敛。
4. **指纹只记录不校验**：变化即覆盖。
5. **团队显式同步**：加成员不自动写个人授权。
6. **会话 = `refresh_tokens` 一行**：用 `id` 作 `sid`，不新建表。

## 13. 后续可选

1. 把当前 refresh token 哈希前缀写进 access token（`tid`），让旧 access token 立即失效。
2. 会话最后活跃时间（需节流写入）。
3. 会话备注 / 设备名称（前端解析 UA 后上报，仅展示）。
4. 多实例共享缓存与跨实例主动失效。
5. 自助会话管理（`/auth/sessions/*`），让非管理员也能看 / 踢自己的会话。
