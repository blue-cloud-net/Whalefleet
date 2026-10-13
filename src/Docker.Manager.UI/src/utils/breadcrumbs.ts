import type { RouteLocationRaw } from 'vue-router'

/**
 * 顶栏面包屑的层级推导。
 *
 * 做成纯函数（而不是写在组件里）有实际好处：面包屑的规则是"几步 + 每个链接指向哪"，
 * 这类逻辑用组件测试来守需要拉起 router / i18n / Element Plus 一整套，
 * 而在这里只要给几个字段就能断言——顺带也让"末项不可点"这种细节有了明确的落点。
 */

/** 面包屑的一级。 */
export interface Crumb {
  /** 稳定标识，供 `v-for` 的 key 使用 */
  key: string
  label: string
  /** 末项不带 `to`：当前页不该可点 */
  to?: RouteLocationRaw
}

/** 推导输入：只取路由 meta 与当前主机，与 Vue 实例无关。 */
export interface BreadcrumbSource {
  /** 路由 `meta.title`（i18n key）；**非字符串视为无标题**（重定向路由等） */
  titleKey: unknown
  /** 是否处于主机作用域（`meta.scope === 'host'`） */
  isHostScope: boolean
  /** 当前主机；未选中或未知时为 `null` */
  currentHost: { id: string; name: string } | null
  /** i18n 翻译函数 */
  t: (key: string) => string
}

/**
 * 由路由派生面包屑层级。
 *
 * - 全局作用域：`总览 / {当前页}`
 * - 主机作用域：`总览 / {主机名} / {当前页}`（主机名链接到该主机的概览页）
 *
 * 总览页本身只有一项（它就是根），此时等价于纯标题——刻意不为凑级数硬加一层。
 *
 * @param source 路由与主机信息。
 * @returns 面包屑层级；最后一项不带 `to`。
 */
export function buildCrumbs(source: BreadcrumbSource): Crumb[] {
  const crumbs: Crumb[] = [
    { key: 'overview', label: source.t('nav.overview'), to: { name: 'overview' } }
  ]

  if (source.isHostScope && source.currentHost) {
    crumbs.push({
      key: `host:${source.currentHost.id}`,
      label: source.currentHost.name,
      to: { name: 'host-dashboard', params: { hostId: source.currentHost.id } }
    })
  }

  // 重定向路由之类没有 title（`t(undefined)` 会抛错），因此必须判类型
  if (typeof source.titleKey === 'string' && source.titleKey !== 'nav.overview') {
    crumbs.push({ key: 'page', label: source.t(source.titleKey) })
  }

  return crumbs
}
