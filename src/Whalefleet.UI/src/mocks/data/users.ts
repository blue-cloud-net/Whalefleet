import type { AdminUserSummary } from '@/types/api'

/**
 * mock 用户数据。
 *
 * 夹具刻意覆盖四种需要区别对待的情形：
 * 1. **当前登录用户自己**（`admin`）——删除按钮必须禁用
 * 2. **自定义角色持有者**（`deployer-bot` 持 `deployer`）——后端支持自定义角色，
 *    界面上必须能原样显示而不是报错
 * 3. **未分配任何角色的用户**（`pending-chen`）——新建用户到被分配角色之间就是这个状态，
 *    `roles` 是**空数组**而不是 null，也不能因此把人踢下线
 * 4. **多角色用户**（`ops-zhang` 同时是 `operator` 与 `viewer`）——0.1.0 起用户与角色是多对多
 *
 * ⚠️ 与真实数据一样，这里**不存任何密码**：改密 mock 只校验「原密码是否正确」这一件事，
 * 用一个固定常量比对（见 `handlers/users.ts`），而不是给每个夹具挂一个哈希。
 */
export const MOCK_USERS_DEFAULT: AdminUserSummary[] = [
  {
    id: '00000000-0000-0000-0000-0000000000b1',
    username: 'admin',
    roles: ['admin'],
    creationTime: '2026-09-20T02:00:00Z'
  },
  {
    id: '00000000-0000-0000-0000-0000000000b2',
    username: 'ops-zhang',
    roles: ['operator', 'viewer'],
    creationTime: '2026-09-28T07:31:12Z'
  },
  {
    id: '00000000-0000-0000-0000-0000000000b3',
    username: 'readonly-li',
    roles: ['viewer'],
    creationTime: '2026-10-02T11:05:40Z'
  },
  {
    id: '00000000-0000-0000-0000-0000000000b4',
    username: 'deployer-bot',
    roles: ['deployer'],
    creationTime: '2026-10-08T03:44:00Z'
  },
  {
    id: '00000000-0000-0000-0000-0000000000b5',
    username: 'pending-chen',
    roles: [],
    creationTime: '2026-10-12T08:20:00Z'
  }
]

/** mock 里「当前登录用户」的用户名，与 auth mock 的登录账号一致。 */
export const MOCK_CURRENT_USERNAME = 'admin'

/** mock 里 admin 账号的口令（仅用于验证「原密码是否正确」）。 */
export const MOCK_CURRENT_PASSWORD = 'demo1234'
