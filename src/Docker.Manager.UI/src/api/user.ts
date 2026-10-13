import { request } from '@/api'
import type { AdminUserSummary, UserListResult } from '@/types/api'

/**
 * 用户管理端点（**全局作用域**，路径不含 `hostId`）。
 *
 * ## 为什么「改密码」与「用户管理」在同一个文件里
 * 后端把它们做成**同一个端点**（`admin/users/update-password`），只是语义按 `userId` 分岔：
 * 省略 = 改自己（必须验原密码），指定他人 = 管理员重置（不需要原密码）。
 * 前端分成两个对话框但共用同一个函数，正是为了让「它们其实是同一件事」显式可见——
 * 拆成两个端点反而会出现「两个入口的规则悄悄漂移」。
 *
 * 路径与请求/响应结构必须与 `docs/api-spec.md` 的「用户管理」一致；
 * 失败统一走 `ApiResult` + 全局异常中间件，因此这里不写 `try/catch`。
 */

/** 用户列表（分页，`pageSize` 上限 100 且**夹紧后的值回显**）。 */
export function listUsers(
  params: { pageIndex?: number; pageSize?: number } = {}
): Promise<UserListResult> {
  return request<UserListResult>({
    url: '/api/v1/admin/users/list',
    method: 'post',
    data: params
  })
}

/**
 * 创建用户。
 *
 * `role` 省略时由后端取**默认角色**（当前为 `auditor`，可由管理员改），
 * 因此表单允许留空——让「用默认角色」成为默认行为，而不是逼管理员先想清楚角色。
 *
 * 失败形态：`1000`/400（字段非法或角色不存在）、`1200`/400（用户名已存在）、
 * `1202`/400（密码强度不足）。
 */
export function createUser(payload: {
  username: string
  password: string
  role?: string
}): Promise<AdminUserSummary> {
  return request<AdminUserSummary>({
    url: '/api/v1/admin/users/create',
    method: 'post',
    data: payload
  })
}

/**
 * 删除用户（软删除）并撤销其全部会话。
 *
 * 两道护栏在后端：**不能删自己**、**不能删最后一个管理员**，两者都是 `1000`/400。
 * 界面据此把「自己那一行」的删除按钮直接禁用掉——让用户点下去才报错是糟糕的交互。
 */
export function deleteUser(userId: string): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/users/delete',
    method: 'post',
    data: { userId }
  })
}

/**
 * 修改密码。
 *
 * - **改自己**：省略 `userId`，**必须**提供 `oldPassword`（否则 `1000`/400）。
 *   只凭一个被盗的 access token 就能改密码，攻击者就能把真正的主人锁在门外。
 * - **管理员重置他人**：提供 `userId`，不需要原密码。
 *
 * 成功后该用户**全部刷新令牌被撤销**（需重新登录）——改密码最常见的动因就是「疑似泄漏」，
 * 不撤销等于把旧凭据留在原地。界面必须把这一点告诉用户。
 */
export function updatePassword(payload: {
  userId?: string
  oldPassword?: string
  newPassword: string
}): Promise<void> {
  return request<void>({
    url: '/api/v1/admin/users/update-password',
    method: 'post',
    data: payload
  })
}
