import { describe, expect, it } from 'vitest'
import { EMPTY, formatDateTime, formatDuration, formatDurationFrom } from '../src/utils/datetime'

const UNITS = { day: '天', hour: '小时', minute: '分', second: '秒' }

describe('formatDuration', () => {
  it('最多两级单位', () => {
    const ms = (d: number, h: number, m: number, s: number) =>
      ((d * 24 + h) * 60 + m) * 60 * 1000 + s * 1000

    expect(formatDuration(ms(3, 4, 12, 5), UNITS)).toBe('3 天 4 小时')
    expect(formatDuration(ms(0, 2, 12, 5), UNITS)).toBe('2 小时 12 分')
    expect(formatDuration(ms(0, 0, 5, 0), UNITS)).toBe('5 分')
    expect(formatDuration(ms(0, 0, 0, 8), UNITS)).toBe('8 秒')
  })

  it('第二级为 0 时只显示一级', () => {
    const day = 24 * 60 * 60 * 1000
    expect(formatDuration(day * 2, UNITS)).toBe('2 天')
  })

  it('负数 / NaN / Infinity 返回占位符', () => {
    expect(formatDuration(-1, UNITS)).toBe(EMPTY)
    expect(formatDuration(Number.NaN, UNITS)).toBe(EMPTY)
    expect(formatDuration(Number.POSITIVE_INFINITY, UNITS)).toBe(EMPTY)
  })

  it('单位文案由调用方传入（支持英文）', () => {
    const en = { day: 'd', hour: 'h', minute: 'm', second: 's' }
    const hour = 60 * 60 * 1000
    expect(formatDuration(hour, en)).toBe('1 h')
  })
})

describe('formatDurationFrom', () => {
  it('按注入的 now 计算时长', () => {
    const start = '2026-10-10T00:00:00Z'
    const now = Date.parse('2026-10-11T00:00:00Z')
    expect(formatDurationFrom(start, UNITS, now)).toBe('1 天')
  })

  it('缺失 / 非法时间戳返回占位符', () => {
    expect(formatDurationFrom(undefined, UNITS)).toBe(EMPTY)
    expect(formatDurationFrom('', UNITS)).toBe(EMPTY)
    expect(formatDurationFrom('not-a-date', UNITS)).toBe(EMPTY)
  })
})

describe('formatDateTime', () => {
  it('缺失 / 非法时间戳返回占位符', () => {
    expect(formatDateTime(undefined)).toBe(EMPTY)
    expect(formatDateTime('not-a-date')).toBe(EMPTY)
  })

  it('合法时间戳输出 YYYY-MM-DD HH:mm:ss', () => {
    const d = new Date(2026, 0, 2, 3, 4, 5)
    expect(formatDateTime(d.toISOString())).toBe('2026-01-02 03:04:05')
  })
})