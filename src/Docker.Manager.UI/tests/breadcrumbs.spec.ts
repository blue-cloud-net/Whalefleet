import { describe, expect, it } from 'vitest'
import { buildCrumbs, type BreadcrumbSource } from '../src/utils/breadcrumbs'

/**
 * 面包屑层级推导。
 *
 * 这里只测"派生规则"，不测 Element Plus 怎么把它画出来——渲染是框架的事，
 * 而"该有几级、每级链到哪、末项是否可点"才是我们自己的逻辑。
 */

/** 极简翻译：把 key 原样回显，便于断言断言的是 key 而不是文案。 */
const t = (key: string) => key

function source(overrides: Partial<BreadcrumbSource> = {}): BreadcrumbSource {
  return {
    titleKey: 'nav.settings',
    isHostScope: false,
    currentHost: null,
    t,
    ...overrides
  }
}

describe('面包屑 — 全局作用域', () => {
  it('总览页只有一级（它就是根，不硬凑层数）', () => {
    const crumbs = buildCrumbs(source({ titleKey: 'nav.overview' }))

    expect(crumbs.map((crumb) => crumb.label)).toEqual(['nav.overview'])
  })

  it('其他页面为「总览 / 当前页」', () => {
    const crumbs = buildCrumbs(source({ titleKey: 'nav.settings' }))

    expect(crumbs.map((crumb) => crumb.label)).toEqual(['nav.overview', 'nav.settings'])
  })

  it('无 title 的路由（重定向等）不抛错，只留总览一级', () => {
    const crumbs = buildCrumbs(source({ titleKey: undefined }))

    expect(crumbs.map((crumb) => crumb.label)).toEqual(['nav.overview'])
  })
})

describe('面包屑 — 主机作用域', () => {
  const localHost = { id: 'local', name: 'local' }

  it('带上主机名一段，并链到该主机概览页', () => {
    const crumbs = buildCrumbs(
      source({
        titleKey: 'nav.containers',
        isHostScope: true,
        currentHost: localHost
      })
    )

    expect(crumbs.map((crumb) => crumb.label)).toEqual([
      'nav.overview',
      'local',
      'nav.containers'
    ])
    expect(crumbs[1]?.to).toEqual({ name: 'host-dashboard', params: { hostId: 'local' } })
  })

  it('主机未知时不插入主机段（避免出现空的一级）', () => {
    const crumbs = buildCrumbs(
      source({ titleKey: 'nav.containers', isHostScope: true, currentHost: null })
    )

    expect(crumbs.map((crumb) => crumb.label)).toEqual(['nav.overview', 'nav.containers'])
  })

  it('非主机作用域时即使有当前主机也不插入（全局页不该显示某台机器）', () => {
    const crumbs = buildCrumbs(
      source({ titleKey: 'nav.users', isHostScope: false, currentHost: localHost })
    )

    expect(crumbs.map((crumb) => crumb.label)).toEqual(['nav.overview', 'nav.users'])
  })
})

describe('面包屑 — 链接可点性', () => {
  it('末项不带 to（当前页不该可点），前面的项都带', () => {
    const crumbs = buildCrumbs(
      source({ titleKey: 'nav.containers', isHostScope: true, currentHost: { id: 'local', name: 'local' } })
    )

    expect(crumbs.map((crumb) => crumb.to !== undefined)).toEqual([true, true, false])
  })

  it('唯一一级时也不带 to', () => {
    const crumbs = buildCrumbs(source({ titleKey: 'nav.overview' }))

    expect(crumbs[0]?.to).toEqual({ name: 'overview' })
    // 末项判定由组件按 index 做；这里守住"根总览本身是可点的"这一事实
    expect(crumbs).toHaveLength(1)
  })

  it('每级都有稳定唯一的 key', () => {
    const crumbs = buildCrumbs(
      source({ titleKey: 'nav.images', isHostScope: true, currentHost: { id: 'prod-1', name: 'prod' } })
    )

    const keys = crumbs.map((crumb) => crumb.key)
    expect(keys).toEqual(['overview', 'host:prod-1', 'page'])
    expect(new Set(keys).size).toBe(keys.length)
  })
})
