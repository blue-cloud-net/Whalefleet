import { defineStore } from 'pinia'
import type { ContainerStateFilter } from '@/api/container'

/** 列表每页条数可选值（与 ui-guidelines 的分页约定一致） */
export const PAGE_SIZE_OPTIONS = [10, 20, 50, 100] as const

/**
 * 每页条数上限，**必须与后端 `ImageService.MaxPageSize` / `ContainerService.MaxPageSize` 一致**。
 *
 * 后端对 `pageSize` 做 `Math.Clamp(value, 1, MaxPageSize)`，超过的值会被静默夹紧。
 * 前端若不认这个上限，「全部」在条目超过 100 时只会拿到 100 条，
 * 而界面上没有任何地方说过这件事——用户以为看到了全部。
 * 改后端上限时这里也要改，否则「全部」的语义会与实际返回悄悄脱节。
 */
export const MAX_PAGE_SIZE = 100

/**
 * 「全部」：不分页，一页列出所有条目。
 *
 * ## 为什么用 0 而不是 `total` 或 `-1`
 * - **`total` 不行**：总数为 20 时，"全部"与"20 条/页"完全同值，两者在下拉框里无法区分，
 *   用户选了「全部」却看到显示成「20 条/页」。而一旦之后又新增到 25 条，
 *   他期望的"看到全部"会静默退化成"只看到 20 条"。
 * - **0 天然不与真实档位冲突**：后端 `Math.Clamp(pageSize, 1, 100)` 会把 0 夹成 1，
 *   也就是说 0 作为真实条数永远传不出去，它只可能表达「不限条数」这一个意思。
 *
 * 请求前由 `resolvePageSize()` 换算成真实条数。
 */
export const PAGE_SIZE_ALL = 0

export type PageSize = (typeof PAGE_SIZE_OPTIONS)[number] | typeof PAGE_SIZE_ALL

/**
 * 容器列表**全部**可开关的列（顺序即列选择面板里的顺序）。
 *
 * ## 为什么从「只有三个指标列」扩到七列
 * 列选择器只列指标时，它读起来像是「指标开关」而不是「显示哪些列」——
 * 用户想收窄一张很宽的表（隐藏端口 / 创建时间）时找不到入口，
 * 只能去改窗口宽度。现在基础信息列也是可关的。
 *
 * ## 名称列**刻意不在这里**
 * 名称既是行身份，又是进入详情的唯一入口（`<button>`）。
 * 允许关掉它，用户就得到一张「点不进任何地方」的表，
 * 而且筛完条件后连「这是哪一行」都说不出来。
 *
 * ⚠️ **健康不再是可开关的列**：它已并入「状态」列（见 `ContainerStatusBadge`）——
 * 状态与健康回答的是同一个问题，分成两列时一行里会出现「运行中 / 不健康」两个词。
 * 留着 `health` 开关等于让人以为「关掉就不看健康了」，而实际上关不掉。
 */
export const CONTAINER_COLUMNS = [
  'image',
  'state',
  'ports',
  'created',
  'cpu',
  'memory',
  'network'
] as const

export type ContainerColumn = (typeof CONTAINER_COLUMNS)[number]

/**
 * 基础信息列：**只影响渲染**，默认全部显示。
 *
 * 关掉它们不省任何请求——列表接口本来就把这些字段一起返回了。
 */
export const CONTAINER_BASIC_COLUMNS = ['image', 'state', 'ports', 'created'] as const

/**
 * 运行指标列：默认全部**关闭**。
 *
 * 它们与前者的成本模型完全不同——开一列就等于给每个可见容器占一条到守护进程的流，
 * 因此默认关、且要在列选择面板里单独成组说明代价。
 */
export const CONTAINER_METRIC_COLUMNS = ['cpu', 'memory', 'network'] as const

/**
 * 每一列的**出厂默认可见性**——state 与 `setContainerColumn` 共用这一份。
 *
 * ⚠️ 必须集中在这里，不能一处写 state、一处写 `next[key] ?? true`：
 * 两份默认值分家时，用户在设置里重置 / 旧持久化对象补键，补出来的可见性与
 * 首次进入页面看到的就不是一回事（「我刚关掉端口，刷新一下它又回来了」）。
 */
export const CONTAINER_COLUMN_DEFAULTS: Record<ContainerColumn, boolean> = {
  image: true,
  state: true,
  ports: true,
  created: true,
  cpu: false,
  memory: false,
  network: false
}

/**
 * 把界面上的每页条数换算成**可以发给后端的**真实条数。
 *
 * 「全部」（0）要换成"当前总数"——但**不能超过 `MAX_PAGE_SIZE`**：
 * 那正是后端的夹紧线，换算出比它更大的值只会被静默夹掉，
 * 结果是"请求了全部、拿到的却是 100 条"，且前后端都没有任何提示。
 * 夹在上限内至少让请求值与实际返回一致。
 *
 * 下限给 1：空列表时总数为 0，传 0 出去又会被夹成 1，不如直接给 1。
 */
export function resolvePageSize(pageSize: number, total: number): number {
  if (pageSize !== PAGE_SIZE_ALL) return pageSize

  return Math.min(Math.max(total, 1), MAX_PAGE_SIZE)
}

