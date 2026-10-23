import { describe, expect, it } from 'vitest'
import { createI18n } from 'vue-i18n'
import en from '../src/locales/en-US.json'
import zh from '../src/locales/zh-CN.json'
import enRaw from '../src/locales/en-US.json?raw'
import zhRaw from '../src/locales/zh-CN.json?raw'

/**
 * 词条结构：**嵌套** JSON。
 *
 * ```json
 * { "container": { "health": { "healthy": "健康" } } }
 * ```
 *
 * vue-i18n 沿路径查找，代码里仍然写点号 key（`t('container.health.healthy')`），
 * 两种写法对调用方完全等价。
 *
 * 为什么不用平铺的 `{ "container.health.healthy": "健康" }`：平铺只是**看起来**更短，
 * JSON 对象里同名成员会被**后者静默覆盖**，而这种覆盖在「中英 key 集合一致」这道断言下
 * 完全不可见——实际踩过的坑是 `container.health.starting` 在同一个语言包里登记了两次，
 * 中文给「启动中」、英文给 "Checking"，同一个 key 两种语言给出不同语义。
 * 嵌套之后「重复成员名」依然可能发生（同一个对象里两个 `"healthy"`），
 * 所以守卫改为在 **JSON 原文** 上查，见下面的 `findDuplicateMemberNames`。
 */

/**
 * 把嵌套词条摊平成点号键，便于与「代码里写的 key」逐一对照。
 */
function flatten(node: unknown, prefix = ''): Record<string, string> {
  const out: Record<string, string> = {}
  if (typeof node !== 'object' || node === null) return out
  for (const [key, value] of Object.entries(node as Record<string, unknown>)) {
    const path = prefix ? `${prefix}.${key}` : key
    if (typeof value === 'string') {
      out[path] = value
    } else {
      Object.assign(out, flatten(value, path))
    }
  }
  return out
}

/**
 * 在 **JSON 原文** 里找出重复的成员名（返回它们的完整路径）。
 *
 * 为什么不能只用 `JSON.parse`：重复键在解析时就被后者覆盖，解析结果里根本看不出来。
 * 而词条文件是「JSON 合法 + 中英 key 集合一致 + `pnpm build` 通过」时，
 * 唯一还能暴露这个问题的就是原文层面的检查。
 *
 * 这里手写一个极小的 JSON 扫描器：只跟踪「对象成员名」和「路径」，不关心值。
 * 词条文件里不会出现数组（值要么是文案要么是子对象），遇到数组直接报错。
 */
function findDuplicateMemberNames(text: string): string[] {
  const duplicates: string[] = []
  let i = 0

  const skipWhitespace = (): void => {
    while (i < text.length && /\s/.test(text[i] as string)) i++
  }

  const readString = (): string => {
    i++ // 开头的引号
    let out = ''
    while (i < text.length) {
      const c = text[i] as string
      if (c === '\\') {
        out += text[i + 1]
        i += 2
        continue
      }
      i++
      if (c === '"') return out
      out += c
    }
    throw new Error('JSON 字符串未闭合')
  }

  const readValue = (path: string[]): void => {
    skipWhitespace()
    const c = text[i]
    if (c === '{') {
      readObject(path)
      return
    }
    if (c === '[') {
      throw new Error('词条文件不应包含数组')
    }
    if (c === '"') {
      readString()
      return
    }
    // number / true / false / null
    while (i < text.length && !/[,\]}\s]/.test(text[i] as string)) i++
  }

  const readObject = (path: string[]): void => {
    i++ // '{'
    const seen = new Set<string>()
    skipWhitespace()
    if (text[i] === '}') {
      i++
      return
    }
    for (;;) {
      skipWhitespace()
      const name = readString()
      if (seen.has(name)) {
        duplicates.push([...path, name].join('.'))
      }
      seen.add(name)
      skipWhitespace()
      i++ // ':'
      readValue([...path, name])
      skipWhitespace()
      if (text[i] === ',') {
        i++
        continue
      }
      i++ // '}'
      return
    }
  }

  skipWhitespace()
  if (text[i] === '{') readObject([])
  return duplicates
}

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

