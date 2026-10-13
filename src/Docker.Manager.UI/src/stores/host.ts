import { defineStore } from 'pinia'
import type { HostGroup, HostInfo, HostOverview } from '@/types/api'

/**
 * 主机分组定义（当前仅展示，无管理 UI）。
 *
 * 未分组主机不属于任何分组，由 `groupedHosts` 统一排在末尾，
 * 显示名走 i18n key `host.group.ungrouped`。
 */
export const DEFAULT_HOST_GROUPS: HostGroup[] = [
  { id: 'production', nameKey: 'host.group.production', description: '线上生产负载', order: 10 },
  { id: 'staging', nameKey: 'host.group.staging', description: '预发布验证环境', order: 20 },
  { id: 'local', nameKey: 'host.group.local', description: 'Docker.Manager 宿主机自身', order: 30 }
]

/**
 * 演示用主机种子数据。
 *
 * TODO(0.0.8)：后端 `POST /api/v1/host/list` 与聚合总览接口就绪后，
 * 改为登录后拉取真实列表填充 `hosts`，本常量仅保留为离线兜底。
 *
 * 注意 `staging-01` 刻意**不提供** version / startedAt —— 用于验证离线主机
 * 在 UI 上显示占位而非塌版。
 */
export const DEFAULT_HOSTS: HostOverview[] = [
  {
    id: 'local',
    name: 'local',
    type: 'socket',
    endpoint: '/var/run/docker.sock',
    enabled: true,
    status: 'online',
    groupId: 'local',
    startedAt: '2026-09-28T03:12:45Z',
    version: { version: '27.3.1', apiVersion: '1.47', os: 'linux', arch: 'x86_64', goVersion: 'go1.22.7' }
  },
  {
    id: 'prod-01',
    name: 'prod-01',
    type: 'tcp',
    endpoint: 'tcp://10.0.0.11:2376',
    enabled: true,
    status: 'online',
    groupId: 'production',
    startedAt: '2026-10-06T11:40:08Z',
    version: { version: '27.3.1', apiVersion: '1.47', os: 'linux', arch: 'aarch64', goVersion: 'go1.22.7' }
  },
  {
    id: 'staging-01',
    name: 'staging-01',
    type: 'tcp',
    endpoint: 'tcp://10.0.0.21:2375',
    enabled: true,
    status: 'offline',
    groupId: 'staging'
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
 */
export const useHostStore = defineStore('host', {
  state: () => ({
    /** 当前选中主机 id；空串 = 全局作用域（未选主机） */
    currentId: '' as string,
    /** 可见主机列表（受用户资源可见性约束） */
    hosts: [...DEFAULT_HOSTS] as HostOverview[],
    /** 分组定义 */
    groups: [...DEFAULT_HOST_GROUPS] as HostGroup[]
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