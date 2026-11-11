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
  /**
   * 当前页的具体名称（不是 i18n key）。
   *
   * ⚠️ **为什么需要它**：详情页的 `meta.title` 是「编排」这类**分类名**，
   * 而用户真正想知道的是「**哪个**编排」——
   * 面包屑只写「编排」的话，深链进来时看不出自己在哪个项目上。
   * 给了它就用它替换末项文案（末项仍不可点）。
   */
  currentLabel?: string | null
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

  // ⚠️ **具体名称优先于分类名**：给了 `currentLabel` 就用它，
  // 但**仍然保留分类名作为提示**——「编排 / shop」比「总览 / 主机 / shop」信息更全，
  // 而完全丢掉分类名会让用户不知道「shop 是什么」。
  const label =
    source.currentLabel && source.currentLabel.length > 0
      ? source.currentLabel
      : typeof source.titleKey === 'string'
        ? source.t(source.titleKey)
        : null

  // 重定向路由之类没有 title（`t(undefined)` 会抛错），因此必须判类型
  if (label !== null && source.titleKey !== 'nav.overview') {
    crumbs.push({ key: 'page', label })
  }

  return crumbs
}
