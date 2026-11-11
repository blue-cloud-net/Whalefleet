import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { parseEmphasis } from '@/utils/emphasis'
import EmphasisText from '@/components/common/EmphasisText.vue'

/**
 * 词条里 `**强调**` 标记的解析与渲染。
 *
 * ## 为什么守这个
 * 界面是 `{{ t(key) }}` 直接插值——**没有 markdown 渲染器**。
 * 于是词条里写的 `**…**` 会**原样显示两个星号**给用户。
 *
 * 这个解析函数是唯一的补救点：它把标记切成「普通段 / 强调段」，
 * 由组件逐段渲染成 `<strong>`。
 */
describe('parseEmphasis', () => {
  it('没有标记时只有一段普通文字', () => {
    expect(parseEmphasis('普通的一句话')).toEqual([{ text: '普通的一句话', bold: false }])
  })

  it('把 `**…**` 切成强调段', () => {
    expect(parseEmphasis('将被清理的是**没有被任何容器使用**的镜像')).toEqual([
      { text: '将被清理的是', bold: false },
      { text: '没有被任何容器使用', bold: true },
      { text: '的镜像', bold: false }
    ])
  })

  it('开头与结尾的标记也能正确切分', () => {
    expect(parseEmphasis('**重点**其余')).toEqual([
      { text: '重点', bold: true },
      { text: '其余', bold: false }
    ])
  })

  it('支持多段强调', () => {
    expect(parseEmphasis('**一**和**二**')).toEqual([
      { text: '一', bold: true },
      { text: '和', bold: false },
      { text: '二', bold: true }
    ])
  })

  it('未配对的星号按普通文字处理，不吞掉后面的内容', () => {
    // 少写一个 `**` 是词条笔误，界面应当照原样显示出来，好让写词条的人看见它
    expect(parseEmphasis('漏了一个**星号')).toEqual([{ text: '漏了一个**星号', bold: false }])
  })

  it('空字符串得到空结果', () => {
    expect(parseEmphasis('')).toEqual([])
  })
})

describe('EmphasisText 的渲染', () => {
  it('把 `**…**` 渲染成 <strong>，而不是把星号显示出来', () => {
    const wrapper = mount(EmphasisText, {
      props: { text: '将被清理的是**没有标签**的镜像' }
    })

    // ⚠️ 核心断言：星号**不能**出现在可见文本里。
    // 出现即说明退回了「纯文本插值」，用户会看到两个孤零零的 `**`。
    expect(wrapper.text()).not.toContain('**')
    expect(wrapper.text()).toBe('将被清理的是没有标签的镜像')

    const strong = wrapper.findAll('strong')
    expect(strong).toHaveLength(1)
    expect(strong[0].text()).toBe('没有标签')
  })

  it('没有标记时按普通文本渲染，不产生 <strong>', () => {
    const wrapper = mount(EmphasisText, { props: { text: '普通的一句话' } })

    expect(wrapper.text()).toBe('普通的一句话')
    expect(wrapper.find('strong').exists()).toBe(false)
  })
})
