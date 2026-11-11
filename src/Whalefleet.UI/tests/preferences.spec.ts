import { describe, expect, it, beforeEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import {
  CONTAINER_BASIC_COLUMNS,
  CONTAINER_COLUMN_DEFAULTS,
  CONTAINER_COLUMNS,
  CONTAINER_METRIC_COLUMNS,
  MAX_PAGE_SIZE,
  PAGE_SIZE_ALL,
  PAGE_SIZE_OPTIONS,
  resolvePageSize,
  usePreferencesStore
} from '../src/stores/preferences'

/**
 * 「每页条数」偏好的单元测试。
 *
 * 重点在 `resolvePageSize`：它把界面上的「全部」（`PAGE_SIZE_ALL` = 0）换算成
 * 能发给后端的真实条数。这里的每个分支都对应一个**曾被我写错**的点——
 * 0 直接发出去会被后端 `Math.Clamp` 夹成 1（选了"全部"只看到一行）；
 * 不夹上限则会请求 150 而只拿回 100（用户以为看到了全部）。
 */
describe('resolvePageSize', () => {
  it('普通档位原样返回，与 total 无关', () => {
    expect(resolvePageSize(20, 0)).toBe(20)
    expect(resolvePageSize(50, 7)).toBe(50)
    expect(resolvePageSize(100, 999)).toBe(100)
  })

  it('「全部」换算成当前总数', () => {
    expect(resolvePageSize(PAGE_SIZE_ALL, 3)).toBe(3)
    expect(resolvePageSize(PAGE_SIZE_ALL, 57)).toBe(57)
  })

  it('空列表时「全部」至少给 1，不把 0 发出去', () => {
    // 传 0 会被后端夹成 1；与其让后端去改，不如前端就给出合法值
    expect(resolvePageSize(PAGE_SIZE_ALL, 0)).toBe(1)
  })

  it('「全部」在超过后端上限时夹到上限', () => {
    // 这是关键分支：不夹就会请求 150，后端静默返回 100，而界面上没有任何提示
    expect(resolvePageSize(PAGE_SIZE_ALL, MAX_PAGE_SIZE + 1)).toBe(MAX_PAGE_SIZE)
    expect(resolvePageSize(PAGE_SIZE_ALL, 5000)).toBe(MAX_PAGE_SIZE)
  })

  it('恰好等于上限时不夹（边界：不能用 >= 判断）', () => {
    expect(resolvePageSize(PAGE_SIZE_ALL, MAX_PAGE_SIZE)).toBe(MAX_PAGE_SIZE)
  })
})

describe('每页条数偏好 store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('默认 20', () => {
    expect(usePreferencesStore().pageSize).toBe(20)
  })

  it('接受合法档位与「全部」', () => {
    const prefs = usePreferencesStore()

    for (const size of PAGE_SIZE_OPTIONS) {
      prefs.setPageSize(size)
      expect(prefs.pageSize).toBe(size)
    }

    // 「全部」（0）是**合法**取值，不能被当成非法值打回 20
    prefs.setPageSize(PAGE_SIZE_ALL)
    expect(prefs.pageSize).toBe(PAGE_SIZE_ALL)
  })

  it('非法档位回落到默认 20', () => {
    const prefs = usePreferencesStore()

    prefs.setPageSize(37)
    expect(prefs.pageSize).toBe(20)

    // 负数不同于「全部」的 0，同样该被拒
    prefs.setPageSize(-1)
    expect(prefs.pageSize).toBe(20)
  })
})

/**
 * 「实时更新」三个开关（0.5.0）。
 *
 * 默认**全部开启**——实时能力是本版本的主卖点，默认关掉等于「装上了但没人用」。
 * 三者**互相独立**：指标订阅会占一条到守护进程的长连接，
 * 列表刷新几乎没有成本，通知则会产生 toast 打扰——代价不同，不该绑在一起。
 */
