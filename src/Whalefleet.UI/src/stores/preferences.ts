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
 * 容器列表可开关的列。
 *
 * ⚠️ **健康不再是可开关的列**：它已并入「状态」列（见 `ContainerStatusBadge`）——
 * 状态与健康回答的是同一个问题，分成两列时一行里会出现「运行中 / 不健康」两个词。
 * 留着 `health` 开关等于让人以为「关掉就不看健康了」，而实际上关不掉。
 *
 * 三个指标列共用一个开关：它们都是**持续订阅**（每个容器一条到守护进程的流），
 * 成本模型相同。
 */
export type ContainerColumn = 'cpu' | 'memory' | 'network'

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
     * 容器列表的列开关（0.6.0）。
     *
     * 指标三列**默认关闭**：每个开着的指标列都意味着给每个可见容器占一条到守护进程的流。
     *
     * ⚠️ 这里**没有** `health`：健康已并入状态列（见 `ContainerColumn` 的说明）。
     * 旧版本持久化下来的 `health: true` 会被 `setContainerColumn` 一并丢弃——
     * 用「显式列出三个键」而不是展开旧对象，才能真正把它清掉。
     */
    containerColumns: {
      cpu: false,
      memory: false,
      network: false
    } as Record<ContainerColumn, boolean>,

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
     * ⚠️ **显式列出三个键**而不是 `{ ...旧对象, [column]: visible }`：
     * 展开旧对象会把旧版本持久化下来的 `health` 键一直带下去，
     * 而它已经不对应任何列了。
     */
    setContainerColumn(column: ContainerColumn, visible: boolean) {
      this.containerColumns = {
        cpu: this.containerColumns.cpu,
        memory: this.containerColumns.memory,
        network: this.containerColumns.network,
        [column]: visible
      }
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