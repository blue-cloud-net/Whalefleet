import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { defineComponent } from 'vue'
import ElementPlus from 'element-plus'

/**
 * `el-popover` 的 reference 插槽结构（0.6.0）。
 *
 * ## 为什么要守这个
 * Element Plus 的 `el-popover` 内部就是 `el-tooltip`，而 tooltip 的触发器用
 * `ElOnlyChild` 取插槽里的**第一个节点**：它把 EP 的 forwardRef **运行时指令**挂到
 * 那个节点上，靠指令的 `mounted` 钩子拿到真正的 DOM 元素。
 *
 * 若那个节点是一个**组件**（例如 `el-tooltip`），指令就挂在了组件上而不是 DOM
 * 元素上——Vue 会直接告警：
 *
 * > Runtime directive used on component with non-element root node.
 * > The directives will not function as intended.
 *
 * 指令不生效 → popover 拿不到触发元素 → **点击触发器永远绑不上**：菜单点不开，
 * 而 tooltip 一切正常。症状极具迷惑性（「图标能出提示，点它却没反应」）。
 *
 * ⚠️ **jsdom 复现不出点击失效本身**（渲染与属性透传在测试环境下与浏览器不一致，
 * 错误写法在 jsdom 里菜单照样能打开）。所以这里守两件**确定**的事：
 * 结构对不对，以及**有没有那条 Vue 告警**。
 */
describe('el-popover 的 reference 插槽', () => {
  let warnings: string[] = []
  let warnSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    warnings = []
    warnSpy = vi.spyOn(console, 'warn').mockImplementation((...args) => {
      warnings.push(args.map(String).join(' '))
    })
  })

  afterEach(() => {
    warnSpy.mockRestore()
  })

  /** EP 的 forwardRef 指令挂错节点时的告警。 */
  const DIRECTIVE_ON_COMPONENT = /Runtime directive used on component with non-element root node/

  /**
   * 造一个与容器列表「显示列」按钮同构的最小复现。
   *
   * @param wrapReference tooltip 是否包在 reference 槽的**最外层**（错误写法）
   */
  const makeSubject = (wrapReference: boolean) =>
    defineComponent({
      components: { ElPopover: ElementPlus.ElPopover, ElTooltip: ElementPlus.ElTooltip },
      template: `
        <el-popover placement="bottom-end" :width="200" trigger="click">
          <template #reference>
            ${
              wrapReference
                ? '<el-tooltip content="提示"><button type="button" aria-label="显示列">X</button></el-tooltip>'
                : '<button type="button" aria-label="显示列"><el-tooltip content="提示"><span>X</span></el-tooltip></button>'
            }
          </template>
          <div class="panel">面板内容</div>
        </el-popover>
      `
    })

  function mountSubject(wrapReference: boolean) {
    return mount(makeSubject(wrapReference), {
      global: { plugins: [ElementPlus] },
      attachTo: document.body
    })
  }

  it('reference 里放按钮本身时，点击能打开面板', async () => {
    const wrapper = mountSubject(false)

    const trigger = wrapper.find('button[aria-label="显示列"]')
    expect(trigger.exists()).toBe(true)

    await trigger.trigger('click')
    await wrapper.vm.$nextTick()
    await new Promise((resolve) => setTimeout(resolve, 60))

    expect(document.querySelectorAll('.panel').length).toBeGreaterThan(0)

    wrapper.unmount()
  })

  it('tooltip 的触发元素在按钮内部，而不是按钮本身', () => {
    // 错误写法：tooltip 包住按钮 → 它的触发元素就是按钮
    //   → forwardRef 指令挂到组件上，popover 拿不到触发元素，菜单永远打不开
    const broken = mountSubject(true)
    expect(
      broken.find('button[aria-label="显示列"]').find('.el-tooltip__trigger').exists()
    ).toBe(false)
    broken.unmount()

    // 正确写法：按钮是 reference，tooltip 挂在按钮内部的图标上
    const ok = mountSubject(false)
    expect(ok.find('button[aria-label="显示列"]').find('.el-tooltip__trigger').exists()).toBe(true)
    ok.unmount()
  })

  it('reference 里放组件时 Vue 会告警（forwardRef 指令挂错节点）', () => {
    const broken = mountSubject(true)
    broken.unmount()

    expect(
      warnings.filter((line) => DIRECTIVE_ON_COMPONENT.test(line)).length,
      '错误写法应当触发「指令挂到组件上」的告警'
    ).toBeGreaterThan(0)

    warnings = []
    const ok = mountSubject(false)
    ok.unmount()

    expect(
      warnings.filter((line) => DIRECTIVE_ON_COMPONENT.test(line)),
      '正确写法不应触发该告警'
    ).toEqual([])
  })
})

