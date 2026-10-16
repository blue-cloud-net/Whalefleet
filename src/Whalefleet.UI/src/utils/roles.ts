/**
 * 角色与权限的展示口径（纯逻辑，可脱离组件单测）。
 *
 * ## 为什么内置角色名仍在前端留一份名单
 * 它**只用于取 i18n 文案**，不做任何校验，也不是权限判定依据：
 * 角色是**数据**（`roles` 表），管理员可以在运行时增删改。拿这份名单当白名单，
 * 会把「支持自定义角色」当场废掉。自定义角色没有对应文案，界面直接显示标识本身。
 *
 * ## 权限文案为什么是显式 Record 而不是拼 key
 * 权限名来自后端目录（`host.read` 这种 `domain.level` 标识）。在模板里拼
 * `` t(`permission.${name}`) `` 会有两个问题：拼出来的 key 一旦不存在，
 * vue-i18n **只告警不报错**，界面上直接显示一串 key 路径；而且权限名本身含点，
 * 拼出来的层级与 i18n 的点号分隔语义混在一起，读的人也说不清最终查的是哪个 key。
 * 显式映射 + 回退原值让「没登记」这件事表现为「显示 `host.read`」——
 * 可读、可搜，且不会静默变成 key 路径。
 */

/** 内置角色（与后端 `0004_roles.sql` 种入的四条一致）。 */
export const BUILT_IN_ROLES = ['admin', 'operator', 'viewer', 'auditor'] as const

export type BuiltInRole = (typeof BUILT_IN_ROLES)[number]

/** 角色名的 i18n key（`role.admin` 等）；自定义角色没有对应 key，直接显示原值。 */
export function roleLabelKey(role: string): string | null {
  return (BUILT_IN_ROLES as readonly string[]).includes(role) ? `role.${role}` : null
}

/**
 * 角色的展示文本：**可读名称优先**，其次内置角色的 i18n 文案，最后回退标识本身。
 *
 * 可读名称是管理员填的数据，按原样显示（把「预发布」硬翻成英文只会更难懂）；
 * 而自定义角色常常没填可读名称，那时回退到标识仍比显示空白有用。
 *
 * `translate` 由调用方传入而不是在这里 import i18n 实例：
 * 纯逻辑要能脱离组件与当前 locale 单测。
 */
export function roleText(
  role: { name: string; displayName: string | null },
  translate: (key: string) => string
): string {
  const displayName = role.displayName?.trim()
  if (displayName !== undefined && displayName.length > 0) return displayName

  const key = roleLabelKey(role.name)

  return key === null ? role.name : translate(key)
}

/**
 * 「管理面」权限：能管用户、能管角色、能注册/删除主机。
 *
 * ## 为什么管理员判定要看这三个权限，而不是看角色名
 * 角色名只是数据——自定义角色也可以带全部权限，而一个被改窄了权限的 `admin`
 * 角色（比如只有 `audit.read`）不该继续被当成管理员。
 * 这三项覆盖了「系统管理」的全部入口：用户、角色、主机注册。
 * 主机域权限（`host.read` 等）**不在其中**——拥有某台机器的操作权不等于系统管理员。
 *
 * ⚠️ 这是**界面显隐依据，不是安全边界**：服务端一律按 `IPermissionChecker` 查库判定。
 * 拿它藏一个按钮，最多让无权的人看不到入口；篡改它也只能得到一个点了会 403 的界面。
 */
export const ADMIN_PERMISSIONS = ['user.write', 'role.write', 'host.manage'] as const

/** 权限集是否涵盖管理面（三个都具备才算管理员）。 */
export function isAdminPermissions(permissions: readonly string[]): boolean {
  return ADMIN_PERMISSIONS.every((permission) => permissions.includes(permission))
}

/**
 * 权限名 → i18n key。未登记的权限直接显示标识本身。
 */
const PERMISSION_LABEL_KEYS: Record<string, string> = {
  'host.read': 'permission.host.read',
  'host.admin': 'permission.host.admin',
  'container.read': 'permission.container.read',
  'container.write': 'permission.container.write',
  'image.read': 'permission.image.read',
  'image.write': 'permission.image.write',
  'network.read': 'permission.network.read',
  'network.write': 'permission.network.write',
  'volume.read': 'permission.volume.read',
  'volume.write': 'permission.volume.write',
  'user.read': 'permission.user.read',
  'user.write': 'permission.user.write',
  'role.read': 'permission.role.read',
  'role.write': 'permission.role.write',
  'host.manage': 'permission.host.manage',
  'audit.read': 'permission.audit.read'
}

/** 权限的 i18n key；后端新增权限时返回 null（调用方回退显示权限名）。 */
export function permissionLabelKey(permission: string): string | null {
  return PERMISSION_LABEL_KEYS[permission] ?? null
}

/** 资源域 → i18n key（权限矩阵的分组标题）。未登记的域回退显示域本身。 */
const DOMAIN_LABEL_KEYS: Record<string, string> = {
  host: 'permission.domain.host',
  container: 'permission.domain.container',
  image: 'permission.domain.image',
  network: 'permission.domain.network',
  volume: 'permission.domain.volume',
  user: 'permission.domain.user',
  role: 'permission.domain.role',
  audit: 'permission.domain.audit'
}

/** 域的 i18n key；未登记的域返回 null。 */
export function domainLabelKey(domain: string): string | null {
  return DOMAIN_LABEL_KEYS[domain] ?? null
}

/** 作用域 → i18n key。作用域决定「这条权限在哪个 hostId 上求值」。 */
const SCOPE_LABEL_KEYS: Record<string, string> = {
  global: 'permission.scope.global',
  host: 'permission.scope.host',
  container: 'permission.scope.container'
}

/** 作用域的 i18n key；未登记的作用域返回 null。 */
export function scopeLabelKey(scope: string): string | null {
  return SCOPE_LABEL_KEYS[scope] ?? null
}

