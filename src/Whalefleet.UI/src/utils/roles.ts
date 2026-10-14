/**
 * 内置角色（与后端 `roles` 表种入的四条一致）。
 *
 * ## 为什么前端要内置这份名单
 * 后端**没有**角色列表端点（0.0.13 范围内不打算加：角色管理界面排在更后面）。
 * 而创建用户时必须选一个角色，于是有两种做法：
 *
 * 1. 只给一个自由输入框 —— 管理员得先记住 `admin` / `operator` / `viewer` / `auditor` 的拼写，
 *    拼错只会得到一句「角色「xxx」不存在」。
 * 2. **给出内置名单的快捷选项 + 保留自由输入** —— 常见情况点一下即可，自定义角色照样能填。
 *
 * 这里选 2。名单只用于**渲染选项**，不做任何校验：后端允许自定义角色，
 * 前端拿它当白名单会把「支持自定义角色」这件事当场废掉。
 */
export const BUILT_IN_ROLES = ['admin', 'operator', 'viewer', 'auditor'] as const

export type BuiltInRole = (typeof BUILT_IN_ROLES)[number]

/** 角色名的 i18n key（`role.admin` 等）；自定义角色没有对应 key，直接显示原值。 */
export function roleLabelKey(role: string): string | null {
  return (BUILT_IN_ROLES as readonly string[]).includes(role) ? `role.${role}` : null
}
