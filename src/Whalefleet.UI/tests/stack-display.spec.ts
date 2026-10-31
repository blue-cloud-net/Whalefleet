import { describe, expect, it } from 'vitest'
import {
  describeStackPorts,
  stackNeedsAttention,
  stackStatusKey,
  stackTone
} from '../src/utils/stack-display'
import type { StackServicePort } from '../src/types/api'

/**
 * 编排展示辅助的单元测试。
 *
 * 这些是**纯逻辑**（不碰组件与网络），因此可以直接断言——
 * 界面上的分支也就跟着它们一起被覆盖到了。
 */
describe('编排状态映射', () => {
  it('running 是 ok，partial 是 warn（不是 down）', () => {
    // ⚠️ partial 用 warn 而不是 down：它与「已停止」长得一样的话，
    // 用户会以为项目全停了而去看「怎么启动」，而真正该做的是看哪个服务挂了。
    expect(stackTone('running')).toBe('ok')
    expect(stackTone('partial')).toBe('warn')
    expect(stackTone('stopped')).toBe('down')
    expect(stackTone('unknown')).toBe('idle')
  })

  it('状态映射到 i18n key 而不是死文案', () => {
    expect(stackStatusKey('running')).toBe('stack.status.running')
    expect(stackStatusKey('partial')).toBe('stack.status.partial')
    expect(stackStatusKey('stopped')).toBe('stack.status.stopped')
    expect(stackStatusKey('unknown')).toBe('stack.status.unknown')
  })

  it('只有 partial 需要提醒', () => {
    expect(stackNeedsAttention('partial')).toBe(true)
    expect(stackNeedsAttention('running')).toBe(false)
    expect(stackNeedsAttention('stopped')).toBe(false)
    expect(stackNeedsAttention('unknown')).toBe(false)
  })
})

describe('编排端口展示', () => {
  it('有宿主端口时展示「宿主→容器/协议」', () => {
    const ports: StackServicePort[] = [
      { containerPort: 80, protocol: 'tcp', hostPort: 8080, hostIp: '0.0.0.0' }
    ]

    expect(describeStackPorts(ports)).toBe('8080→80/tcp')
  })

  it('只暴露不映射时只显示容器端口', () => {
    const ports: StackServicePort[] = [
      { containerPort: 3306, protocol: 'tcp', hostPort: null, hostIp: null }
    ]

    expect(describeStackPorts(ports)).toBe('3306/tcp')
  })

  it('多个端口用、分隔', () => {
    const ports: StackServicePort[] = [
      { containerPort: 80, protocol: 'tcp', hostPort: 8080, hostIp: '0.0.0.0' },
      { containerPort: 443, protocol: 'tcp', hostPort: 8443, hostIp: '0.0.0.0' }
    ]

    expect(describeStackPorts(ports)).toBe('8080→80/tcp、8443→443/tcp')
  })

  it('没有端口时返回空串（界面显示占位符而不是空元素）', () => {
    expect(describeStackPorts([])).toBe('')
  })
})
