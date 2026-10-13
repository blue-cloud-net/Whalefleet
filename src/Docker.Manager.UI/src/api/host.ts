import { request } from '@/api'
import type { HostOverview } from '@/types/api'

/**
 * 主机相关端点。
 *
 * ## 路径约定（见 docs/api-spec.md）
 *
 * - `POST /api/v1/hosts/list` —— **当前用户可见**的主机。总览页与侧栏 `HostSelector` 都用它，
 *   因此它对所有角色开放；admin 调用时返回全部主机。
 * - `POST /api/v1/admin/hosts/list` —— 主机管理页专用（admin），返回全部主机供管理操作。
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