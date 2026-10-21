import { describe, expect, it, beforeEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { describe as describeEvent, useNotificationStore } from '../src/stores/notifications'
import type { ContainerDiedEvent } from '../src/types/api'

/**
 * 通知 store 的单元测试。
 *
 * 重点在**上限**与**文案口径**：
 * - 超过 20 条要丢最旧的（否则内存无限涨）；
 * - 文案只给 i18n key + 参数，**不给死文案**（要能被翻译，
 *   而且同一件事在 toast 与铃铛下拉里必须是同一句话）。
 */
function makeEvent(overrides: Partial<ContainerDiedEvent> = {}): ContainerDiedEvent {
  return {
    hostId: 'local',
    containerId: 'abc123def456',
    containerName: 'web',
    exitCode: 1,
    outOfMemory: false,
    occurredAt: '2026-10-20T10:00:00Z',
    ...overrides
  }
}

describe('通知 store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('新通知排在最前', () => {
    const store = useNotificationStore()

    store.push(makeEvent({ containerId: 'first' }))
    store.push(makeEvent({ containerId: 'second' }))

    expect(store.items[0]?.containerId).toBe('second')
    expect(store.items[1]?.containerId).toBe('first')
  })

  it('未读数随推送增加，标记已读后清零', () => {
    const store = useNotificationStore()

    store.push(makeEvent({ containerId: 'a' }))
    store.push(makeEvent({ containerId: 'b' }))
    expect(store.unread).toBe(2)
    expect(store.hasUnread).toBe(true)

    store.markAllRead()
    expect(store.unread).toBe(0)
    expect(store.hasUnread).toBe(false)
  })

  it('同一次死亡只记一条（去重）', () => {
    const store = useNotificationStore()

    // 同一主机 + 同一容器 + 同一时刻 = 同一次事故；多个通道各自上报时不能变成两条
    store.push(makeEvent())
    store.push(makeEvent())

    expect(store.items).toHaveLength(1)
    expect(store.unread).toBe(1)
  })

  it('超过 20 条时丢最旧的', () => {
    const store = useNotificationStore()

    for (let i = 0; i < 25; i += 1) {
      store.push(makeEvent({ containerId: `c${i}` }))
    }

    expect(store.items).toHaveLength(20)
    // 最新的在头部，最旧的（第 0～4 条）已经被丢掉
    expect(store.items[0]?.containerId).toBe('c24')
    expect(store.items.some((item) => item.containerId === 'c0')).toBe(false)
  })

  it('清空后列表与未读都归零', () => {
    const store = useNotificationStore()

    store.push(makeEvent())
    store.clear()

    expect(store.items).toHaveLength(0)
    expect(store.unread).toBe(0)
  })
})

describe('通知文案描述', () => {
  it('OOM 走单独的说法', () => {
    const description = describeEvent(makeEvent({ outOfMemory: true, exitCode: 137 }))

    expect(description.key).toBe('notification.oom')
    expect(description.params.name).toBe('web')
    expect(description.params.code).toBe(137)
  })

  it('退出码 0 是「已退出」而不是「异常退出」', () => {
    expect(describeEvent(makeEvent({ exitCode: 0 })).key).toBe('notification.exited')
  })

  it('其它退出码是「异常退出」', () => {
    expect(describeEvent(makeEvent({ exitCode: 2 })).key).toBe('notification.died')
  })

  it('没有容器名时退回短 ID', () => {
    const description = describeEvent(makeEvent({ containerName: '', containerId: 'abcdef0123456789' }))

    expect(description.params.name).toBe('abcdef012345')
  })
})
