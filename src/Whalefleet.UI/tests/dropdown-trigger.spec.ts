import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { defineComponent } from 'vue'
import ElementPlus from 'element-plus'

/**
 * `el-dropdown` 的触发器结构（0.7.0）。
 *
 * ## 为什么要守这个
 * Element Plus 的 `el-dropdown` 把**默认插槽**交给 `ElOnlyChild`：
 * 它取插槽里的**第一个节点**，`cloneVNode` 之后挂上 EP 的 forwardRef **运行时指令**，
 * 靠指令的 `mounted` 钩子把真正的 DOM 元素交回给下拉（`useForwardRef`）。
 *
 * 若那个节点是一个**组件**且**根节点不是单一元素**（fragment），指令就挂不上去：
 *
 * > Runtime directive used on component with non-element root node.
 * > The directives will not function as intended.
 *
 * 指令不生效 → 下拉拿不到触发元素 → **点击触发器菜单永远打不开**。
 * 症状极具迷惑性：按钮在、样式正常、**没有任何报错**。
 *
 * ⚠️ **jsdom 复现不出「菜单打不开」本身**（渲染与属性透传与浏览器不一致），
 * 所以这里守两件**确定**的事：
 * 1. 触发器的根节点是**单一元素**（组件根是 fragment 就一定会触发那条告警）；
 * 2. 那条告警**不出现**。
 *
 * ## 具体踩过的坑
 * `IconActionButton` 的根是 `el-tooltip`（→ `ElPopper`，多根 fragment），
 * 因此它**不能**直接当 `el-dropdown` 的触发器——必须换成根为 `<button>` 的
 * `el-button`，tooltip 改挂按钮**内部的图标**上。
 */
describe('el-dropdown 的触发器', () => {
  let warnings: string[] = []
  let warnSpy: ReturnType<typeof vi.spyOn>

  beforeEach(() => {
    warnings = []
    warnSpy = vi.spyOn(console, 'warn').mockImplementation((...args) => {
      warnings.push(args.map((arg) => String(arg)).join(' '))
    })
  })

  afterEach(() => {
    warnSpy.mockRestore()
  })

  /** EP 的 forwardRef 指令挂错节点时的告警。 */
  const DIRECTIVE_ON_COMPONENT = /Runtime directive used on component with non-element root node/

  /**
   * 造一个与容器列表「更多操作」下拉同构的最小复现。
   *
   * @param wrapTrigger 触发器是否是一个「根为 fragment 的组件」（错误写法）
   */
  const makeSubject = (wrapTrigger: boolean) =>
    defineComponent({
      components: { ElDropdown: ElementPlus.ElDropdown, ElTooltip: ElementPlus.ElTooltip, ElButton: ElementPlus.ElButton },
      template: `
        <el-dropdown trigger="click">
          ${
            wrapTrigger
              ? '<el-tooltip content="提示"><button type="button" aria-label="更多操作">X</button></el-tooltip>'
              : '<el-button text circle size="small" aria-label="更多操作"><el-tooltip content="提示"><span>X</span></el-tooltip></el-button>'
          }
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item>菜单项</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      `
    })

  function mountSubject(wrapTrigger: boolean) {
    return mount(makeSubject(wrapTrigger), {
      global: { plugins: [ElementPlus] },
      attachTo: document.body
    })
  }

  it('触发器是根为 fragment 的组件时，Vue 会告警（指令挂错节点）', () => {
    const broken = mountSubject(true)
    broken.unmount()

    expect(
      warnings.filter((line) => DIRECTIVE_ON_COMPONENT.test(line)).length,
      '错误写法应当触发「指令挂到组件上」的告警'
    ).toBeGreaterThan(0)
  })

  it('触发器是 el-button 时不应触发该告警', () => {
    const ok = mountSubject(false)
    ok.unmount()

    expect(
      warnings.filter((line) => DIRECTIVE_ON_COMPONENT.test(line)),
      '正确写法不应触发该告警'
    ).toEqual([])
  })

  it('触发器的根节点是单一元素（下拉能拿到 DOM 触发器）', () => {
    const ok = mountSubject(false)

    // el-dropdown 会给触发元素加上 `role="button"`；
    // 能加上就说明下拉确实拿到了那个 DOM 元素。
    expect(ok.find('[role="button"]').exists()).toBe(true)
    ok.unmount()
  })
})
