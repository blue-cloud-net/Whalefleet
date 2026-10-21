import { defineStore } from 'pinia'
import { listHostGroups } from '@/api/host-group'
import { getHostOverview, listHosts } from '@/api/host'
import type { HostGroup, HostInfo, HostListFilter, HostOverview } from '@/types/api'

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
 * ## 主机与分组都来自 API，不再内置种子
 *
 * 早期版本把三台演示主机与三个分组写成本文件的常量，等于「前端自带一份真相」——
 * 既让「数据加载失败」这条路径无法被测试，也让新手误以为数据本来就长这样。
 * 0.0.13 起分组也是后端数据（<c>host_groups</c> 表），前端只负责展示：
 * 内置分组的名字→文案映射在 `utils/group-display.ts`。
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
    /**
     * 当前生效的过滤条件（0.6.0，服务端过滤）。
     *
     * ⚠️ **「未分组」用空串表达**：JSON 分不清「没给 groupId」与「给了 null」，
     * 而两者语义不同（不过滤 vs 只看未分组）。
     */
    filter: {} as HostListFilter,
    /** `ensureLoaded()` 的在途 Promise，用于并发去重 */
    _inflight: null as Promise<void> | null,
    /** `loadHostDetails()` 的在途 Promise，用于并发去重 */
    _detailsInflight: null as Promise<void> | null
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
     * 排序规则：分组按 `sortOrder` 升序（后端已排好，这里再排一次是为了不依赖响应顺序），
     * 无成员（或不存在）的分组直接丢弃，未分组主机恒排在末尾。组内保持 `hosts` 的原始顺序。
     */
    groupedHosts(state): { group: HostGroup | null; hosts: HostOverview[] }[] {
      const ordered = [...state.groups].sort((a, b) => a.sortOrder - b.sortOrder)
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

    /** 更新过滤条件（不自动重拉；调用方连续改多个条件时只需拉一次） */
    setFilter(filter: HostListFilter) {
      this.filter = filter
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
        // 两个请求并行：分组与主机列表互不依赖，串行只会白等一个 RTT
        const [hosts, groups] = await Promise.all([listHosts(this.filter), listHostGroups()])

        this.hosts = hosts
        this.groups = groups
        this.lastSyncedAt = new Date().toISOString()

        // 版本 / 计数不阻塞首屏：列表已够画出主机行，这几项到位后再各自补上。
        // 刻意**不** await——否则守卫要多等 N 次重量调用，深链首屏白屏时间翻倍。
        void this.loadHostDetails()
      } catch (err) {
        this.error = err instanceof Error ? err.message : String(err)
      } finally {
        this.loading = false
      }
    },

    /**
     * 补齐全在线主机的聚合信息（`version` / `counts` / `startedAt`）并合并进列表。
     *
     * **为什么需要这一步**：列表端点刻意不带这三项——每台主机要额外四次守护进程调用，
     * 主机一多列表就会明显变慢甚至超时。它们只能走 `POST /hosts/{hostId}/overview`。
     * 此前前端只定义了 `getHostOverview()` 却**没有任何调用方**，于是总览卡片与主机落地页的
     * 四类资源计数恒为「—」、版本行整行消失——看起来像后端没实现，其实是最后一段没接上。
     *
     * 语义：
     * - **只问在线主机**：离线主机刚被列表探测判过，再问一次只是多等一次超时；
     * - **单台失败不影响其他**（`allSettled`）：一台主机连不上不该让整页计数全空；
     * - **只覆盖拿到的字段**：接口回 `null`（如离线）时保留列表里的原值，不把已知信息抹掉。
     */
    async loadHostDetails(): Promise<void> {
      if (this._detailsInflight) return this._detailsInflight

      const targets = this.hosts.filter((h) => h.status === 'online').map((h) => h.id)
      if (targets.length === 0) return

      const pending = (async () => {
        const results = await Promise.allSettled(targets.map((id) => getHostOverview(id)))

        const details = new Map<string, HostOverview>()
        for (const [index, result] of results.entries()) {
          const id = targets[index]
          if (id !== undefined && result.status === 'fulfilled') details.set(id, result.value)
        }

        // 只更新仍在列表里的主机：这批请求返回前列表可能已被重新拉取过
        this.hosts = this.hosts.map((host) => {
          const detail = details.get(host.id)
          if (!detail) return host

          return {
            ...host,
            version: detail.version ?? host.version,
            counts: detail.counts ?? host.counts,
            daemon: detail.daemon ?? host.daemon,
            tags: detail.tags ?? host.tags
          }
        })
      })().finally(() => {
        this._detailsInflight = null
      })

      this._detailsInflight = pending
      return pending
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
 * `whalefleet:host` 键会继续存在并造成困惑，故在应用启动时一次性移除。
 */
export const LEGACY_HOST_STORAGE_KEY = 'whalefleet:host'

export function purgeLegacyHostStorage(): void {
  if (typeof window === 'undefined') return
  try {
    window.localStorage.removeItem(LEGACY_HOST_STORAGE_KEY)
  } catch {
    // localStorage 不可用（隐私模式 / 配额禁用）时静默跳过——清理失败不应阻断启动
  }
}