const enFlat = flatten(en)
const zhFlat = flatten(zh)

describe('i18n 词条一致性', () => {
  it('中英文 key 集合完全一致（缺失时回退英文的前提）', () => {
    expect(Object.keys(zhFlat).sort()).toEqual(Object.keys(enFlat).sort())
  })

  it('所有 key 都是 dotted 命名（不含首尾分隔符）', () => {
    for (const key of Object.keys(enFlat)) {
      expect(key.startsWith('.')).toBe(false)
      expect(key.endsWith('.')).toBe(false)
      expect(key).not.toContain('..')
    }
  })

  it('中英文值均非空', () => {
    for (const [k, v] of Object.entries(enFlat)) {
      expect(v, `en:${k}`).toBeTruthy()
    }
    for (const [k, v] of Object.entries(zhFlat)) {
      expect(v, `zh:${k}`).toBeTruthy()
    }
  })

  it('词条文件原文没有重复成员名（JSON.parse 会静默吞掉重复键）', () => {
    expect(findDuplicateMemberNames(enRaw)).toEqual([])
    expect(findDuplicateMemberNames(zhRaw)).toEqual([])
  })

  it('没有「叶子与子树同名」的 key（否则 t() 取到的是对象而不是文案）', () => {
    for (const [locale, flat] of [
      ['zh-CN', zhFlat],
      ['en-US', enFlat]
    ] as const) {
      const keys = new Set(Object.keys(flat))
      for (const key of keys) {
        const parts = key.split('.')
        for (let i = 1; i < parts.length; i++) {
          const prefix = parts.slice(0, i).join('.')
          expect(
            keys.has(prefix),
            `${locale}: ${key} 的前缀 ${prefix} 也是一条词条`
          ).toBe(false)
        }
      }
    }
  })

  it('每条词条在两种语言下都能编译（字面花括号必须转义）', () => {
    const failures: string[] = []

    for (const locale of ['zh-CN', 'en-US'] as const) {
      i18n.global.locale.value = locale

      for (const key of Object.keys(locale === 'zh-CN' ? zhFlat : enFlat)) {
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

  /**
   * 容器创建表单三个编辑器的提示词条曾经**根本没有登记**。
   *
   * `EnvVarEditor.vue` / `PortMappingEditor.vue` / `VolumeMountEditor.vue` 引用了
   * 下面这 6 个 key，语言包里却没有——用户填错时界面直接显示
   * `container.create.envInvalid` 这样的字面 key 路径。
   *
   * 「中英 key 集合一致」与「每条词条都能编译」这两道都拦不住它：
   * 缺的 key 在两个语言包里同时缺，集合照样一致；少一条也不会触发任何编译错误。
   * 只有真正渲染到那一句才会暴露，所以这里把 6 个 key 显式列出来守着。
   */
  it('容器创建表单三个编辑器的 6 条提示词条都已登记', () => {
    const editorKeys = [
      'container.create.envHint',
      'container.create.envInvalid',
      'container.create.portHint',
      'container.create.portInvalid',
      'container.create.volumeHint',
      'container.create.volumeInvalid'
    ]

    for (const key of editorKeys) {
      expect(zhFlat[key], `zh 缺少 ${key}`).toBeTruthy()
      expect(enFlat[key], `en 缺少 ${key}`).toBeTruthy()
    }

    // 错误提示要带行号占位符，否则「第几行错了」这个信息丢了
    for (const key of editorKeys.filter((k) => k.endsWith('Invalid'))) {
      expect(zhFlat[key], `zh:${key} 缺少 {line} 占位符`).toContain('{line}')
      expect(enFlat[key], `en:${key} 缺少 {line} 占位符`).toContain('{line}')
    }
  })
})
