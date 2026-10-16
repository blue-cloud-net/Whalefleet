import type { PermissionDefinition, RoleAssignmentSummary, RoleSummary } from '@/types/api'

/**
 * mock 角色 / 权限目录 / 角色分配夹具（0.1.0）。
 *
 * ## 为什么权限目录要在这里再写一份
 * 后端的 `/admin/roles/permissions` 返回的是**编译期目录**（`Permission.Hosts` +
 * `Permission.Globals`，共 16 项）。mock 必须能返回同一份，
 * 否则权限矩阵在没有后端时无法渲染。**顺序与后端一致**（Host 域在前、Global 在后），
 * 因为界面的分组顺序就来自它。
 *
 * 权限矩阵**不许**在前端另写一份列表——界面的列必须遍历这个响应。
 *
 * ## 四个内置角色 = 迁移 `0006_rbac.sql` 的权限种子
 * 夹具照抄迁移里那 32 行 `role_permissions`，不是「随手编一组像样的权限」：
 * 走查时看到的矩阵要与真库一致，否则「内置角色只读」这条会在一半的格子上得到错误印象。
 *
 * ## 分配夹具刻意覆盖的四种形状
 * 1. **全局分配**（`admin` → `admin`）：贡献角色的全部权限
 * 2. **按主机分配**（`readonly-li` → `viewer` @ `prod-01`）：只在该主机上生效
 * 3. **同一用户多角色 + 多作用域**（`ops-zhang`：全局 `operator`，另在 `staging-01` 上是 `viewer`）
 *    —— 界面不能把「有全局分配」误当成「不再需要主机分配」
 * 4. **自定义角色**（`deployer-bot` → `deployer`）：证明自定义角色能出现在分配列表里
 *
 * 主键与 `MOCK_USERS_DEFAULT` 的用户一一对应，与 `data/hosts.ts` 的主机名一一对应：
 * 跨夹具引用必须真实对得上，否则走查会指向一个不存在的用户或主机。
 */

/** 权限目录（对应后端 `RoleService.ListPermissionDefinitions()`）。 */
export const MOCK_PERMISSION_DEFINITIONS: PermissionDefinition[] = [
  // ===== Host 作用域（随主机变化） =====
  { name: 'host.read', domain: 'host', level: 'read', scope: 'host' },
  { name: 'host.admin', domain: 'host', level: 'admin', scope: 'host' },
  { name: 'container.read', domain: 'container', level: 'read', scope: 'host' },
  { name: 'container.write', domain: 'container', level: 'write', scope: 'host' },
  { name: 'image.read', domain: 'image', level: 'read', scope: 'host' },
  { name: 'image.write', domain: 'image', level: 'write', scope: 'host' },
  { name: 'network.read', domain: 'network', level: 'read', scope: 'host' },
  { name: 'network.write', domain: 'network', level: 'write', scope: 'host' },
  { name: 'volume.read', domain: 'volume', level: 'read', scope: 'host' },
  { name: 'volume.write', domain: 'volume', level: 'write', scope: 'host' },

  // ===== Global 作用域（与主机无关） =====
  { name: 'user.read', domain: 'user', level: 'read', scope: 'global' },
  { name: 'user.write', domain: 'user', level: 'write', scope: 'global' },
  { name: 'role.read', domain: 'role', level: 'read', scope: 'global' },
  { name: 'role.write', domain: 'role', level: 'write', scope: 'global' },
  { name: 'host.manage', domain: 'host', level: 'manage', scope: 'global' },
  { name: 'audit.read', domain: 'audit', level: 'read', scope: 'global' }
]

/** 全部权限名（顺序同目录）；仅用于夹具与 handler 的白名单校验。 */
export const MOCK_PERMISSION_NAMES: string[] = MOCK_PERMISSION_DEFINITIONS.map((item) => item.name)

/** 目录之外的一律非法（对应后端 `PermissionNames.Normalize` 的白名单校验）。 */
export function isKnownPermission(name: unknown): name is string {
  return typeof name === 'string' && MOCK_PERMISSION_NAMES.includes(name.trim().toLowerCase())
}

/**
 * 角色夹具。主键沿用迁移 `0004_roles.sql` / `0006_rbac.sql` 的固定值，
 * 因此文档与运维脚本里出现的 id 在 mock 下同样成立。
 */
