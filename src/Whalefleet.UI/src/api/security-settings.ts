import { request } from '@/api'
import type { SecurityPolicyOptions } from '@/types/api'

/**
 * 实例级安全策略端点（0.13.0）。
 *
 * 路径与请求 / 响应结构必须与 `docs/api-spec.md` 的「账号状态、会话与安全策略」一致。
 */

/**
 * 读取安全策略。
 *
 * 失败形态：`1500`/403（缺少 `user.write`）。
 */
export function getSecuritySettings(): Promise<SecurityPolicyOptions> {
  return request<SecurityPolicyOptions>({
    url: '/api/v1/admin/settings/security/get',
    method: 'post'
  })
}

/**
 * 保存安全策略。
 *
 * ⚠️ **只影响之后发生的动作**：已有账号的密码不会被追溯校验，
 * 已有账号的「必须改密」标记也不会被批量置位——
 * 否则一次调参就会把所有人锁在门外。
 *
 * 失败形态：`1000`/400（参数非法）、`1500`/403。
 */
export function updateSecuritySettings(
  payload: SecurityPolicyOptions
): Promise<SecurityPolicyOptions> {
  return request<SecurityPolicyOptions>({
    url: '/api/v1/admin/settings/security/update',
    method: 'post',
    data: payload
  })
}
