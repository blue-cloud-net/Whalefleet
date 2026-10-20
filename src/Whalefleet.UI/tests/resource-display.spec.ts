import { describe, expect, it } from 'vitest'
import {
  hasVolumeRefCount,
  hasVolumeUsage,
  isBuiltInNetwork,
  isNetworkRemovable,
  isValidGateway,
  isValidResourceName,
  isValidSubnet,
  isVolumeInUse,
  looksLikeIpv6,
  networkSubnetLabel
} from '../src/utils/resource-display'
import type { NetworkSummary, VolumeSummary } from '../src/types/api'

function volume(patch: Partial<VolumeSummary> = {}): VolumeSummary {
  return {
    name: 'v',
    driver: 'local',
    mountpoint: '/var/lib/docker/volumes/v/_data',
    scope: 'local',
    created: null,
    size: 0,
    refCount: 0,
    labels: {},
    ...patch
  }
}

function network(patch: Partial<NetworkSummary> = {}): NetworkSummary {
  return {
    id: 'a'.repeat(64),
    name: 'n',
    driver: 'bridge',
    scope: 'local',
    attachable: false,
    internal: false,
    subnets: [],
    gateways: [],
    labels: {},
    ...patch
  }
}

describe('数据卷用量展示', () => {
  it('size 为 null 时不展示（守护进程没给用量）', () => {
    // null 才是「不知道」，界面显示「—」
    expect(hasVolumeUsage(volume({ size: null }))).toBe(false)
  })

  it('size 为 0 时照常展示（空卷是一个确切的答案）', () => {
    // 用量来自 GET /system/df，0 是真实结果而不是「字段缺失」
    expect(hasVolumeUsage(volume({ size: 0 }))).toBe(true)
  })

  it('size 为正时展示', () => {
    expect(hasVolumeUsage(volume({ size: 1024 }))).toBe(true)
  })

  it('refCount 为 null 时不展示，为 0 时展示', () => {
    expect(hasVolumeRefCount(volume({ refCount: null }))).toBe(false)
    expect(hasVolumeRefCount(volume({ refCount: 0 }))).toBe(true)
    expect(hasVolumeRefCount(volume({ refCount: 2 }))).toBe(true)
  })

  it('refCount > 0 即视为被引用；null（未知）不算', () => {
    expect(isVolumeInUse(volume({ refCount: 1 }))).toBe(true)
    expect(isVolumeInUse(volume({ refCount: 0 }))).toBe(false)
    expect(isVolumeInUse(volume({ refCount: null }))).toBe(false)
  })
})

describe('内置网络判定', () => {
  it.each(['bridge', 'host', 'none', 'null'])('%s 是内置网络', (name) => {
    expect(isBuiltInNetwork(network({ name }))).toBe(true)
    expect(isNetworkRemovable(network({ name }))).toBe(false)
  })

  it('自定义网络可删除', () => {
    expect(isNetworkRemovable(network({ name: 'app-net' }))).toBe(true)
  })

  it('Docker 26 把 null 改名成 none，两者都要认', () => {
    // 只认一个的话，新旧机器上各有一个内置网络会漏掉而被误删入口
    expect(isBuiltInNetwork(network({ name: 'none' }))).toBe(true)
    expect(isBuiltInNetwork(network({ name: 'null' }))).toBe(true)
  })
})

describe('子网展示', () => {
  it('多个子网用间隔号连接', () => {
    expect(networkSubnetLabel(network({ subnets: ['172.18.0.0/16', '10.0.0.0/8'] }))).toBe(
      '172.18.0.0/16 · 10.0.0.0/8'
    )
  })

  it('没有 IPAM 时显示占位符而不是空串', () => {
    // macvlan 等自定义驱动本就没有 IPAM，那是正常状态而非缺数据
    expect(networkSubnetLabel(network({ subnets: [] }))).toBe('—')
  })

  it('按子网推断 IPv6', () => {
    expect(looksLikeIpv6(network({ subnets: ['fd00::/64'] }))).toBe(true)
    expect(looksLikeIpv6(network({ subnets: ['172.18.0.0/16'] }))).toBe(false)
    expect(looksLikeIpv6(network({ subnets: [] }))).toBe(false)
  })
})

describe('名字与网段校验（与后端同口径）', () => {
  it.each(['app-data', 'a', 'my_vol', 'my.app', 'my-app', 'A1'])('接受合法名 %s', (name) => {
    expect(isValidResourceName(name)).toBe(true)
  })

  it.each(['', '-lead', '.lead', 'has space', 'has/slash', '有中文'])(
    '拒绝非法名 %s',
    (name) => {
      expect(isValidResourceName(name)).toBe(false)
    }
  )

  it.each(['172.18.0.0/16', '10.0.0.0/8', 'fd00::/64', ''])('接受网段 %s', (subnet) => {
    expect(isValidSubnet(subnet)).toBe(true)
  })

  it.each(['172.18.0.0', '172.18.0.0/33', 'fd00::/200', 'not-an-ip/16', '999.1.1.1/16'])(
    '拒绝网段 %s',
    (subnet) => {
      expect(isValidSubnet(subnet)).toBe(false)
    }
  )

  it('网关是地址，不要求带前缀长度', () => {
    expect(isValidGateway('172.18.0.1')).toBe(true)
    expect(isValidGateway('fd00::1')).toBe(true)
    expect(isValidGateway('')).toBe(true)
    expect(isValidGateway('172.18.0')).toBe(false)
  })
})
