import { describe, expect, it } from 'vitest'
import { router } from '../src/router'

const layoutRoute = () => router.options.routes.find((r) => r.path === '/')
const children = () => layoutRoute()?.children ?? []

describe('路由表', () => {
  it('根路径重定向到 overview（全局总览，而非某台机器的容器）', () => {
    expect(layoutRoute()?.redirect).toBe('/overview')
  })

  it('login 页标记 requiresAuth=false', () => {
    const login = router.options.routes.find((r) => r.name === 'login')
    expect(login?.meta?.requiresAuth).toBe(false)
  })

  it('users / hosts 标记 requiresAdmin', () => {
    const users = children().find((r) => r.name === 'users')
    const hosts = children().find((r) => r.name === 'hosts')
    expect(users?.meta?.requiresAdmin).toBe(true)
    expect(hosts?.meta?.requiresAdmin).toBe(true)
  })

  it('roles 与 users / hosts 同属管理组，且图标进 safelist', () => {
    const roles = children().find((r) => r.name === 'roles')

    expect(roles?.meta?.requiresAdmin).toBe(true)
    expect(roles?.meta?.scope).toBe('global')
    expect(roles?.meta?.group).toBe('manage')
    // 图标写作动态字符串，UnoCSS 扫不到，必须与路由一起进 safelist
    // （`uno.config.ts` 的 collectRouteIcons 自动收集）
    expect(roles?.meta?.icon).toBe('i-carbon-user-role')
  })

  it('管理组菜单顺序：主机 → 用户 → 角色 → Registry（日常动线是「看人再建角色」）', () => {
    const manage = children()
      .filter((r) => r.meta?.scope !== 'host' && r.meta?.group === 'manage')
      .map((r) => r.name)

    // Registry 排在末尾：管理组前三项是「人」的动线（看主机 → 看人 → 缺角色再建），
    // Registry 是纯基础设施配置、一配就不动，插在中间会打断日常动线。
    expect(manage).toEqual(['hosts', 'users', 'roles', 'registries'])
  })

  it('registries 用 registry.read 权限而非 requiresAdmin 把门', () => {
    const registries = children().find((r) => r.name === 'registries')

    expect(registries?.meta?.requiresPermission).toBe('registry.read')
    // ⚠️ 不能同时标 requiresAdmin：后端这道门只看 registry.read，
    // 自定义角色可能只带它；叠加管理面要求会比后端更严，把人挡在自己的页外。
    expect(registries?.meta?.requiresAdmin).toBeUndefined()
    expect(registries?.meta?.scope).toBe('global')
    expect(registries?.path).toBe('registries')
  })

  it('全局作用域：overview 与 settings 存在且 scope=global', () => {
    const overview = children().find((r) => r.name === 'overview')
    const settings = children().find((r) => r.name === 'settings')
    expect(overview?.meta?.scope).toBe('global')
    expect(settings?.meta?.scope).toBe('global')
  })

  it('settings 自成一组，且不是 admin 限定项', () => {
    const settings = children().find((r) => r.name === 'settings')
    expect(settings?.meta?.group).toBe('settings')
    // 非 admin 也得能改外观与偏好 —— 这一项若标了 requiresAdmin，
    // 普通用户看到的侧边栏将只剩「总览」，且没有任何分组标题。
    expect(settings?.meta?.requiresAdmin).toBeUndefined()
  })

  it('settings 声明在管理组之后（侧边栏顺序直接取路由表声明顺序）', () => {
    const names = children().filter((r) => r.meta?.scope !== 'host').map((r) => r.name)
    expect(names.indexOf('settings')).toBeGreaterThan(names.indexOf('roles'))
  })

  it('主机作用域：7 个资源路由均为 scope=host 且路径带 :hostId', () => {
    const hostRoutes = [
      'host-status',
      'host-containers',
      'host-images',
      'host-volumes',
      'host-networks',
      'host-stacks',
      'host-events'
    ]
    for (const name of hostRoutes) {
      const r = children().find((x) => x.name === name)
      expect(r, `缺少路由 ${name}`).toBeTruthy()
      expect(r?.meta?.scope, `${name} 作用域`).toBe('host')
      expect(r?.path, `${name} 路径`).toContain(':hostId')
    }
  })

  it('主机菜单顺序：状态 → 编排 → 容器 → 镜像 → 数据卷 → 网络 → 事件', () => {
    // 只取真正进侧边栏的条目：纯重定向路由与概览页均标记 showInSidebar=false
    //
    // 「编排」排在容器之前是按**用户动线**而非实现节奏：Compose 项目的日常操作是
    // 「看编排 → 看它拉起的容器 / 挂载的卷 / 接入的网络」，从编排往下点最顺手。
    // 实现节奏（后端 0.7.0）只影响功能何时就绪，不占用菜单的排列位置。
    const order = children()
      .filter((r) => r.meta?.scope === 'host' && r.meta?.showInSidebar !== false)
      .map((r) => r.name)
    expect(order).toEqual([
      'host-status',
      'host-stacks',
      'host-containers',
      'host-images',
      'host-volumes',
      'host-networks',
      'host-events'
    ])
  })

  it('主机概览页与重定向路由不进侧边栏', () => {
    const dashboard = children().find((r) => r.name === 'host-dashboard')
    const redirect = children().find((r) => r.path === 'hosts/:hostId')
    expect(dashboard?.meta?.showInSidebar).toBe(false)
    expect(redirect?.meta?.showInSidebar).toBe(false)
  })

  it('每个侧边栏路由都有 i18n title（否则 t(undefined) 会抛错）', () => {
    for (const r of children()) {
      if (r.meta?.showInSidebar === false) continue
      expect(typeof r.meta?.title, `${String(r.name)} 缺 title`).toBe('string')
    }
  })

  it('hosts/:hostId 缺省重定向到该主机的 dashboard', () => {
    const base = children().find((r) => r.path === 'hosts/:hostId')
    const redirect = base?.redirect as (to: { params: Record<string, string> }) => unknown
    expect(typeof redirect).toBe('function')
    expect(redirect({ params: { hostId: 'local' } })).toEqual({
      name: 'host-dashboard',
      params: { hostId: 'local' }
    })
  })

  it('两种作用域的菜单集合互不相交（选择/未选择机器菜单不同）', () => {
    const globalNames = children()
      .filter((r) => r.meta?.scope !== 'host')
      .map((r) => r.name)
    const hostNames = children()
      .filter((r) => r.meta?.scope === 'host')
      .map((r) => r.name)
    expect(globalNames).toEqual(expect.arrayContaining(['overview', 'settings']))
    expect(hostNames).toEqual(expect.arrayContaining(['host-dashboard', 'host-containers']))
    expect(globalNames.some((n) => hostNames.includes(n))).toBe(false)
  })

  it('catch-all 兜底路由存在', () => {
    const nf = router.options.routes.find((r) => r.name === 'not-found')
    expect(nf).toBeTruthy()
  })
})