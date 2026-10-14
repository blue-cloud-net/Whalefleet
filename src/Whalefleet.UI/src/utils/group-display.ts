import type { HostGroupSummary } from '@/types/api'

/**
 * 主机分组的展示逻辑（纯函数，无 vue-i18n 依赖）。
 *
 * ## 为什么内置分组走 i18n、自定义分组原样显示
 * 分组名是**管理员可改的数据**，而数据不该被程序硬翻。但三个内置分组
 * （`production` / `staging` / `local`，由迁移按英文标识种入）在界面上有个固定含义，
 * 中文界面下显示「生产环境」比显示 `production` 更好懂。
 *
 * 因此约定：**名字恰好等于这三个已知标识**时取 `host.group.<name>` 文案，
 * 其余一律原样显示用户输入。管理员把 `production` 改名成「生产」后，
 * 界面就显示「生产」——这正是他想要的，而硬翻只会覆盖掉他的选择。
 */

/** 内置分组名 → i18n key（`name` 与 `description` 各一份）。 */
const BUILT_IN_LABEL_KEYS: Record<string, string> = {
  production: 'host.group.production',
  staging: 'host.group.staging',
  local: 'host.group.local'
}

const BUILT_IN_DESCRIPTION_KEYS: Record<string, string> = {
  production: 'host.group.productionDesc',
  staging: 'host.group.stagingDesc',
  local: 'host.group.localDesc'
}

/** 未分组分组的 i18n key。 */
export const UNGROUPED_LABEL_KEY = 'host.group.ungrouped'

/** 是否是内置分组名（三个已知标识之一）。 */
export function isBuiltInGroupName(name: string): boolean {
  return Object.hasOwn(BUILT_IN_LABEL_KEYS, name)
}

/**
 * 分组的展示名。
 *
 * @param group 分组；`null` 表示「未分组」这一虚拟分组
 * @param translate i18n 的 `t` 函数（由组件传入，本模块不依赖 vue-i18n）
 */
export function groupLabel(
  group: Pick<HostGroupSummary, 'name'> | null,
  translate: (key: string) => string
): string {
  if (group === null) return translate(UNGROUPED_LABEL_KEY)

  const key = BUILT_IN_LABEL_KEYS[group.name]

  return key ? translate(key) : group.name
}

/**
 * 分组的描述。
 *
 * 后端有描述就用后端的（管理员写的说明最准确）；没有描述时，内置分组回退到
 * 内置文案，自定义分组则**不给描述**——为空的分组硬塞一句「这是某某环境」
 * 只会让界面更像模板，而不是更像数据。
 */
export function groupDescription(
  group: Pick<HostGroupSummary, 'name' | 'description'> | null,
  translate: (key: string) => string
): string | undefined {
  if (group === null) return undefined

  if (group.description && group.description.length > 0) return group.description

  const key = BUILT_IN_DESCRIPTION_KEYS[group.name]

  return key ? translate(key) : undefined
}
