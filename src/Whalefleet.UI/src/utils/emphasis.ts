/**
 * 词条正文里的 `**强调**` 标记 → 可渲染的分段（纯函数，无 vue 依赖，可脱离组件单测）。
 *
 * ## 为什么需要它
 * 词条用 `**…**` 表达「这一句要加粗」，但界面是 `{{ t(key) }}` 直接插值——
 * **没有任何 markdown 渲染器**，于是那两个星号会**原样显示给用户**。
 * 之前只有 4 条词条用到这个约定，且都在同一处（清理对话框），所以没人发现。
 *
 * ## 为什么不用 v-html
 * `v-html` 能一次渲染 `<strong>`，但它把词条变成了 HTML 输入：
 * 词条来自仓库里的 JSON，而**用户可见的提示语里可能混入尖括号或引号**——
 * 一旦有人写了 `'<b>'` 之类的东西，界面就跟着渲染成了标签。
 * 分段渲染没有这个问题：每段都是纯文本，只有「要不要加粗」这一个布尔值。
 */

/** 一段文字及其是否强调。 */
export interface EmphasisSegment {
  /** 该段的文字 */
  text: string
  /** 是否为 `**…**` 包裹的强调段 */
  bold: boolean
}

/**
 * 匹配 `**…**`。
 *
 * ⚠️ **内容不允许再含 `*`**：否则 `**a*b**` 会在 `a*b` 中间断开，
 * 剩下的 `*` 又落回普通段——排版上不可预期。正文里需要强调的词组
 * 不会含星号，因此这条限制没有实际代价。
 */
const EMPHASIS_PATTERN = /\*\*([^*]+)\*\*/g

/**
 * 把带 `**强调**` 标记的正文切成片段。
 *
 * @param source 词条正文
 * @returns 按出现顺序排列的片段；没有标记时只有一段普通文字
 *
 * ## 未配对的 `**` 怎么办
 * 当成**普通文字**处理（而不是丢掉或吞掉后面的内容）——
 * 少一个星号是词条笔误，界面应当照原样显示出来，好让写词条的人看见它。
 */
export function parseEmphasis(source: string): EmphasisSegment[] {
  const segments: EmphasisSegment[] = []
  let cursor = 0

  for (const match of source.matchAll(EMPHASIS_PATTERN)) {
    const start = match.index
    if (start > cursor) {
      segments.push({ text: source.slice(cursor, start), bold: false })
    }
    segments.push({ text: match[1] ?? '', bold: true })
    cursor = start + match[0].length
  }

  if (cursor < source.length) {
    segments.push({ text: source.slice(cursor), bold: false })
  }

  return segments
}
