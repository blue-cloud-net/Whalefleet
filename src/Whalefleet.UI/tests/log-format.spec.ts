import { describe, expect, it } from 'vitest'
import {
  formatBytes,
  formatLogTime,
  formatMemory,
  formatNetwork,
  formatPercent,
  memoryPercent
} from '../src/utils/log-format'

/**
 * 展示格式化的单元测试。
 *
 * 重点在**「不知道」不能显示成「0」**：
 * 内存上限未知时显示「0 B / 0 B」会被读成「确实没占内存」，
 * 而实际是「守护进程没设限」。因此 `formatMemory` 与 `memoryPercent`
 * 都要能把「未知」与「真的是 0」区分开。
 */
describe('formatBytes', () => {
  it('小于 1KB 用字节', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(512)).toBe('512 B')
  })

  it('按 1024 进位', () => {
    expect(formatBytes(1024)).toBe('1.0 KB')
    expect(formatBytes(1536)).toBe('1.5 KB')
    expect(formatBytes(1024 * 1024)).toBe('1.0 MB')
    expect(formatBytes(1024 * 1024 * 1024)).toBe('1.0 GB')
  })

  it('负数与非法值归零', () => {
    expect(formatBytes(-1)).toBe('0 B')
    expect(formatBytes(Number.NaN)).toBe('0 B')
  })
})

describe('formatMemory', () => {
  it('有上限时显示「已用 / 上限」', () => {
    expect(formatMemory(128 * 1024 * 1024, 512 * 1024 * 1024)).toBe('128.0 MB / 512.0 MB')
  })

  it('上限未知时只显示已用（不能显示成 0 / 0）', () => {
    expect(formatMemory(64 * 1024 * 1024, 0)).toBe('64.0 MB')
  })
})

describe('memoryPercent', () => {
  it('有上限时算出百分比', () => {
    expect(memoryPercent(512, 1024)).toBe(50)
  })

  it('上限未知时返回 null（不是 0）', () => {
    expect(memoryPercent(512, 0)).toBeNull()
  })

  it('超过上限时夹到 100', () => {
    expect(memoryPercent(2048, 1024)).toBe(100)
  })
})

describe('formatPercent', () => {
  it('保留一位小数', () => {
    expect(formatPercent(12.34)).toBe('12.3%')
  })

  it('非法值显示破折号而不是 NaN', () => {
    expect(formatPercent(Number.NaN)).toBe('—')
  })
})

describe('formatNetwork', () => {
  it('收 / 发分别标注', () => {
    expect(formatNetwork(1024 * 1024, 1024)).toBe('↓ 1.0 MB / ↑ 1.0 KB')
  })
})

describe('formatLogTime', () => {
  it('压成时:分:秒', () => {
    // 构造本地时区的固定时刻，避免测试随时区漂移
    const local = new Date(2026, 9, 12, 22, 4, 11)
    expect(formatLogTime(local.toISOString())).toBe('22:04:11')
  })

  it('没有时间戳时返回空串（不是 Invalid Date）', () => {
    expect(formatLogTime(null)).toBe('')
    expect(formatLogTime('')).toBe('')
    expect(formatLogTime('not-a-date')).toBe('')
  })
})
