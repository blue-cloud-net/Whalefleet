import { describe, expect, it } from 'vitest'
import {
  CONTAINER_TONE_CHIP,
  CONTAINER_TONE_LABEL_KEY,
  containerTone,
  formatBytes,
  formatPort
} from '../src/utils/container-status'

/**
 * 容器状态映射与格式化。
 *
 * 这几条是「看一眼就知道对不对」的逻辑，但正是它们最容易在改动中被悄悄改错：
 * 颜色错一位（暂停被染成红色）只有真看到界面才会发现，而那时已经在走查流程里了。
 */
describe('containerTone', () => {
  it('运行中 → ok，已停止 / 已死亡 → down', () => {
    expect(containerTone('running')).toBe('ok')
    expect(containerTone('exited')).toBe('down')
    expect(containerTone('dead')).toBe('down')
  })

  it('过渡态独立成一档，不落进 ok 或 down', () => {
    // paused 的语义是「既没正常运行、也没出错退出」——
    // 归进 ok 会让用户以为服务在跑，归进 down 会让他去查日志。
    expect(containerTone('paused')).toBe('warn')
    expect(containerTone('restarting')).toBe('warn')
    expect(containerTone('removing')).toBe('warn')
  })

  it('大小写不敏感（守护进程的措辞不能假定）', () => {
    expect(containerTone('RUNNING')).toBe('ok')
    expect(containerTone('Paused')).toBe('warn')
  })

  it('未登记与空值一律「未知」，不猜颜色', () => {
    expect(containerTone('some-future-state')).toBe('idle')
    expect(containerTone(undefined)).toBe('idle')
    expect(containerTone(null)).toBe('idle')
    expect(containerTone('')).toBe('idle')
  })

  it('每一档都有文案 key 与配色（漏一个就会渲染出空白药丸）', () => {
    for (const tone of ['ok', 'down', 'warn', 'idle'] as const) {
      expect(CONTAINER_TONE_LABEL_KEY[tone]).toBeTruthy()
      expect(CONTAINER_TONE_CHIP[tone]).toBeTruthy()
    }
  })
})

describe('formatPort', () => {
  it('有宿主端口时同时给出两侧', () => {
    expect(formatPort({ privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' })).toBe(
      '0.0.0.0:8080→80/tcp'
    )
  })

  it('没有绑定地址时不显示 0.0.0.0（那只是「没指定」）', () => {
    expect(formatPort({ privatePort: 80, publicPort: 8080, type: 'tcp', ip: null })).toBe(
      '8080→80/tcp'
    )
  })

  it('只暴露不绑定时只给容器侧那一半', () => {
    // 给成 `→80/tcp` 会让人以为宿主上也有个端口，进而以为访问不通是别的原因。
    expect(formatPort({ privatePort: 443, publicPort: null, type: 'tcp', ip: null })).toBe('443/tcp')
  })

  it('保留协议', () => {
    expect(formatPort({ privatePort: 53, publicPort: 53, type: 'udp', ip: null })).toBe('53→53/udp')
  })
})

describe('formatBytes', () => {
  const units = { b: 'B', kib: 'KiB', mib: 'MiB', gib: 'GiB' }

  it('按量级选单位', () => {
    expect(formatBytes(512, units)).toBe('512 B')
    expect(formatBytes(2048, units)).toBe('2.0 KiB')
    expect(formatBytes(5 * 1024 * 1024, units)).toBe('5.0 MiB')
    expect(formatBytes(3 * 1024 * 1024 * 1024, units)).toBe('3.0 GiB')
  })

  it('null / undefined 给占位符而不是 0 B', () => {
    // 「未限制」与「限制为 0 字节」是两件完全不同的事，后者根本跑不起来。
    expect(formatBytes(null, units)).toBe('—')
    expect(formatBytes(undefined, units)).toBe('—')
  })

  it('负数与 NaN 不抛异常，给占位符', () => {
    expect(formatBytes(-1, units)).toBe('—')
    expect(formatBytes(Number.NaN, units)).toBe('—')
  })
})
