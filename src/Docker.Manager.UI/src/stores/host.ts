import { defineStore } from 'pinia'
import { listHosts } from '@/api/host'
import type { HostGroup, HostInfo, HostOverview } from '@/types/api'

/**
 * 主机分组定义。
 *
 * 后端尚无分组表（`hosts.group_id` 未落库），因此分组由前端提供。
 * **刻意不从 `src/mocks/` 引入**——那会让 store 与 mock 产生生产构建上的耦合：
 * mock 模块会在开发时被静态打进主chunk，而分组定义在两种模式下都需要。
 *
 * 未分组主机不属于任何分组，由 `groupedHosts` 统一排在末尾，
 * 显示名与描述走 i18n key（不存字面文案）。
 */
export const DEFAULT_HOST_GROUPS: HostGroup[] = [
  {
    id: 'production',
    nameKey: 'host.group.production',
    descriptionKey: 'host.group.productionDesc',
    order: 10
  },
  {
    id: 'staging',
    nameKey: 'host.group.staging',
    descriptionKey: 'host.group.stagingDesc',
    order: 20
  },
  {
    id: 'local',
    nameKey: 'host.group.local',
    descriptionKey: 'host.group.localDesc',
    order: 30
  }
]

/**
 * 主机 store：主机列表 + 分组 + 当前选中主机。
 *
 * 作用域约定（与 router 的 `meta.scope` 对应）：
 *   - **全局作用域**（总览 / 全局设置 / 主机管理 / 用户管理）：`currentId` 为空串
 *   - **主机作用域**（`/hosts/:hostId/**`）：`currentId` 等于路由里的 `hostId`，由路由守卫写入
 *
 * URL 是唯一事实来源，store 只做镜像，因此刷新 / 深链都能还原作用域。
 *
 * **刻意不做持久化**：hosts / groups 来自后端，持久化快照必然陈旧；
 * currentId 由 URL 驱动，路由守卫每次导航都会重写。
 *
 * ## 主机数据来自 API，不再内置种子
 *
 * 早期版本把三台演示主机写成本文件的常量并作为初始 state，等于「前端自带一份真相」——
 * 既让「数据加载失败」这条路径无法被测试，也让新手误以为数据本来就长这样。
 * 现改为 `loadHosts()` 拉取；离线开发请开 mock（见 `src/mocks/`）。
 *
 * `ensureLoaded()` 是给路由守卫用的**幂等**入口：首次导航 await 一次，
 * 之后的导航直接返回，不会重复请求。
 */
export const useHostStore = defineStore('host', {
  state: () => ({
    /** 当前选中主机 id；空串 = 全局作用域（未选主机） */
    currentId: '' as string,
    /** 可见主机列表（受用户资源可见性约束） */
    hosts: [] as HostOverview[],
    /** 分组定义 */
    groups: [] as HostGroup[],
    /** 首次加载中；用于页面出骨架屏 */
    loading: false,
    /** 加载失败信息；页面据此显示错误态 + 重试 */
    error: null as string | null,
    /** 最近一次成功同步时间（ISO 字符串），供页面显示「数据更新于…」 */
    lastSyncedAt: null as string | null,
    /** `ensureLoaded()` 的在途 Promise，用于并发去重 */
    _inflight: null as Promise<void> | null
  }),
  getters: {
    /** 当前主机；全局作用域返回 null（不再回退到 local，避免"看起来像选中了某台机器"） */
    currentHost(state): HostInfo | null {
      if (!state.currentId) return null
      return state.hosts.find((h) => h.id === state.currentId) ?? null
    },
    /** 当前 id 是否命中已知主机 */
    hasCurrentHost: (state): boolean => state.hosts.some((h) => h.id === state.currentId),
    /**
     * 按分组聚合的主机列表，供总览页渲染。
     *
     * 排序规则：分组按 `order` 升序，无成员（或不存在）的分组直接丢弃，
     * 未分组主机恒排在末尾。组内保持 `hosts` 的原始顺序。
     */
    groupedHosts(state): { group: HostGroup | null; hosts: HostOverview[] }[] {
      const ordered = [...state.groups].sort((a, b) => a.order - b.order)
      const result: { group: HostGroup | null; hosts: HostOverview[] }[] = []

      for (const group of ordered) {
        const members = state.hosts.filter((h) => h.groupId === group.id)
        if (members.length > 0) {
          result.push({ group, hosts: members })
        }
      }

      // 已定义分组里没有归属的主机（groupId 指向不存在的组）也归入「未分组」
      const knownIds = new Set(ordered.map((g) => g.id))
      const ungrouped = state.hosts.filter((h) => !h.groupId || !knownIds.has(h.groupId))
      if (ungrouped.length > 0) {
        result.push({ group: null, hosts: ungrouped })
      }

      return result
    }
  },
  actions: {
    setCurrent(id: string) {
      this.currentId = id
    },
    /** 离开主机作用域：清空当前主机，回到全局视角 */
    clearCurrent() {
      this.currentId = ''
    },
    setHosts(hosts: HostOverview[]) {
      this.hosts = hosts
    },
    setGroups(groups: HostGroup[]) {
      this.groups = groups
    },

    /**
     * 拉取主机列表。
     *
     * 失败时**不抛异常**，而是把 `error` 置上并保留旧数据：
     * 守卫与页面都要根据 `error` 决定表现，抛出只会让调用方各自 try/catch。
     */
    async loadHosts(): Promise<void> {
      this.loading = true
      this.error = null
      try {
        this.hosts = await listHosts()
        // 分组定义仍由前端常量提供（后端尚无分组表）
        if (this.groups.length === 0) this.groups = [...DEFAULT_HOST_GROUPS]
        this.lastSyncedAt = new Date().toISOString()
      } catch (err) {
        this.error = err instanceof Error ? err.message : String(err)
      } finally {
        this.loading = false
      }
    },

    /**
     * 幂等加载：已加载过（或正在加载）则直接返回。
     *
     * 路由守卫用它保证「深链首屏也能校验 hostId」——
     * 否则 `hosts` 为空时守卫会跳过校验，坏链接会静默停在死路上。
     */
    async ensureLoaded(): Promise<void> {
      if (this._inflight) return this._inflight
      if (!this.loading && this.hosts.length > 0 && this.lastSyncedAt) return

      this._inflight = this.loadHosts().finally(() => {
        this._inflight = null
      })
      return this._inflight
    }
  }
})

/**
 * 清理历史遗留的持久化数据。
 *
 * 本 store 早期版本用 `pinia-plugin-persistedstate` 把 hosts 快照写入 localStorage，
 * 现已改为不持久化（见上方说明）。若不清掉，用户升级后旧的
 * `docker-manager:host` 键会继续存在并造成困惑，故在应用启动时一次性移除。
 */
export const LEGACY_HOST_STORAGE_KEY = 'docker-manager:host'

export function purgeLegacyHostStorage(): void {
  if (typeof window === 'undefined') return
  try {
    window.localStorage.removeItem(LEGACY_HOST_STORAGE_KEY)
  } catch {
    // localStorage 不可用（隐私模式 / 配额禁用）时静默跳过——清理失败不应阻断启动
  }
}