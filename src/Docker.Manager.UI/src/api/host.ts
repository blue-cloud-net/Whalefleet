import { request } from '@/api'
import type {
  AdminHostSummary,
  CreateHostPayload,
  HostOverview,
  TestHostPayload,
  UpdateHostPayload
} from '@/types/api'

/**
 * 主机相关端点。
 *
 * ## 路径约定（见 docs/api-spec.md）
 *
 * - `POST /api/v1/hosts/list` —— **当前用户可见**的主机。总览页与侧栏 `HostSelector` 都用它，
 *   因此它对所有角色开放；admin 调用时返回全部主机。
 * - `POST /api/v1/admin/hosts/*` —— 主机管理页专用（admin），返回全部主机供管理操作。
 *
 * 两者刻意分开：管理端点要能改 / 删，普通列表端点只读且受可见性约束。
 *
 * 列表**内置 `status`**（每台一次轻量 ping），但**不含 `version` / `counts`** ——
 * 那两项需要完整调用 Docker API，若都放进列表，主机一多就会明显变慢甚至超时。
 * 它们由 `getHostOverview()` 单独取。
 */

/** 当前用户可见的主机列表（含连通状态）。 */
export function listHosts(): Promise<HostOverview[]> {
  return request<HostOverview[]>({ url: '/api/v1/hosts/list', method: 'post' })
}

/**
 * 单台主机的聚合信息：`version` 与 `counts`。
 *
 * 失败形态：`1300`/404（主机不存在）、`1302`/400（主机已停用）、
 * `1501`/403（无权限访问此主机）。
 */
export function getHostOverview(hostId: string): Promise<HostOverview> {
  return request<HostOverview>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/overview`,
    method: 'post'
  })
}

/**
 * 管理端点的主机列表（admin）。
 *
 * ⚠️ 返回形状与 `listHosts()` **不同**，别混用：
 * - 这里的 `id` 是**主键**（Guid），`listHosts()` 的 `id` 是**主机名**（路径里的 hostId）
 * - 这里用 `reachable`（可空布尔，`null` = 未探测），那里用 `status`（online/offline/unknown）
 */
export function listAdminHosts(): Promise<AdminHostSummary[]> {
  return request<AdminHostSummary[]>({ url: '/api/v1/admin/hosts/list', method: 'post' })
}

/**
 * 新建主机。
 *
 * 失败形态：`1000`/400（字段非法）、`1301`/502（凭据或端点不可用）、`1303`/400（主机名已被占用）。
 */
export function createHost(payload: CreateHostPayload): Promise<AdminHostSummary> {
  return request<AdminHostSummary>({
    url: '/api/v1/admin/hosts/create',
    method: 'post',
    data: payload
  })
}

/**
 * 更新主机的端点 / 凭据 / 启用状态。
 *
 * **不含 `name`**：主机名是对外标识（路径里的 hostId 就是它），改名会让已分享的深链失效。
 * `clearCredentials: true` 表示改为无认证——JSON 分不清「没给 tlsConfig」与「tlsConfig: null」，
 * 没有它就答不出「把主机从 TLS 改回明文」（而残留的 TLS 凭据会被后端直接拒绝）。
 */
export function updateHost(hostId: string, payload: UpdateHostPayload): Promise<AdminHostSummary> {
  return request<AdminHostSummary>({
    url: `/api/v1/admin/hosts/${encodeURIComponent(hostId)}/update`,
    method: 'post',
    data: payload
  })
}

/** 删除主机（软删除）。失败形态：`1300`/404。 */
export function deleteHost(hostId: string): Promise<void> {
  return request<void>({
    url: `/api/v1/admin/hosts/${encodeURIComponent(hostId)}/delete`,
    method: 'post'
  })
}

/**
 * 测试一份**尚未保存**的配置能否连通。
 *
 * `data=false` 表示连不上，**不是错误响应**——因此界面要把「连不上」显示成一个结论，
 * 而不是一个失败弹窗。字段非法才是 `1000`/400。
 */
export function testHostConnection(payload: TestHostPayload): Promise<boolean> {
  return request<boolean>({
    url: '/api/v1/admin/hosts/test',
    method: 'post',
    data: payload
  })
}
