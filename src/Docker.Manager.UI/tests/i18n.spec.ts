import { describe, expect, it } from 'vitest'
import en from '../src/locales/en-US.json'
import zh from '../src/locales/zh-CN.json'

describe('i18n 词条一致性', () => {
  it('中英文 key 集合完全一致（缺失时回退英文的前提）', () => {
    const enKeys = Object.keys(en).sort()
    const zhKeys = Object.keys(zh).sort()
    expect(zhKeys).toEqual(enKeys)
  })

  it('所有 key 都是 dotted 命名（不含首尾分隔符）', () => {
    for (const key of Object.keys(en)) {
      expect(key.startsWith('.')).toBe(false)
      expect(key.endsWith('.')).toBe(false)
      expect(key).not.toContain('..')
    }
  })

  it('中英文值均非空', () => {
    for (const [k, v] of Object.entries(en)) {
      expect(v, `en:${k}`).toBeTruthy()
    }
    for (const [k, v] of Object.entries(zh)) {
      expect(v, `zh:${k}`).toBeTruthy()
    }
  })
})