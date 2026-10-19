import type { AdminUserSummary } from '@/types/api'

/**
 * mock 用户数据。
 *
 * 语义夹具（`MOCK_USERS_DEFAULT` 的前 5 条）刻意覆盖四种需要区别对待的情形：
 * 1. **当前登录用户自己**（`admin`）——删除按钮必须禁用
 * 2. **自定义角色持有者**（`deployer-bot` 持 `deployer`）——后端支持自定义角色，
 *    界面上必须能原样显示而不是报错
 * 3. **未分配任何角色的用户**（`pending-chen`）——新建用户到被分配角色之间就是这个状态，
 *    `roles` 是**空数组**而不是 null，也不能因此把人踢下线
 * 4. **多角色用户**（`ops-zhang` 同时是 `operator` 与 `viewer`）——0.1.0 起用户与角色是多对多
 *
 * ⚠️ 与真实数据一样，这里**不存任何密码**：改密 mock 只校验「原密码是否正确」这一件事，
 * 用一个固定常量比对（见 `handlers/users.ts`），而不是给每个夹具挂一个哈希。
 *
 * ## 为什么后面还有一张 20+ 条的账号表
 * 前 5 条够验证「用户与角色的四种形态」，但用户列表同样是个**分页列表**
 * （默认 20 条 / 页）。只有 5 条时：分页控件不出现、用户名搜索只能搜到 4 个、
 * 「共 N 位用户」永远是 5、角色筛选器只有三种命中——这些分支全都走不到。
 * 真实部署里几十上百个账号是常态，所以 mock 也给到这个量级。
 *
 * ⚠️ 生成部分**恰好只有一个 `admin`**：多一个，「删除最后一个管理员 → 1000」
 * 这条护栏在走查里就再也触发不了（`mock-contract.spec.ts` 也断言了这一点）。
 */

/** 语义夹具：每条都在验证一个具体的界面分支。 */
const SEMANTIC_USERS: AdminUserSummary[] = [
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

/** 生成表的一行。 */
interface UserSeed {
  username: string
  /** 角色名数组；空数组 = 新建后还没分配角色的账号 */
  roles: string[]
  /** 创建时间 = 固定时刻减去 `daysAgo` 天（再叠一点哈希抖动），保证降序 */
  daysAgo: number
}

/**
 * 长尾账号表。
 *
 * 角色分布刻意贴近真实团队：运维最多、只读其次、发布机器人与审计零散，
 * 这样按角色筛选时每个选项都有命中（只有一个选项有数据 = 筛选项形同虚设）。
 */
const USER_SEEDS: UserSeed[] = [
  { username: 'ops-wang', roles: ['operator'], daysAgo: 3 },
  { username: 'ops-sun', roles: ['operator'], daysAgo: 4 },
  { username: 'sre-liu', roles: ['operator'], daysAgo: 5 },
  { username: 'dba-zhou', roles: ['operator', 'viewer'], daysAgo: 6 },
  { username: 'grafana-rw', roles: ['operator'], daysAgo: 7 },
  { username: 'dev-chen', roles: ['deployer'], daysAgo: 8 },
  { username: 'dev-wu', roles: ['deployer'], daysAgo: 9 },
  { username: 'ci-runner', roles: ['deployer'], daysAgo: 10 },
  { username: 'viewer-zhao', roles: ['viewer'], daysAgo: 11 },
  { username: 'viewer-qian', roles: ['viewer'], daysAgo: 12 },
  { username: 'readonly-yang', roles: ['viewer'], daysAgo: 13 },
  { username: 'grafana-ro', roles: ['viewer'], daysAgo: 14 },
  { username: 'backup-svc', roles: ['viewer'], daysAgo: 15 },
  { username: 'api-tester', roles: ['viewer'], daysAgo: 16 },
  { username: 'nightly-backup', roles: ['viewer'], daysAgo: 17 },
  { username: 'intern-he', roles: ['viewer'], daysAgo: 18 },
  { username: 'sec-audit', roles: ['auditor'], daysAgo: 19 },
  { username: 'audit-yin', roles: ['auditor'], daysAgo: 20 },
  // 第二个「未分配角色」夹具：真实里新人常有好几个，列表里同时看到两个空角色行才对
  { username: 'new-hire-zheng', roles: [], daysAgo: 21 }
]

/**
 * 账号主键。
 *
 * 沿用语义夹具的 `…b<n>` 段（`b1` ~ `b5`），从 `b6` 往后接——
 * `data/permissions.ts` 的分配夹具按同样的规则反查用户名，两边必须一致。
 */
function userIdOf(index: number): string {
  return `00000000-0000-0000-0000-0000000000b${(index + 6).toString(16)}`
}

/** 确定性时间抖动：让创建时间不至于全部落在整点整日上（不超过 1 小时）。 */
function jitterOf(username: string): number {
  let hash = 0x811c9dc5

  for (let index = 0; index < username.length; index += 1) {
    hash ^= username.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193) >>> 0
  }

  return (hash >>> 0) % 3600
}

const USER_EPOCH = Date.parse('2026-10-16T09:12:00Z')

const GENERATED_USERS: AdminUserSummary[] = USER_SEEDS.map((seed, index) => ({
  id: userIdOf(index),
  username: seed.username,
  roles: [...seed.roles],
  creationTime: new Date(USER_EPOCH - (seed.daysAgo * 24 + jitterOf(seed.username) / 3600) * 3600 * 1000).toISOString()
}))

/** 默认场景的用户列表（顺序即服务端顺序：创建时间降序）。 */
export const MOCK_USERS_DEFAULT: AdminUserSummary[] = [...SEMANTIC_USERS, ...GENERATED_USERS]

/** mock 里「当前登录用户」的用户名，与 auth mock 的登录账号一致。 */
export const MOCK_CURRENT_USERNAME = 'admin'

/** mock 里 admin 账号的口令（仅用于验证「原密码是否正确」）。 */
export const MOCK_CURRENT_PASSWORD = 'demo1234'
