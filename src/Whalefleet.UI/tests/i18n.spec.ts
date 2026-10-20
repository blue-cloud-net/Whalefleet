import { describe, expect, it } from 'vitest'
import { createI18n } from 'vue-i18n'
import en from '../src/locales/en-US.json'
import zh from '../src/locales/zh-CN.json'

/**
 * 用**真实**的 i18n 实例把每个 key 渲染一遍。
 *
 * ## 守的是什么
 * `typecheck` / `lint` / 「中英 key 集合一致」这三道都拦不住一类错误：
 * **消息正文里的字面花括号**。vue-i18n 把 `{…}` 当占位符编译，于是
 * `JSON 对象，如 {"VERSION":"1.0"}` 这句会在**编译期**抛
 * `Message compilation error: Invalid token in placeholder: '"VERSION":"1.0"'`。
 *
 * 症状极难定位：词条文件是合法 JSON、`i18n.spec.ts` 的三条断言全绿、
 * `pnpm build` 也过——只有真正渲染到那一句时才发现，而那一处往往藏在一个
 * 默认折叠的高级选项面板里。
 *
 * 修法是把字面花括号转义成 `{'{'}` / `{'}'}`（vue-i18n 的字面量插值语法）。
 * 本用例就是防止下一次又写回去。
 */
const i18n = createI18n({
  legacy: false,
  locale: 'zh-CN',
  fallbackLocale: 'en-US',
  messages: { 'en-US': en, 'zh-CN': zh }
})

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

  it('每条词条在两种语言下都能编译（字面花括号必须转义）', () => {
    const failures: string[] = []

    for (const locale of ['zh-CN', 'en-US'] as const) {
      i18n.global.locale.value = locale

      for (const key of Object.keys(locale === 'zh-CN' ? zh : en)) {
        try {
          const text = i18n.global.t(key)

          // 解析不出来时 vue-i18n 会原样返回 key —— 那是「缺词条」而不是「有词条」
          if (text === key) failures.push(`${locale}:${key} 解析后仍是 key`)
        } catch (error) {
          failures.push(`${locale}:${key} 编译失败：${(error as Error).message}`)
        }
      }
    }

    expect(failures).toEqual([])
  })
})