export const MOCK_ROLES_DEFAULT: RoleSummary[] = [
  {
    id: '00000000-0000-0000-0000-0000000000a1',
    name: 'admin',
    displayName: '管理员',
    description: '可管理用户、主机与全部资源',
    isBuiltIn: true,
    isDefault: false,
    permissions: [...MOCK_PERMISSION_NAMES],
    assignmentCount: 1
  },
  {
    id: '00000000-0000-0000-0000-0000000000a2',
    name: 'operator',
    displayName: '运维',
    description: '对已授权主机上的容器、镜像、数据卷与网络有操作权限',
    isBuiltIn: true,
    isDefault: false,
    permissions: [
      'host.read',
      'container.read',
      'container.write',
      'image.read',
      'image.write',
      'network.read',
      'network.write',
      'volume.read',
      'volume.write'
    ],
    assignmentCount: 1
  },
  {
    id: '00000000-0000-0000-0000-0000000000a3',
    name: 'viewer',
    displayName: '只读',
    description: '对已授权资源仅可查看，不可修改',
    isBuiltIn: true,
    isDefault: false,
    permissions: ['host.read', 'container.read', 'image.read', 'network.read', 'volume.read'],
    assignmentCount: 2
  },
  {
    id: '00000000-0000-0000-0000-0000000000a4',
    name: 'auditor',
    displayName: '审计',
    description: '只读，并可查看操作审计',
    isBuiltIn: true,
    isDefault: true,
    permissions: [
      'host.read',
      'container.read',
      'image.read',
      'network.read',
      'volume.read',
      'audit.read'
    ],
    assignmentCount: 0
  },
  {
    // 自定义角色：可改权限、可删除。内置角色之外必须至少有一个，
    // 否则「内置角色只读」与「自定义角色可编辑」这两条无法在同一次走查里区分。
    id: '00000000-0000-0000-0000-0000000000c1',
    name: 'deployer',
    displayName: '发布机器人',
    description: '只能拉取镜像并重建容器，不能改主机配置',
    isBuiltIn: false,
    isDefault: false,
    permissions: ['host.read', 'container.read', 'container.write', 'image.read', 'image.write'],
    assignmentCount: 1
  }
]

/**
 * 角色分配夹具（全局 + 主机作用域混在一起）。
 *
 * `hostId` / `hostName` 同时为 `null` 即全局分配——界面必须把这一行与
 * 「在某台主机上」严格区分渲染，混起来看就等于「把 admin 分配到某台主机」
 * 被误读成系统级提权。
 */
export const MOCK_ROLE_ASSIGNMENTS_DEFAULT: RoleAssignmentSummary[] = [
  {
    id: '00000000-0000-0000-0000-0000000000d1',
    userId: '00000000-0000-0000-0000-0000000000b1',
    username: 'admin',
    roleId: '00000000-0000-0000-0000-0000000000a1',
    role: 'admin',
    roleDisplayName: '管理员',
    hostId: null,
    hostName: null,
    lastModificationTime: null
  },
  {
    id: '00000000-0000-0000-0000-0000000000d2',
    userId: '00000000-0000-0000-0000-0000000000b2',
    username: 'ops-zhang',
    roleId: '00000000-0000-0000-0000-0000000000a2',
    role: 'operator',
    roleDisplayName: '运维',
    hostId: null,
    hostName: null,
    lastModificationTime: null
  },
  {
    // 同一用户既有全局分配，又在某台主机上多一个只读角色：
    // 分配列表里必须出现两行，界面不能因为「已经全局有角色了」就吞掉这一条。
    id: '00000000-0000-0000-0000-0000000000d3',
    userId: '00000000-0000-0000-0000-0000000000b2',
    username: 'ops-zhang',
    roleId: '00000000-0000-0000-0000-0000000000a3',
    role: 'viewer',
    roleDisplayName: '只读',
    hostId: '00000000-0000-0000-0000-000000000003',
    hostName: 'staging-01',
    lastModificationTime: null
  },
  {
    // 只有主机分配、没有全局分配：全局权限集应当为空（`auth/me` 的 permissions）。
    id: '00000000-0000-0000-0000-0000000000d4',
    userId: '00000000-0000-0000-0000-0000000000b3',
    username: 'readonly-li',
    roleId: '00000000-0000-0000-0000-0000000000a3',
    role: 'viewer',
    roleDisplayName: '只读',
    hostId: '00000000-0000-0000-0000-000000000002',
    hostName: 'prod-01',
    lastModificationTime: null
  },
  {
    id: '00000000-0000-0000-0000-0000000000d5',
    userId: '00000000-0000-0000-0000-0000000000b4',
    username: 'deployer-bot',
    roleId: '00000000-0000-0000-0000-0000000000c1',
    role: 'deployer',
    roleDisplayName: '发布机器人',
    hostId: null,
    hostName: null,
    lastModificationTime: null
  }
]

