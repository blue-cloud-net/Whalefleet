import type { AdminUserSummary } from '@/types/api'

/**
 * mock 用户数据。
 *
 * 夹具刻意覆盖三种需要区别对待的情形：
 * 1. **当前登录用户自己**（`admin`）——删除按钮必须禁用，改密走「验原密码」分支
 * 2. **另一个 admin**——用来验证「不能删最后一个管理员」之外的另一条路（可以删）
 * 3. **自定义角色**（`deployer`）——后端支持自定义角色，界面上必须能原样显示而不是报错
 *
 * ⚠️ 与真实数据一样，这里**不存任何密码**：改密 mock 只校验「原密码是否正确」这一件事，
 * 用一个固定常量比对（见 `handlers/users.ts`），而不是给每个夹具挂一个哈希。
 */
export const MOCK_USERS_DEFAULT: AdminUserSummary[] = [
  {
    id: '00000000-0000-0000-0000-0000000000b1',
    username: 'admin',
    role: 'admin',
    creationTime: '2026-09-20T02:00:00Z'
  },
  {
    id: '00000000-0000-0000-0000-0000000000b2',
    username: 'ops-zhang',
    role: 'operator',
    creationTime: '2026-09-28T07:31:12Z'
  },
  {
    id: '00000000-0000-0000-0000-0000000000b3',
    username: 'readonly-li',
    role: 'viewer',
    creationTime: '2026-10-02T11:05:40Z'
  },
  {
    id: '00000000-0000-0000-0000-0000000000b4',
    username: 'deployer-bot',
    role: 'deployer',
    creationTime: '2026-10-08T03:44:00Z'
  }
]

/** mock 里「当前登录用户」的用户名，与 auth mock 的登录账号一致。 */
export const MOCK_CURRENT_USERNAME = 'admin'

/** mock 里 admin 账号的口令（仅用于验证「原密码是否正确」）。 */
export const MOCK_CURRENT_PASSWORD = 'demo1234'
