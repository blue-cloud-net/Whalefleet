import { http, HttpResponse } from 'msw'
import { ok } from '../envelope'
import type { AuditLogEntry } from '@/types/api'

/**
 * 审计日志端点 mock（0.9.0）。
 *
 * ## 复刻要点
 * - **两页各一个路径**：`/authentication/list` 与 `/activity/list` 返回不同形状的数据
 *   （认证没有目标与主机），前端按页签取列的行为因此能被验证。
 * - **名字是快照**：这里的记录里带着已删除容器的名字——真实后端也是这样存的，
 *   所以界面上“删了容器还能看到名字”不是 mock 的假象。
 * - **导出返回 `text/csv`**：与真实端点一致（失败时才是 JSON）。
 */

/** 固定的时间基准（避免每次刷新日期都变）。 */
const BASE = Date.parse('2026-10-30T09:00:00Z')

/** 认证日志夹具。 */
const AUTHENTICATION: AuditLogEntry[] = [
  {
    id: 'a1',
    occurredAt: new Date(BASE).toISOString(),
    action: 'auth.login',
    actorName: 'admin',
    targetType: null,
    targetName: null,
    hostName: null,
    outcome: 'success',
    errorCode: null,
    clientIp: '192.168.1.10',
    detail: null
  },
  {
    id: 'a2',
    occurredAt: new Date(BASE - 60_000).toISOString(),
    action: 'auth.login',
    actorName: 'admin',
    targetType: null,
    targetName: null,
    hostName: null,
    outcome: 'failure',
    errorCode: 1100,
    clientIp: '192.168.1.10',
    detail: '窗口内失败 1 次'
  },
  {
    id: 'a3',
    occurredAt: new Date(BASE - 120_000).toISOString(),
    action: 'auth.login',
    actorName: 'admin',
    targetType: null,
    targetName: null,
    hostName: null,
    outcome: 'denied',
    errorCode: 1103,
    clientIp: '192.168.1.10',
    detail: null
  }
]

/** 活动日志夹具（含一条已删除容器的记录——名字是快照）。 */
const ACTIVITY: AuditLogEntry[] = [
  {
    id: 'b1',
    occurredAt: new Date(BASE).toISOString(),
    action: 'container.exec',
    actorName: 'admin',
    targetType: 'container',
    targetName: 'nginx-old',
    hostName: 'local',
    outcome: 'success',
    errorCode: null,
    clientIp: '192.168.1.10',
    detail: '会话=8f2c…；时长=125.4s；退出码=0；原因=client-closed'
  },
  {
    id: 'b2',
    occurredAt: new Date(BASE - 30_000).toISOString(),
    action: 'container.remove',
    actorName: 'admin',
    targetType: 'container',
    targetName: 'nginx-old',
    hostName: 'local',
    outcome: 'success',
    errorCode: null,
    clientIp: '192.168.1.10',
    detail: '强制=true'
  },
  {
    id: 'b3',
    occurredAt: new Date(BASE - 60_000).toISOString(),
    action: 'role.assign',
    actorName: 'admin',
    targetType: 'role-assignment',
    targetName: 'viewer → operator',
    hostName: 'local',
    outcome: 'success',
    errorCode: null,
    clientIp: '192.168.1.10',
    detail: '作用域=local'
  }
]

/** 简单分页（分页参数在**请求体**里，与其余 handler 一致）。 */
function paged<T>(items: T[], pageIndex: number, pageSize: number): {
  items: T[]
  total: number
  pageIndex: number
  pageSize: number
} {
  return {
    items: items.slice(pageIndex * pageSize, pageIndex * pageSize + pageSize),
    total: items.length,
    pageIndex,
    pageSize
  }
}

/** 从请求体读分页参数（缺省与后端一致：页 0、每页 20）。 */
function readPaging(body: unknown): { pageIndex: number; pageSize: number } {
  const source = (body ?? {}) as { pageIndex?: number; pageSize?: number }

  return {
    pageIndex: Math.max(Number(source.pageIndex ?? 0) || 0, 0),
    pageSize: Math.min(Math.max(Number(source.pageSize ?? 20) || 20, 1), 100)
  }
}

/**
 * 审计端点 mock。
 *
 * @returns handler 列表。
 */
export const auditHandlers = () => [
  http.post('/api/v1/admin/logs/:category/list', async ({ params, request }) => {
    const source = params.category === 'activity' ? ACTIVITY : AUTHENTICATION
    const { pageIndex, pageSize } = readPaging(await request.json())

    return HttpResponse.json(ok(paged(source, pageIndex, pageSize)))
  }),

  http.post('/api/v1/admin/logs/:category/export', ({ params }) => {
    const source = params.category === 'activity' ? ACTIVITY : AUTHENTICATION
    const header = params.category === 'activity'
      ? '时间 (UTC),操作人,动作,目标类型,目标,主机,结果,错误码,来源 IP,说明'
      : '时间 (UTC),用户,动作,结果,错误码,来源 IP,说明'

    const rows = source.map((entry) =>
      params.category === 'activity'
        ? [entry.occurredAt, entry.actorName, entry.action, entry.targetType ?? '',
            entry.targetName ?? '', entry.hostName ?? '', entry.outcome,
            entry.errorCode ?? '', entry.clientIp ?? '', entry.detail ?? ''].join(',')
        : [entry.occurredAt, entry.actorName, entry.action, entry.outcome,
            entry.errorCode ?? '', entry.clientIp ?? '', entry.detail ?? ''].join(',')
    )

    return new HttpResponse(`\uFEFF${header}\r\n${rows.join('\r\n')}\r\n`, {
      status: 200,
      headers: { 'Content-Type': 'text/csv; charset=utf-8' }
    })
  }),

  http.post('/api/v1/hosts/:hostId/events/list', async ({ request }) => {
    const { pageIndex, pageSize } = readPaging(await request.json())

    return HttpResponse.json(ok(paged(ACTIVITY, pageIndex, pageSize)))
  })
]
