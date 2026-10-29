import { request } from '@/api'
import type { AuditLogListResult, AuditLogQuery } from '@/types/api'

/**
 * 审计日志端点（0.9.0）。
 *
 * ## 为什么两页各一组路径
 * 认证日志与活动日志的过滤维度本就不同（认证没有「目标资源」与「主机」），
 * 路径分开后前端的调用点不必每次多传一个常量，也不会因为传错而整页显示另一类数据。
 *
 * ## 为什么导出用 POST 而不是 GET
 * 与其余端点一致（RPC 风格 + 统一响应）。导出会绕过分页，服务端**先计数再决定给不给**：
 * 超过 5 万行时返回 `1000` 与提示，而不是给一份被截断的文件。
 */

/** 类别 → 路径片段。 */
export type AuditCategory = 'authentication' | 'activity'

/**
 * 分页查询审计日志。
 *
 * @param category 类别。
 * @param query 过滤条件。
 * @returns 分页结果。
 */
export function listAuditLogs(
  category: AuditCategory,
  query: AuditLogQuery = {}
): Promise<AuditLogListResult> {
  return request<AuditLogListResult>({
    url: `/api/v1/admin/logs/${category}/list`,
    method: 'post',
    data: query
  })
}

/**
 * 导出审计日志（CSV）。
 *
 * 返回的是**原始文本**而不是 JSON：后端在导出成功时直接写 `text/csv`。
 * 失败（参数非法 / 超过上限 / 无权限）仍然是 JSON 的 `ApiResult`，
 * 因此前端要按 `Content-Type` 区分——这里用 `blob` 收下整个响应，
 * 由调用方判断类型。
 *
 * @param category 类别。
 * @param query 过滤条件。
 * @returns 响应（`blob`）。
 */
export function exportAuditLogs(
  category: AuditCategory,
  query: AuditLogQuery = {}
): Promise<Blob> {
  return request<Blob>({
    url: `/api/v1/admin/logs/${category}/export`,
    method: 'post',
    data: query,
    responseType: 'blob'
  })
}

/**
 * 查询某台主机上的活动日志（主机侧「事件」页）。
 *
 * @param hostId 主机标识（主机名）。
 * @param query 过滤条件。
 * @returns 分页结果。
 */
export function listHostEvents(
  hostId: string,
  query: AuditLogQuery = {}
): Promise<AuditLogListResult> {
  return request<AuditLogListResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/events/list`,
    method: 'post',
    data: query
  })
}
