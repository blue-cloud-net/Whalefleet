import { describe, expect, it } from 'vitest'
import {
  hostTone,
  HOST_TONE_CHIP,
  HOST_TONE_DOT,
  HOST_TONE_LABEL_KEY,
  HOST_TONE_RAIL
} from '../src/utils/host-status'

/**
 * 主机状态语义映射。
 *
 * 这组映射早先散在 StatusDot / HostCard / HostSelector 三处各写一遍，
 * 已经漂移过一次（有的带 dark 变体、有的没有）。收口成纯函数后，
 * 「三态必须一一对应、且各色调的文案与配色都不缺项」这件事就能用单测钉住。
 */
describe('hostTone', () => {
  it('已登记的状态映射到对应色调', () => {
    expect(hostTone('online')).toBe('ok')
    expect(hostTone('offline')).toBe('down')
  })

  it('未登记、空值与非字符串一律当「未知」，不抛错', () => {
    expect(hostTone('unknown')).toBe('idle')
    expect(hostTone(undefined)).toBe('idle')
    expect(hostTone(null)).toBe('idle')
    expect(hostTone('')).toBe('idle')
    expect(hostTone('ONLINE')).toBe('idle')
  })
})

describe('三色调的派生数据', () => {
  const tones = ['ok', 'down', 'idle'] as const

  it('每个色调都有文案 key', () => {
    for (const tone of tones) {
      expect(HOST_TONE_LABEL_KEY[tone]).toBeTruthy()
    }
  })

  it('文案 key 指向的 i18n 条目真实存在（防止拼错 key 静默回落）', () => {
    // key 用 host.status.* 这一族，便于一眼看出约定
    for (const tone of tones) {
      expect(HOST_TONE_LABEL_KEY[tone]).toMatch(/^host\.status\./)
    }
  })

  it('每个色调都有药丸、状态点与色条三套配色', () => {
    for (const tone of tones) {
      expect(HOST_TONE_CHIP[tone]).toBeTruthy()
      expect(HOST_TONE_DOT[tone]).toBeTruthy()
      expect(HOST_TONE_RAIL[tone]).toBeTruthy()
    }
  })

  it('配色使用语义令牌而非写死的 gray/green/red', () => {
    for (const tone of tones) {
      expect(HOST_TONE_CHIP[tone]).toContain('status-')
      expect(HOST_TONE_DOT[tone]).toContain('status-')
      expect(HOST_TONE_RAIL[tone]).toContain('status-')
    }
  })

  it('药丸配色带透明度修饰符，可随主题色与明暗微调', () => {
    expect(HOST_TONE_CHIP.ok).toContain('/60')
    expect(HOST_TONE_CHIP.down).toContain('/60')
    expect(HOST_TONE_CHIP.idle).toContain('/60')
  })
})