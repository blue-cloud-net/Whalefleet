import { describe, expect, it } from 'vitest'
import { router } from '../src/router'
import { buildCommands, searchCommands, type SearchableCommand } from '../src/utils/commands'

/**
 * 命令面板的检索（0.0.14）。
 *
 * 与 UI 无关的部分单独测：面板的焦点 / 键盘导航很难在 jsdom 里忠实还原，
 * 而「搜得到 / 搜不到」是最容易出错也最影响体验的部分。
 */

/** 模拟组件侧做的展示文案解析：把 i18n key 换成人类可读的中英文串。 */
function withLabels(items = buildCommands()): SearchableCommand[] {
  const DISPLAY: Record<string, string> = {
    'nav.overview': '总览',
    'nav.status': '状态',
    'nav.containers': '容器',
    'nav.images': '镜像',
    'nav.volumes': '数据卷',
    'nav.networks': '网络',
    'nav.stacks': '编排',
    'nav.events': '事件',
    'nav.hosts': '主机',
    'nav.users': '用户',
    'nav.settings': '设置',
    'command.createContainer': '创建容器',
    'command.pullImage': '拉取镜像',
    'command.toggleSidebar': '折叠 / 展开侧边栏',
    'command.shortcuts': '快捷键'
  }

  return items.map((item) => {
    const displayLabel = DISPLAY[item.label] ?? item.label
    return { ...item, displayLabel, haystack: [displayLabel, ...(item.keywords ?? [])].join(' ') }
  })
}

/** 递归收集路由表里的所有 name（含 children）。 */
function collectRouteNames(route: { name?: unknown; children?: unknown[] }): string[] {
  const self = typeof route.name === 'string' ? [route.name] : []
  const children = Array.isArray(route.children)
    ? route.children.flatMap((child) => collectRouteNames(child as never))
    : []

  return [...self, ...children]
}

describe('buildCommands', () => {
  it('id 唯一（Fuse 以对象身份比对，重复 id 会让选中态错乱）', () => {
    const items = buildCommands()
    expect(new Set(items.map((i) => i.id)).size).toBe(items.length)
  })

  it('每个条目都能被触发（要么有路由，要么有动作）', () => {
    // 传入全套动作：buildCommands 默认不带任何回调，
    // 不注入的话动作类条目会看起来「无可执行」
    const noop = (): void => {}
    const items = buildCommands({
      createContainer: noop,
      pullImage: noop,
      showShortcuts: noop,
      toggleSidebar: noop
    })

    for (const item of items) {
      // 面板只认这两种触发方式；两者都没有 = 面板里点一下毫无反应
      const executable = item.action !== undefined || item.routeName !== undefined
      expect(executable, `${item.id} 既无 routeName 也无 action`).toBe(true)
    }
  })

  it('路由名在真实的路由表里都存在（防止拼错静默失败）', () => {
    // 静态 import 而非动态：routes.spec.ts 已证明这条路在测试环境里可用，
    // 而动态 import 在这里会因 vue-i18n / 路由懒加载链而超时。
    const names = new Set(router.options.routes.flatMap(collectRouteNames))

    for (const item of buildCommands()) {
      if (!item.routeName) continue
      expect(names.has(item.routeName), `${item.id} 的路由名 ${item.routeName} 不存在`).toBe(true)
    }
  })

  it('needs hostId 的条目都声明了 requiresHost（漏声明会白屏）', () => {
    for (const item of buildCommands()) {
      if (item.routeName?.startsWith('host-')) {
        expect(item.requiresHost, `${item.id} 是主机作用域但未声明 requiresHost`).toBe(true)
      }
    }
  })

  it('传入的动作被原样挂到条目上', () => {
    const createContainer = (): void => {}
    const item = buildCommands({ createContainer }).find((i) => i.id === 'act-create-container')

    expect(item?.action).toBe(createContainer)
  })

  it('管理员条目被标记，供组件按角色过滤', () => {
    const items = buildCommands()
    expect(items.find((i) => i.id === 'nav-users')?.requiresAdmin).toBe(true)
    expect(items.find((i) => i.id === 'nav-overview')?.requiresAdmin).toBeUndefined()
  })
})

describe('searchCommands', () => {
  const items = withLabels()

  it('空查询原样返回（顺序稳定，不被 Fuse 打乱）', async () => {
    const first = await searchCommands(items, '')
    const second = await searchCommands(items, '   ')

    expect(first.map((i) => i.id)).toEqual(items.map((i) => i.id).slice(0, first.length))
    expect(second.map((i) => i.id)).toEqual(first.map((i) => i.id))
  })

  it('中文能命中', async () => {
    const hits = await searchCommands(items, '镜像')
    expect(hits[0]?.id).toBe('nav-images')
  })

  it('英文关键词也能命中（界面是中文时用户仍会敲 docker 命令）', async () => {
    const hits = await searchCommands(items, 'pull')
    expect(hits.map((i) => i.id)).toContain('act-pull-image')
  })

  it('片段匹配而非前缀匹配', async () => {
    // 「image」应命中「创建容器」这类把 image 放在 keywords 里的条目，
    // 而不是要求它出现在开头
    const hits = await searchCommands(items, 'image')
    expect(hits.length).toBeGreaterThan(0)
  })

  it('中文短查询（单字）不返回空面板', async () => {
    // 阈值放宽的核心目的：Fuse 默认阈值下「镜」这类查询会一无所获
    const hits = await searchCommands(items, '镜')
    expect(hits.length).toBeGreaterThan(0)
  })

  it('完全无关的查询返回空列表（面板会显示「无结果」）', async () => {
    const hits = await searchCommands(items, 'zzzzqqqqxxxx')
    expect(hits).toHaveLength(0)
  })

  it('遵守 limit 上限', async () => {
    const hits = await searchCommands(items, '', 3)
    expect(hits).toHaveLength(3)
  })
})
