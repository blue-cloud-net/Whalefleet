import { request } from '@/api'
import type {
  ComposeVariablesResponse,
  EnvVariableDto,
  SaveEnvVariableRequest
} from '@/types/api'

/**
 * 环境变量端点（0.15.0）。
 *
 * ## 两套路径，两类作用域
 * - **全局变量**：`/api/v1/admin/variables/*`（全局作用域，路径不含 `hostId`）。
 *   权限 `variable.read` / `variable.write`，只种给 admin。
 * - **项目级变量**：`/api/v1/hosts/{hostId}/composes/variables/*`（主机作用域）。
 *   权限 `compose.read` / `compose.write`。
 *
 * 全局变量刻意**不挂在主机下**：它对所有主机生效，挂在某台主机下会让同一个变量
 * 在 N 台主机上重复登记 N 遍，而 N 份副本迟早会漂移（改了其中一份，其它主机
 * 开始认不上）。
 *
 * ## 值永不返回
 * 响应里只有 `hasValue` 与 `valueMasked`。这不是「后端忘了返回」——
 * 变量里常存口令，而响应会进浏览器缓存、DevTools 与访问日志。
 * **也不要试着在前端把遮罩串「还原」**：那个串是后端给的固定长度圆点。
 *
 * ## 失败错误码（界面要区分，不要统一显示「保存失败」）
 * - `1909` 变量名非法（字符集 `[A-Za-z_][A-Za-z0-9_]*`，≤ 64）
 * - `1913` 变量不存在（被别人抢先删掉了）
 * - `1914` 变量名已被占用（**同一作用域**内唯一）
 * - `1908` YAML 引用了未定义的变量（部署时才会出现，见 ComposeVariablesResponse.undefined）
 */

/** 列出全局变量。 */
export function listGlobalVariables(): Promise<EnvVariableDto[]> {
  return request<EnvVariableDto[]>({
    url: '/api/v1/admin/variables/list',
    method: 'post'
  })
}

/**
 * 新建全局变量。
 *
 * `webhookOverridable` 传 `true` 时后端会**再判一次** `isSecret`：
 * 密钥恒不可覆盖，这是不可变量，不靠调用方自觉。
 */
export function createGlobalVariable(payload: SaveEnvVariableRequest): Promise<EnvVariableDto> {
  return request<EnvVariableDto>({
    url: '/api/v1/admin/variables/create',
    method: 'post',
    data: payload
  })
}

/** 修改全局变量（`value` 省略表示不改值；清空值要显式传 `clearValue`）。 */
export function updateGlobalVariable(
  name: string,
  payload: Omit<SaveEnvVariableRequest, 'name'> & { clearValue?: boolean }
): Promise<EnvVariableDto> {
  return request<EnvVariableDto>({
    url: '/api/v1/admin/variables/update',
    method: 'post',
    // 名字只来自第一个参数：让 payload 里也带一个 name 会静默覆盖它，
    // 而「改错了变量」是必须避免的事故。
    data: { ...payload, name }
  })
}

/** 删除全局变量。 */
export function removeGlobalVariable(name: string): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/variables/remove',
    method: 'post',
    data: { name }
  })
}

/**
 * 列出某个 Compose 项目可见的变量（项目自己的 + 从全局继承的 + 未定义的）。
 *
 * 三个清单一起返回而不是分成三个请求：它们回答的是**同一个问题**
 * （「这个项目部署时会拿到什么」），拆开就会出现「项目变量是新的、继承的是旧的」
 * 这种中间状态。
 */
export function listComposeVariables(
  hostId: string,
  composeName: string
): Promise<ComposeVariablesResponse> {
  return request<ComposeVariablesResponse>({
    url: `/api/v1/hosts/${hostId}/composes/variables/list`,
    method: 'post',
    data: { composeName }
  })
}

/** 新建项目级变量。 */
export function createComposeVariable(
  hostId: string,
  composeName: string,
  payload: SaveEnvVariableRequest
): Promise<EnvVariableDto> {
  return request<EnvVariableDto>({
    url: `/api/v1/hosts/${hostId}/composes/variables/create`,
    method: 'post',
    data: { composeName, ...payload }
  })
}

/** 修改项目级变量。 */
export function updateComposeVariable(
  hostId: string,
  composeName: string,
  name: string,
  payload: Omit<SaveEnvVariableRequest, 'name'> & { clearValue?: boolean }
): Promise<EnvVariableDto> {
  return request<EnvVariableDto>({
    url: `/api/v1/hosts/${hostId}/composes/variables/update`,
    method: 'post',
    data: { ...payload, composeName, name }
  })
}

/** 删除项目级变量。 */
export function removeComposeVariable(
  hostId: string,
  composeName: string,
  name: string
): Promise<void> {
  return request<void>({
    url: `/api/v1/hosts/${hostId}/composes/variables/remove`,
    method: 'post',
    data: { composeName, name }
  })
}