/**
 * 用户偏好 store：列表每页条数 + 三项「实时更新」开关。
 * 主题（stores/theme.ts）与语言（locales/index.ts）各自独立持久化，不在此重复。
 */
export const usePreferencesStore = defineStore('preferences', {
  state: () => ({
    /** 列表每页条数，默认 20；`PAGE_SIZE_ALL` 表示「全部」 */
    pageSize: 20 as PageSize,

    /**
     * 容器列表实时刷新（0.5.0）。
     *
     * 开启时列表订阅容器事件，容器一停/一死就立刻刷新；
     * 关闭时回到 5 秒轮询。
     */
    realtimeContainers: true,

    /**
     * 容器详情页的运行指标（0.5.0）。
     *
     * 开启时详情页订阅资源采样；关闭时不订阅（不占那条到守护进程的长连接）。
     */
    realtimeStats: true,

    /**
     * 容器列表的列开关（0.6.0，0.12.0 起覆盖基础信息列）。
     *
     * 基础信息列默认显示，指标三列**默认关闭**：
     * 每个开着的指标列都意味着给每个可见容器占一条到守护进程的流。
     *
     * ⚠️ 这里**没有** `health`：健康已并入状态列（见 `ContainerColumn` 的说明）。
     * 旧版本持久化下来的 `health: true` 会被 `setContainerColumn` 一并丢弃——
     * 按 `CONTAINER_COLUMNS` 重建而不是展开旧对象，才能真正把它清掉。
     *
     * ⚠️ **旧版本持久化下来的对象只有 cpu/memory/network 三个键**。
     * 因此每个键都必须有确定的值（这里从 `CONTAINER_COLUMN_DEFAULTS` 展开），
     * 否则 hydrate 之后 `containerColumns.image` 是 `undefined` →
     * 模板里 `v-if="columns.image"` 取到 undefined 为假 → **镜像列凭空消失**，
     * 而代码里明明写着默认显示。
     *
     * 新增一列要同时改三处：`CONTAINER_COLUMNS`、`CONTAINER_COLUMN_DEFAULTS`
     * （或它对应的分组常量）+ 列选择面板里那个 `Record<ContainerColumn, string>` 标签表。
     */
    containerColumns: { ...CONTAINER_COLUMN_DEFAULTS },

    /**
     * 顶栏铃铛的容器异常退出通知（0.5.0）。
     *
     * 开启时全局订阅通知 Hub；关闭时不连接。
     */
    realtimeNotifications: true,

    /**
     * 容器列表的状态筛选（0.7.0）。
     *
     * ⚠️ **刻意记住上次选择**：状态筛选是「我这次要看哪一类容器」的延续，
     * 不是一次性的临时开关——用户切到「仅未运行」排查完问题后，
     * 下次进页面往往还是想看那类。不记住的话每次都要重新点一遍。
     *
     * `stopped` 的口径是「未运行」（已退出 / 已死亡 / 从未启动），
     * 与后端 `ContainerStateFilter` 一致。
     */
    containerStateFilter: 'all' as ContainerStateFilter
  }),
  actions: {
    setPageSize(size: number) {
      // 防御非法值：外部（持久化数据被手改）传入时回落到默认。
      // `PAGE_SIZE_ALL` 单独放行——它在语义上是合法的，不能被当成"非法值"打回 20。
      this.pageSize =
        size === PAGE_SIZE_ALL || (PAGE_SIZE_OPTIONS as readonly number[]).includes(size)
          ? (size as PageSize)
          : 20
    },

    /**
     * 切换容器列表某一列的显示。
     *
     * ⚠️ **按 `CONTAINER_COLUMNS` 重建**，而不是 `{ ...旧对象, [column]: visible }`：
     * 展开旧对象会把旧版本持久化下来的 `health` 键一直带下去，
     * 而它已经不对应任何列了；同时也会把「用户手改 localStorage 塞进来的野键」一路保留。
     *
     * 重建还能顺带修好另一件事：旧版持久化对象里**缺**新加的键，
     * 展开旧对象时那些键会一直是 `undefined`（列永远不显示），
     * 而重建会把它们补成本次 state 里的当前值（= 默认显示）。
     */
    setContainerColumn(column: ContainerColumn, visible: boolean) {
      const next = {} as Record<ContainerColumn, boolean>

      for (const key of CONTAINER_COLUMNS) {
        // 旧持久化对象里没有的键（新增列）回落到出厂默认，而不是 undefined——
        // undefined 在模板里 `v-if` 为假，会让新列"默认显示"的约定静默失效。
        next[key] = key === column ? visible : (this.containerColumns[key] ?? CONTAINER_COLUMN_DEFAULTS[key])
      }

      this.containerColumns = next
    },

    /**
     * 设置容器列表的状态筛选。
     *
     * ⚠️ **防御非法值**：外部（持久化数据被手改、旧版本残留）传入时回落到「全部」。
     * 非法值会让后端按「全部」处理，而界面显示的是一个下拉里根本不存在的选项——
     * 用户会以为筛选坏了。
     */
    setContainerStateFilter(filter: ContainerStateFilter) {
      this.containerStateFilter = filter === 'running' || filter === 'stopped' ? filter : 'all'
    }
  },
  persist: {
    key: 'whalefleet:preferences',
    storage: localStorage
  }
})