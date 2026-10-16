import { request } from '@/api'
import type {
  AssignRoleRequest,
  PermissionDefinition,
  RevokeRoleAssignmentRequest,
  RoleAssignmentSummary,
  RoleSelectorRequest,
  RoleSummary
} from '@/types/api'

/**
 * 角色与授权端点（0.1.0）。
 *
 * ## 路径约定（见 docs/api-spec.md）
 *
 * 三组端点，分工是刻意分开的：
 *
 * | 基址 | 回答的问题 |
 * |------|------------|
 * | `/api/v1/admin/roles/*` | 有哪些**角色**、各自能干什么 |
 * | `/api/v1/admin/hosts/{hostId}/roles/*` | **这台机器**上谁以什么角色进来 |
 * | `/api/v1/admin/users/{userId}/roles/*` | **这个人**在哪几台机器上、以什么角色 |
 *
 * 同一个「角色分配」资源有两个视角，不做成两个端点：
 * 从主机侧看是「谁能管这台机器」，从用户侧看是「他能管哪几台机器」。
 *
 * ## hostId 是主机名
 *
 * 不是 `hosts.id`。整个 admin 侧都用主机名做路由键（见 `api/host.ts`），
 * 这里保持一致。
 *
 * ## 请求体字段名照抄后端记录类型
 *
 * 后端的 `RoleSelectorRequest` / `AssignRoleRequest` / `RevokeRoleAssignmentRequest`
 * 都有「角色主键或角色名」两个可选字段（至少给一个），前端这里只用 `roleId`
 * （列表里选中的行天然带主键），但**字段名必须与后端一致**——否则会出现
 * 「发了 hostId、后端只认 host」这种静默失效：JSON 里多一个不认识的字段，
 * 后端不报错，只是把它当成「没给 host」＝ 全局分配。
 */

/** 角色列表的基址。 */
function roleBase(): string {
  return '/api/v1/admin/roles'
}

/** 主机作用域分配的基址；`hostId` 是**主机名**。 */
function hostRoleBase(hostId: string): string {
  return `/api/v1/admin/hosts/${encodeURIComponent(hostId)}/roles`
}

/** 用户作用域分配的基址；`userId` 是用户主键。 */
function userRoleBase(userId: string): string {
  return `/api/v1/admin/users/${encodeURIComponent(userId)}/roles`
}

/* ==================================================================
 * 角色（管理角色本身）
 * ================================================================== */

/**
 * 列出角色（含各自的权限集与当前分配数）。
 *
 * 返回**普通数组**而非分页信封：角色是人工维护的个位数集合，
 * 分页只会给每个调用点多一层要拆的壳。
 *
 * `customOnly=true` 时只返回自定义角色（内置角色被排除）——权限矩阵只想编辑
 * 自定义的那些，而分配场景要全部角色，因此做成一个开关而不是让前端各自过滤。
 *
 * 失败形态：`1500`/403（无 `role.read` 权限）。
 */
export function listRoles(customOnly = false): Promise<RoleSummary[]> {
  return request<RoleSummary[]>({
    url: `${roleBase()}/list`,
    method: 'post',
    data: { customOnly }
  })
}

/**
 * 列出**全部可授予的权限**（权限目录）。
 *
 * 后端自带全部权限的目录，界面**直接遍历它**渲染矩阵，
 * 不要在前端硬编码一份权限列表——那份副本必然与后端不同步。
 *
 * 失败形态：`1500`/403（无 `role.read` 权限）。
 */
export function listPermissionDefinitions(): Promise<PermissionDefinition[]> {
  return request<PermissionDefinition[]>({
    url: `${roleBase()}/permissions`,
    method: 'post'
  })
}

/** 新建角色。入参里的 `permissions` 可省略（省略即空权限集）。 */
export interface CreateRolePayload {
  name: string
  displayName: string
  description?: string
  permissions?: string[]
}

/**
 * 新建角色。
 *
 * 失败形态：`1000`/400（名称非法或权限名未知）、`1511`/400（角色名已存在）、
 * `1500`/403（无 `role.write` 权限）。
 */
export function createRole(payload: CreateRolePayload): Promise<RoleSummary> {
  return request<RoleSummary>({
    url: `${roleBase()}/create`,
    method: 'post',
    data: payload
  })
}

/** 修改角色可读信息（对应后端 `UpdateRoleRequest`）。角色标识不可改。 */
export interface UpdateRolePayload extends RoleSelectorRequest {
  displayName?: string
  description?: string
}

/**
 * 修改角色。
 *
 * `role` **只在请求体里**，URL 上不带——路径只承载 `hostId`（见 AGENTS.md）。
 *
 * 失败形态：`1000`/400、`1510`/404（角色不存在）。
 * 内置角色的可读信息**允许**修改——受保护的是权限集与删除，不是文案。
 */
export function updateRole(payload: UpdateRolePayload): Promise<RoleSummary> {
  return request<RoleSummary>({
    url: `${roleBase()}/update`,
    method: 'post',
    data: payload
  })
}

/** 设置角色的权限集（**覆盖**语义）。 */
export interface SetRolePermissionsPayload extends RoleSelectorRequest {
  /** **必填**，但允许空数组（「收回这个角色的全部权限」是合法操作） */
  permissions: string[]
}

/**
 * 设置角色的权限集。
 *
 * **覆盖而非叠加**：提交什么就是什么，没有的权限会被移除。
 * 界面要的正是「这个角色勾成什么样」，一次写入就够。
 *
 * 失败形态：`1000`/400（权限名未知）、`1510`/404、`1513`/400（内置角色）、
 * `1500`/403。
 */
