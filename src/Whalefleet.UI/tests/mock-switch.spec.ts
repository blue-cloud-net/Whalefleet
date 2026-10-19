/**
 * @vitest-environment node
 */
import { describe, expect, it } from 'vitest'
import { isMockRuntimeEnabled, shouldUseMock } from '../src/mocks/switch'
import { resolveScenario, MOCK_SCENARIOS, hostsFor } from '../src/mocks/scenarios'
import { MOCK_CONTAINERS_DEFAULT } from '../src/mocks/data/containers'

/**
 * mock 开关与场景解析。
 *
 * 这组逻辑的坑在于**文档与实现很容易对不上**：
 * README 写了「URL 加 `?mock=<场景>` 即可」，而早先的实现只认 `?mock=on`，
 * 场景名会被当成未开启——不写测试就只能靠人肉发现。
 */
describe('shouldUseMock', () => {
  it('环境变量为 true 时开启', () => {
    expect(shouldUseMock('true', null, '')).toBe(true)
  })

  it('URL 为 ?mock=on 时开启', () => {
    expect(shouldUseMock(undefined, null, '?mock=on')).toBe(true)
  })

  it('URL 为 ?mock=<场景名> 时同样开启（每个场景都要认）', () => {
    for (const s of MOCK_SCENARIOS) {
      expect(shouldUseMock(undefined, null, `?mock=${s}`), `场景 ${s} 未被识别为开启`).toBe(true)
    }
  })

  it('localStorage 开关为 on 时开启', () => {
    expect(shouldUseMock(undefined, 'on', '')).toBe(true)
  })

  it('无任何开关时关闭（默认不 mock，避免静默假数据）', () => {
    expect(shouldUseMock(undefined, null, '')).toBe(false)
    expect(shouldUseMock('false', null, '')).toBe(false)
  })

  it('非法的 query 值不当成开启（输错一个字符不该悄悄开 mock）', () => {
    expect(shouldUseMock(undefined, null, '?mock=nope')).toBe(false)
    expect(shouldUseMock(undefined, null, '?mock=1')).toBe(false)
  })
})

describe('isMockRuntimeEnabled', () => {
  it('URL 优先于 localStorage：URL 关闭、storage 开启时仍开启', () => {
    expect(isMockRuntimeEnabled('on', '')).toBe(true)
  })

  it('storage 为其他值时不开启', () => {
    expect(isMockRuntimeEnabled('off', '')).toBe(false)
    expect(isMockRuntimeEnabled(null, '')).toBe(false)
  })
})

describe('resolveScenario', () => {
  it('query 优先于环境变量', () => {
    expect(resolveScenario('?mock=counts', 'empty')).toBe('counts')
  })

  it('query 非法时回退环境变量', () => {
    expect(resolveScenario('?mock=bogus', 'error')).toBe('error')
  })

  it('两者都无/都非法时回退 default', () => {
    expect(resolveScenario('', undefined)).toBe('default')
    expect(resolveScenario('?mock=bogus', 'also-bogus')).toBe('default')
  })
})

describe('hostsFor', () => {
  it('default 场景含 3 台主机，且 staging-01 离线且无 version/counts', () => {
    const hosts = hostsFor('default')
    expect(hosts).toHaveLength(3)
    const staging = hosts.find((h) => h.id === 'staging-01')
    expect(staging?.status).toBe('offline')
    expect(staging?.version).toBeUndefined()
    expect(staging?.counts).toBeUndefined()
  })

  it('empty 场景返回空数组（而非 null，避免调用方判空分支）', () => {
    expect(hostsFor('empty')).toEqual([])
  })

  it('counts 场景带出资源计数', () => {
    // 与 `mock-contract` 的同名用例互为镜像：这里断言「有值」，那边断言「与列表一致」
    const counts = hostsFor('counts').find((h) => h.id === 'local')?.counts
    expect(counts?.containers).toBe(MOCK_CONTAINERS_DEFAULT.length)
    expect(counts?.containers).toBeGreaterThan(0)
  })

  it('disabled 场景含一台 enabled=false 的主机（用于 1302 分支）', () => {
    const disabled = hostsFor('disabled').find((h) => h.id === 'retired-01')
    expect(disabled?.enabled).toBe(false)
  })
})