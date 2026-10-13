import { describe, expect, it, beforeEach } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useHostStore } from '../src/stores/host'
import { MOCK_HOST_GROUPS, MOCK_HOSTS_DEFAULT } from '../src/mocks/data/hosts'
import type { HostOverview } from '../src/types/api'

beforeEach(() => {
  setActivePinia(createPinia())
  // 主机与分组都不再由 store 内置（0.0.13 起分组也来自后端），因此测试需显式铺数据。
  // 两者都要铺，否则 groupedHosts 恒为空。
  const host = useHostStore()
  host.setGroups(structuredClone(MOCK_HOST_GROUPS))
  host.setHosts([...MOCK_HOSTS_DEFAULT])
})

describe('主机 store — 作用域', () => {
  it('初始为全局作用域：currentId 为空串且 currentHost 为 null', () => {
    const host = useHostStore()
    expect(host.currentId).toBe('')
    // 不再回退到 local —— 否则 UI 会「看起来像选中了某台机器」
    expect(host.currentHost).toBeNull()
  })

  it('setCurrent 后能取到主机，clearCurrent 后回到 null', () => {
    const host = useHostStore()
    host.setCurrent('local')
    expect(host.currentHost?.name).toBe('local')
    host.clearCurrent()
    expect(host.currentHost).toBeNull()
  })

  it('未知 id 时 currentHost 为 null 且 hasCurrentHost 为 false', () => {
    const host = useHostStore()
    host.setCurrent('nope')
    expect(host.currentHost).toBeNull()
    expect(host.hasCurrentHost).toBe(false)
  })
})

describe('主机 store — 分组聚合', () => {
  it('按 sortOrder 升序排列分组', () => {
    const host = useHostStore()
    const names = host.groupedHosts.map((g) => g.group?.name)
    expect(names).toEqual(['production', 'staging', 'local'])
  })

  it('无成员的分组不出现在结果中', () => {
    const host = useHostStore()
    host.setGroups([
      ...structuredClone(MOCK_HOST_GROUPS),
      { id: 'empty-group', name: 'empty', description: null, sortOrder: 99, hostCount: 0 }
    ])
    expect(host.groupedHosts.map((g) => g.group?.name)).not.toContain('empty')
  })

  it('未分组主机恒排在末尾（group = null）', () => {
    const host = useHostStore()
    const loose: HostOverview = {
      id: 'loose-1',
      name: 'loose-1',
      type: 'tcp',
      endpoint: 'tcp://10.0.0.99:2375',
      enabled: true
    }
    host.setHosts([...MOCK_HOSTS_DEFAULT, loose])

    const last = host.groupedHosts[host.groupedHosts.length - 1]
    expect(last.group).toBeNull()
    expect(last.hosts.map((h) => h.id)).toEqual(['loose-1'])
  })

  it('groupId 指向不存在的分组时归入「未分组」', () => {
    const host = useHostStore()
    host.setHosts([
      {
        id: 'ghost',
        name: 'ghost',
        type: 'tcp',
        endpoint: 'tcp://10.0.0.98:2375',
        enabled: true,
        groupId: 'does-not-exist'
      }
    ])
    expect(host.groupedHosts).toHaveLength(1)
    expect(host.groupedHosts[0].group).toBeNull()
  })

  it('hosts 为空时 groupedHosts 也为空（总览页走空态分支）', () => {
    const host = useHostStore()
    host.setHosts([])
    expect(host.groupedHosts).toEqual([])
  })
})