export function setRolePermissions(payload: SetRolePermissionsPayload): Promise<RoleSummary> {
  return request<RoleSummary>({
    url: `${roleBase()}/set-permissions`,
    method: 'post',
    data: payload
  })
}

/**
 * 把某个角色设为**默认角色**（新用户创建时采用）。
 *
 * ⚠️ 后端是**单向**的：请求体只有 `{ roleId }` 或 `{ role }`，**没有 `isDefault`**。
 * 「取消默认」的唯一方式是把另一个角色设为默认（仓储在同一事务里先清掉旧标记）。
 * 因此界面上不该出现「取消默认」按钮——它没有对应的请求。
 *
 * 失败形态：`1510`/404。
 */
export function setDefaultRole(payload: RoleSelectorRequest): Promise<RoleSummary> {
  return request<RoleSummary>({
    url: `${roleBase()}/set-default`,
    method: 'post',
    data: payload
  })
}

/**
 * 删除角色。
 *
 * **仍有用户持有该角色时失败**（`1512`/400）——后端不隐式清理分配，
 * 因为那会让一批用户的权限无声消失。
 *
 * 失败形态：`1510`/404、`1513`/400（内置角色不可删）、`1512`/400。
 */
export function removeRole(payload: RoleSelectorRequest): Promise<void> {
  return request<void>({
    url: `${roleBase()}/remove`,
    method: 'post',
    data: payload
  })
}

/* ==================================================================
 * 角色分配：主机视角
 * ================================================================== */

/**
 * 列出某台主机上的全部角色分配（谁以什么角色管这台机器）。
 *
 * `userId` 省略即列全部；带上它只列这个人的——用户管理页嵌对话框时正好用它。
 *
 * ⚠️ **结果里不含全局分配**：它对所有主机生效、无法归属到某一台。
 */
export function listHostRoleAssignments(
  hostId: string,
  userId?: string
): Promise<RoleAssignmentSummary[]> {
  return request<RoleAssignmentSummary[]>({
    url: `${hostRoleBase(hostId)}/list`,
    method: 'post',
    data: userId === undefined ? {} : { userId }
  })
}

/**
 * 给某台主机上的某个用户授予一个角色。
 *
 * 分配是 `(用户, 角色, 作用域)` 三元组：同一用户可以在 A 机是 `operator`、
 * 在 B 机只是 `viewer`。`hostId` **不在这份请求体里**——它就在 URL 上。
 *
 * **幂等**：对已有组合再次分配返回同一条记录（不换主键）。
 *
 * 失败形态：`1510`/404（角色不存在）、`1201`/404（用户不存在）、
 * `1300`/404（主机不存在）。
 */
export function assignHostRole(
  hostId: string,
  payload: Pick<AssignRoleRequest, 'userId' | 'roleId' | 'role'>
): Promise<RoleAssignmentSummary> {
  return request<RoleAssignmentSummary>({
    url: `${hostRoleBase(hostId)}/assign`,
    method: 'post',
    data: { userId: payload.userId, roleId: payload.roleId }
  })
}

/**
 * 撤销某台主机上的某条分配。
 *
 * **按三元组精确撤销**——传 `userId` + `roleId` + URL 上的 `hostId`，
 * 撤销的是这一条。别的角色、别的作用域不受影响。
 *
 * 失败形态：`1515`/404（该分配不存在）、`1300`/404（主机不存在）。
 */
export function revokeHostRole(
  hostId: string,
  payload: Pick<RevokeRoleAssignmentRequest, 'userId' | 'roleId' | 'role'>
): Promise<void> {
  return request<void>({
    url: `${hostRoleBase(hostId)}/revoke`,
    method: 'post',
    data: { userId: payload.userId, roleId: payload.roleId }
  })
}

/* ==================================================================
 * 角色分配：用户视角
 * ================================================================== */

/** 列出某个用户的全部角色分配（全局 + 各主机）。 */
export function listUserRoleAssignments(userId: string): Promise<RoleAssignmentSummary[]> {
  return request<RoleAssignmentSummary[]>({
    url: `${userRoleBase(userId)}/list`,
    method: 'post'
  })
}

/**
 * 给某个用户授予一个角色；`host` 为 `null` / 省略表示**全局分配**。
 *
 * 这是「不指定主机」的那条路径——它只能走用户视角：主机视角的端点作用域由路径固定，
 * 天然表达不了「这个人在所有主机上都怎样」。
 *
 * 字段名是 `host`（主机名）而不是 `hostId`：后端记录里就叫 `host`，
 * 发错名字不报错，只会静默变成全局分配。
 *
 * 失败形态：`1510`/404、`1201`/404、`1300`/404。
 */
export function assignUserRole(
  userId: string,
  payload: Pick<AssignRoleRequest, 'roleId' | 'role' | 'host'>
): Promise<RoleAssignmentSummary> {
  return request<RoleAssignmentSummary>({
    url: `${userRoleBase(userId)}/assign`,
    method: 'post',
    data: { roleId: payload.roleId, host: payload.host ?? null }
  })
}

/**
 * 撤销某个用户的一条分配（`host` 为 `null` 表示撤销全局分配）。
 *
 * 失败形态：`1515`/404（该分配本就不存在）、`1300`/404。
 */
export function revokeUserRole(
  userId: string,
  payload: Pick<RevokeRoleAssignmentRequest, 'roleId' | 'role' | 'host'>
): Promise<void> {
  return request<void>({
    url: `${userRoleBase(userId)}/revoke`,
    method: 'post',
    data: { roleId: payload.roleId, host: payload.host ?? null }
  })
}
