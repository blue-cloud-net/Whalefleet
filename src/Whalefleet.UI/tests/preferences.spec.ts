import { describe, expect, it, beforeEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import {
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
