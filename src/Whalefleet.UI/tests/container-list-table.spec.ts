import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { i18n } from '../src/locales'
import ContainerListTable from '../src/components/business/ContainerListTable.vue'
import { CONTAINER_COLUMN_DEFAULTS } from '../src/stores/preferences'
import type { ContainerStatsSample, ContainerSummary } from '../src/types/api'

/**
 * 共享的容器列表表格（0.10.0）。
 *
 * ## 为什么单独守它
 * 编排详情页的主体是**按 compose 项目过滤出来的容器列表**，
 * 它必须和容器列表页那张表**渲染得一模一样**——
 * 抄第二遍的话，容器列表页改了端口展示规则，编排详情页不会跟着改，
 * 用户在两个页面看到同一批容器的不同信息量。
 *
 * 抄第二遍的代价是「两边会漂移」，而漂移的症状是
 * 「这里显示的端口怎么和那边不一样」，几乎不可能靠读代码定位。
 *
 * 所以这里守的是**共用组件本身的行为**：
 * 状态徽标、端口逐行、服务列、行动作插槽。
 */

/** 一个最小可用的容器列表项。 */
function container(overrides: Partial<ContainerSummary> = {}): ContainerSummary {
  return {
    id: 'a1b2c3d4e5f6',
    name: 'shop-api-1',
    image: 'nginx:1.27-alpine',
    imageId: 'sha256:aa',
    command: 'nginx',
    created: '2026-10-01T08:12:33Z',
    state: 'running',
    status: 'Up 3 hours',
    ports: [{ privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' }],
    composeService: 'api',
    ...overrides
  }
}

describe('容器列表表格', () => {
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

  /**
   * 挂载表格。
   *
   * ⚠️ **必须等 `nextTick`**：`el-table` 的表体是**异步渲染**的
   * （EP 用 `doLayout` 在挂载后算列宽），不等的话 `wrapper.text()` 是空串——
   * 那会让每个断言都以「内容为空」的面貌失败，看着像组件坏了。
   */
  async function mountTable(
    props: Record<string, unknown> = {},
    slots?: Record<string, unknown>
  ) {
    const wrapper = mount(ContainerListTable, {
      props: { containers: [container()], ...props },
      slots,
      global: { plugins: [ElementPlus, i18n] }
    })

    await wrapper.vm.$nextTick()
    await Promise.resolve()
    await wrapper.vm.$nextTick()

    return wrapper
  }

  it('渲染出容器名与镜像', async () => {
    const wrapper = await mountTable()

    expect(wrapper.text()).toContain('shop-api-1')
    expect(wrapper.text()).toContain('nginx:1.27-alpine')
    wrapper.unmount()
  })

  it('状态列用徽标而不是裸文本', async () => {
    const wrapper = await mountTable()

    // 「运行中」必须来自 `ContainerStatusBadge`——裸文本状态在两个页面会长得不一样
    expect(wrapper.text()).toContain('运行中')
    wrapper.unmount()
  })

  it('端口逐行渲染并给出可点开的链接', async () => {
    const wrapper = await mountTable()

    const link = wrapper.find('a[href="http://0.0.0.0:8080"]')

    expect(link.exists()).toBe(true)
    expect(link.text()).toContain('8080')
    wrapper.unmount()
  })

  it('未映射的端口不显示（它们从宿主机外连不上）', async () => {
    const wrapper = await mountTable({
      containers: [
        container({
          ports: [
            { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' },
            { privatePort: 33060, publicPort: null, type: 'tcp', ip: null }
          ]
        })
      ]
    })

    expect(wrapper.text()).not.toContain('33060')
    wrapper.unmount()
  })

  it('服务列显示 compose 服务名', async () => {
    const wrapper = await mountTable({ showServiceColumn: true })

    // 容器名是 shop-api-1，服务名是 api——两个都要能看到
    expect(wrapper.text()).toContain('shop-api-1')
    expect(wrapper.text()).toContain('api')
    wrapper.unmount()
  })

  it('不显示服务列时不渲染它', async () => {
    const wrapper = await mountTable({ showServiceColumn: false })

    expect(wrapper.text()).not.toContain('服务')
    wrapper.unmount()
  })

  it('不属于任何项目的容器，服务列显示占位符', async () => {
    const wrapper = await mountTable({
      showServiceColumn: true,
      containers: [container({ composeService: null })]
    })

    // 空串会渲染成一个空白格，而 null 能让它显示「—」
    expect(wrapper.text()).toContain('—')
    wrapper.unmount()
  })

  it('点容器名发出 openDetail 事件', async () => {
    const wrapper = await mountTable()

    const button = wrapper.find('button.app-truncate')
    await button.trigger('click')

    expect(wrapper.emitted('openDetail')).toBeTruthy()
    expect(wrapper.emitted('openDetail')?.[0]?.[0]).toMatchObject({ name: 'shop-api-1' })
    wrapper.unmount()
  })

  it('行动作列由插槽提供', async () => {
    const wrapper = await mountTable(
      {},
      {
        'row-actions': '<button class="probe-action">探针</button>'
      }
    )

    expect(wrapper.find('.probe-action').exists()).toBe(true)
    wrapper.unmount()
  })

  it('选择列可关闭（编排页不支持批量操作）', async () => {
    const withSelection = await mountTable({ selectable: true })
    const withoutSelection = await mountTable({ selectable: false })

    expect(withSelection.find('.el-checkbox').exists()).toBe(true)
    expect(withoutSelection.find('.el-checkbox').exists()).toBe(false)

    withSelection.unmount()
    withoutSelection.unmount()
  })

  it('渲染期没有 Vue 告警（模板引用了没导出的函数会在这里炸）', async () => {
    const wrapper = await mountTable()

    expect(warnings).toEqual([])
    wrapper.unmount()
  })

  /**
   * 列开关（0.12.0）。
   *
   * 早先只有三个指标列可关，基础信息列不可关——用户想收窄一张很宽的表时
   * 在列选择器里找不到入口（它读起来像「指标开关」而不是「显示哪些列」）。
   */
  describe('列开关', () => {
    it('关掉基础信息列就不再渲染它（连表头一起）', async () => {
      const wrapper = await mountTable({
        columns: { ...CONTAINER_COLUMN_DEFAULTS, created: false }
      })

      expect(wrapper.text()).not.toContain('创建时间')
      wrapper.unmount()
    })

    it('名称列始终渲染（它是进详情的唯一入口，不在开关里）', async () => {
      const wrapper = await mountTable({
        // 所有可开关的列全关
        columns: Object.fromEntries(
          Object.keys(CONTAINER_COLUMN_DEFAULTS).map((key) => [key, false])
        )
      })

      expect(wrapper.text()).toContain('shop-api-1')
      wrapper.unmount()
    })
  })

  /**
   * 指标单元格（0.12.0）。
   *
   * 两个问题：
   * ① 内存文案带「已用 / 上限」时会在一列里折行，整张表的行高被撑开（「挤在一堆」）；
   * ② 裸画的折线看不出上下限——纵轴当时是每行自己算的峰值。
   */
  describe('指标单元格', () => {
    /** 一份采样（旧 → 新只给两条，够画线但不必很多）。 */
    function sample(overrides: Partial<ContainerStatsSample> = {}): ContainerStatsSample {
      return {
        containerId: 'a1b2c3d4e5f6',
        cpuPercent: 12.3,
        // 160 MB / 62.5 GB：上限远大于用量，正是「挤在同一行」的那种文案
        memoryUsage: 160 * 1024 * 1024,
        memoryLimit: 62.5 * 1024 * 1024 * 1024,
        networkRxBytes: 0,
        networkTxBytes: 0,
        networkRxBytesPerSecond: 785,
        networkTxBytesPerSecond: 10,
        timestamp: '2026-10-01T08:12:40Z',
        ...overrides
      }
    }

    /** 只开一个指标列（其余基础列照默认）。 */
    function onlyMetric(metric: 'cpu' | 'memory' | 'network') {
      return {
        ...CONTAINER_COLUMN_DEFAULTS,
        cpu: metric === 'cpu',
        memory: metric === 'memory',
        network: metric === 'network'
      }
    }

    it('内存只显示已用量，上限移到 tooltip', async () => {
      const wrapper = await mountTable({
        columns: onlyMetric('memory'),
        series: { a1b2c3d4e5f6: [sample(), sample()] }
      })

      expect(wrapper.text()).toContain('160.0 MB')
      // 上限不能占列宽（它正是折行的元凶）
      expect(wrapper.text()).not.toContain('62.5 GB')

      // 但上限没丢——它在提示里
      const label = wrapper.find('[role="img"]').attributes('aria-label') ?? ''
      expect(label).toContain('62.5 GB')
      expect(label).toContain('160.0 MB')
      wrapper.unmount()
    })

    it('内存未设上限时，tooltip 明说「未设上限」而不是编一个 0', async () => {
      const wrapper = await mountTable({
        columns: onlyMetric('memory'),
        series: {
          a1b2c3d4e5f6: [sample({ memoryLimit: 0 }), sample({ memoryLimit: 0 })]
        }
      })

      const label = wrapper.find('[role="img"]').attributes('aria-label') ?? ''
      expect(label).toContain('未设上限')
      wrapper.unmount()
    })

    it('迷你图有外壳（边框 + 下沉底色）与无障碍标签', async () => {
      const wrapper = await mountTable({
        columns: onlyMetric('cpu'),
        series: { a1b2c3d4e5f6: [sample(), sample({ cpuPercent: 40 })] }
      })

      const chart = wrapper.find('[role="img"]')

      expect(chart.exists()).toBe(true)
      // 外壳：没有边框时用户不知道「框顶 = 上限、框底 = 0」
      expect(chart.classes()).toContain('border')
      expect(chart.classes()).toContain('border-line-subtle')
      // 标签必须含轴范围——外壳只能表达相对高度
      expect(chart.attributes('aria-label')).toContain('0–100%')
      wrapper.unmount()
    })

    it('没有采样时显示占位符，不画一个假的空图', async () => {
      const wrapper = await mountTable({ columns: onlyMetric('cpu'), series: {} })

      expect(wrapper.find('[role="img"]').exists()).toBe(false)
      expect(wrapper.text()).toContain('—')
      wrapper.unmount()
    })

    it('指标列关闭时一条采样也不渲染', async () => {
      const wrapper = await mountTable({
        columns: CONTAINER_COLUMN_DEFAULTS,
        series: { a1b2c3d4e5f6: [sample(), sample()] }
      })

      expect(wrapper.find('[role="img"]').exists()).toBe(false)
      wrapper.unmount()
    })
  })
})