describe('实时更新偏好', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('默认全部开启', () => {
    const prefs = usePreferencesStore()

    expect(prefs.realtimeContainers).toBe(true)
    expect(prefs.realtimeStats).toBe(true)
    expect(prefs.realtimeNotifications).toBe(true)
  })

  it('三个开关互相独立', () => {
    const prefs = usePreferencesStore()

    prefs.realtimeStats = false

    expect(prefs.realtimeStats).toBe(false)
    expect(prefs.realtimeContainers).toBe(true)
    expect(prefs.realtimeNotifications).toBe(true)
  })
})

/**
 * 容器列表的列开关（0.6.0 起，0.12.0 扩到基础信息列）。
 *
 * ## 守的是什么
 * 列开关有两个容易出错、而且**都不报错**的地方：
 * ① 新增列时漏了默认值 → 旧持久化对象 hydrate 后该键是 `undefined` →
 *   `v-if="columns.image"` 为假 → **镜像列凭空消失**，而代码里写着默认显示；
 * ② `setContainerColumn` 用展开旧对象实现 → 旧版遗留的 `health` 键一直带着，
 *   用户手改进 localStorage 的野键也会一路保留。
 */
describe('容器列表列开关', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('三个分组常量拼起来正好是全部列（不多不少）', () => {
    const grouped = [...CONTAINER_BASIC_COLUMNS, ...CONTAINER_METRIC_COLUMNS]

    expect([...grouped].sort()).toEqual([...CONTAINER_COLUMNS].sort())
    // 每列都必须在默认表里有一项，否则就是「有这一列但不知道该不该显示」
    expect(Object.keys(CONTAINER_COLUMN_DEFAULTS).sort()).toEqual([...CONTAINER_COLUMNS].sort())
  })

  it('基础信息列默认显示，指标列默认关闭', () => {
    const prefs = usePreferencesStore()

    for (const column of CONTAINER_BASIC_COLUMNS) {
      expect(prefs.containerColumns[column], `${column} 应默认显示`).toBe(true)
    }

    for (const column of CONTAINER_METRIC_COLUMNS) {
      expect(prefs.containerColumns[column], `${column} 应默认关闭`).toBe(false)
    }
  })

  it('切换一列不影响其它列', () => {
    const prefs = usePreferencesStore()

    prefs.setContainerColumn('ports', false)

    expect(prefs.containerColumns.ports).toBe(false)
    expect(prefs.containerColumns.image).toBe(true)
    expect(prefs.containerColumns.cpu).toBe(false)
  })

  it('切换时丢弃旧版遗留的 health 与野键', () => {
    const prefs = usePreferencesStore()

    // 模拟旧版本持久化下来的对象：有 health，还有一个根本不存在的键
    prefs.containerColumns = {
      ...prefs.containerColumns,
      health: true,
      bogus: true
    } as typeof prefs.containerColumns

    prefs.setContainerColumn('cpu', true)

    expect(prefs.containerColumns.cpu).toBe(true)
    expect(Object.keys(prefs.containerColumns).sort()).toEqual([...CONTAINER_COLUMNS].sort())
  })

  it('旧持久化对象缺新键时回落到默认值（不能变成 undefined）', () => {
    const prefs = usePreferencesStore()

    // 模拟 0.11 及以前的对象：只有三个指标键
    prefs.containerColumns = {
      cpu: true,
      memory: false,
      network: false
    } as typeof prefs.containerColumns

    prefs.setContainerColumn('memory', true)

    // 缺的四个基础信息键必须补成默认的 true，而不是 undefined
    for (const column of CONTAINER_BASIC_COLUMNS) {
      expect(prefs.containerColumns[column], `${column} 应为 true 而不是 undefined`).toBe(true)
    }

    expect(prefs.containerColumns.cpu).toBe(true)
    expect(prefs.containerColumns.memory).toBe(true)
  })
})
