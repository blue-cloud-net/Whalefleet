import { describe, expect, it } from 'vitest'
import {
  COMMON_SIGNALS,
  DEFAULT_SIGNAL,
  MAX_SIGNAL_LENGTH,
  isNonTerminating,
  validateSignal
} from '../src/utils/signals'

/**
 * 容器终止信号的本地校验（0.7.0）。
 *
 * ## 为什么要单测
 * 信号名是**自由文本**（`allow-create` 的下拉框允许手打），因此本地校验必须与后端
 * `ContainerService.RequireSignal` **同口径**——两边不一致的症状是
 * 「界面放过了，后端拒了」，用户看到的是莫名其妙的「参数校验失败」。
 */
describe('信号名校验', () => {
  it('空输入视为「用默认值」', () => {
    expect(validateSignal(null)).toBeNull()
    expect(validateSignal(undefined)).toBeNull()
    expect(validateSignal('')).toBeNull()
    expect(validateSignal('   ')).toBeNull()
  })

  it('去掉首尾空白后返回', () => {
    expect(validateSignal('  SIGTERM  ')).toBe('SIGTERM')
  })

  it('含内部空白的信号非法', () => {
    expect(validateSignal('SIG KILL')).toBeNull()
    expect(validateSignal('SIG\tKILL')).toBeNull()
  })

  it('超过长度上限的信号非法', () => {
    expect(validateSignal('A'.repeat(MAX_SIGNAL_LENGTH))).toBe('A'.repeat(MAX_SIGNAL_LENGTH))
    expect(validateSignal('A'.repeat(MAX_SIGNAL_LENGTH + 1))).toBeNull()
  })
})

describe('非终止信号的判定', () => {
  it('SIGKILL / SIGTERM 属于终止类', () => {
    expect(isNonTerminating('SIGKILL')).toBe(false)
    expect(isNonTerminating('SIGTERM')).toBe(false)
  })

  it('SIG 前缀可省略，大小写不敏感', () => {
    expect(isNonTerminating('KILL')).toBe(false)
    expect(isNonTerminating('term')).toBe(false)
    expect(isNonTerminating('sigkill')).toBe(false)
  })

  it('其余信号都算「不保证终止」', () => {
    expect(isNonTerminating('SIGINT')).toBe(true)
    expect(isNonTerminating('SIGQUIT')).toBe(true)
    expect(isNonTerminating('SIGHUP')).toBe(true)
    expect(isNonTerminating('SIGUSR1')).toBe(true)
  })

  it('空信号不算非终止（还没选信号，不该先警告）', () => {
    expect(isNonTerminating(null)).toBe(false)
    expect(isNonTerminating('')).toBe(false)
  })
})

describe('常用信号清单', () => {
  it('默认信号是 SIGKILL（与 docker kill 一致）', () => {
    expect(DEFAULT_SIGNAL).toBe('SIGKILL')
  })

  it('终止类信号排在前面（非终止类靠后）', () => {
    const values = COMMON_SIGNALS.map((item) => item.value)

    expect(values.indexOf('SIGKILL')).toBeLessThan(values.indexOf('SIGTERM'))
    expect(values.indexOf('SIGTERM')).toBeLessThan(values.indexOf('SIGINT'))
  })

  it('每一项都有可翻译的 i18n key', () => {
    for (const item of COMMON_SIGNALS) {
      expect(item.labelKey, item.value).toMatch(/^container\.signal\./)
    }
  })
})
