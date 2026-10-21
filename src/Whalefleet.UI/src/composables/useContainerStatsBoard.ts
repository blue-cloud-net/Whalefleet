import { onUnmounted, reactive, ref, shallowRef } from 'vue'
import type { HubConnection } from '@microsoft/signalr'
import { createHubConnection, hostHubPath } from '@/realtime/connection'
import { createMockStatsStream } from '@/realtime/mock'
import { shouldUseMock } from '@/mocks/switch'
import type { ContainerStatsHistoryPayload, ContainerStatsPayload, ContainerStatsSample } from '@/types/api'

/**
 * 容器列表的指标订阅板（0.6.0）。
 *
 * ## 与详情页的 `useContainerStats` 有什么不同
 * 详情页只看**一个**容器，所以「一个 composable 一条连接一条订阅」是最简单的写法。
 * 列表页要同时看一页的几十个容器：如果复用那个实现，一个页面就会开出几十条 SignalR
 * 连接。因此这里**共用一条连接**，在这条连接上按容器逐条 `SubscribeStats`。
 *
 * ## 成本保护（roadmap 明确要求）
 * - **上限 20**：与服务端的 `MaxStatsSubscriptionsPerConnection` 一致，超出的容器进队列；
 * - **页面隐藏即退订**：切到别的标签页后没人看那些数字，却仍在持续占用守护进程的流；
 * - **列关闭 / 切主机 / 翻页即退订**：由调用方传入的 `containerIds` 决定「谁该被订阅」。
 *
 * ## 为什么缓冲区在前端也保留
 * 服务端订阅时会先补发一段历史（环形缓冲），前端只需把收到的点按容器攒起来即可。
 * 双缓冲不是浪费：服务端缓冲跨客户端共享，前端缓冲只服务当前页面，互不干扰。
 */

/** 单个容器保留的采样点数（与服务端环形缓冲一致）。 */
const MAX_POINTS = 60

/** 单连接最多同时订阅的容器数（与服务端 `MaxStatsSubscriptionsPerConnection` 一致）。 */
export const MAX_STATS_SUBSCRIPTIONS = 20

export interface UseContainerStatsBoardOptions {
  /** 主机标识（主机名）。 */
  hostId: () => string
  /** 是否启用（任一指标列打开即为 true）。 */
  enabled: () => boolean
}

export function useContainerStatsBoard(options: UseContainerStatsBoardOptions) {
  /** 容器 ID → 采样序列（旧 → 新）。 */
  const series = reactive<Record<string, ContainerStatsSample[]>>({})

  const status = ref<'idle' | 'connecting' | 'live' | 'error'>('idle')

  const connection = shallowRef<HubConnection | null>(null)
  const subscribed = new Set<string>()
  const pending = new Set<string>()
  const mockStops = new Map<string, () => void>()

  /** 追加一个采样（裁剪到 MAX_POINTS）。 */
  function append(containerId: string, sample: ContainerStatsSample): void {
    const current = series[containerId] ?? []
    series[containerId] = [...current, sample].slice(-MAX_POINTS)
  }

  function onStats(payload: ContainerStatsPayload): void {
    if (!payload?.stats) return

    append(payload.containerId || payload.stats.containerId, payload.stats)
  }

  function onHistory(payload: ContainerStatsHistoryPayload): void {
    if (!payload?.samples?.length) return

    series[payload.containerId] = payload.samples.slice(-MAX_POINTS)
  }

  /** 建立连接（幂等）。 */
  async function connect(): Promise<void> {
    if (connection.value) return

    const host = options.hostId()
    if (!host || !options.enabled()) return

    status.value = 'connecting'

    if (import.meta.env.DEV && shouldUseMock()) {
      // mock 模式下每个容器一条定时器；数量受 MAX_STATS_SUBSCRIPTIONS 约束
      return
    }

    const hub = createHubConnection(hostHubPath(host, 'container-stats'))
    connection.value = hub

    hub.on('OnStats', onStats)
    hub.on('OnStatsHistory', onHistory)

    // 重连后原订阅全部丢失：把当前集合重放一遍
    hub.onreconnected(() => {
      const ids = [...subscribed]
      subscribed.clear()

      for (const id of ids) {
        void subscribe(id)
      }
    })

    try {
      await hub.start()
      status.value = 'live'
    } catch {
      status.value = 'error'
    }
  }

  /** 订阅单个容器（幂等）。 */
  async function subscribe(containerId: string): Promise<void> {
    if (subscribed.has(containerId)) return

    subscribed.add(containerId)

    if (import.meta.env.DEV && shouldUseMock()) {
      if (pending.has(containerId)) return

      pending.add(containerId)

      const stop = createMockStatsStream().start(containerId, (sample) => append(containerId, sample))

      mockStops.set(containerId, stop)
      status.value = 'live'

      return
    }

    const hub = connection.value
    if (!hub) return

    try {
      await hub.invoke('SubscribeStats', containerId)
    } catch {
      status.value = 'error'
      subscribed.delete(containerId)
    }
  }

  /** 退订单个容器。 */
  async function unsubscribe(containerId: string): Promise<void> {
    subscribed.delete(containerId)

    const stop = mockStops.get(containerId)
    if (stop) {
      stop()
      mockStops.delete(containerId)
      pending.delete(containerId)
    }

    delete series[containerId]

    const hub = connection.value
    if (!hub) return

    try {
      await hub.invoke('UnsubscribeStats', containerId)
    } catch {
      // 连接可能已断：退订失败无需处理，服务端会在断开时清理
    }
  }

  /**
   * 同步订阅集合：只订阅 `containerIds` 里出现、且没超过上限的容器。
   *
   * @param containerIds 当前页面「应当被订阅」的容器（可见 + 列开着 + 正在运行）。
   */
  async function sync(containerIds: string[]): Promise<void> {
    if (!options.enabled()) {
      await stop()

      return
    }

    await connect()

    const wanted = containerIds.slice(0, MAX_STATS_SUBSCRIPTIONS)
    const wantedSet = new Set(wanted)

    for (const id of [...subscribed]) {
      if (!wantedSet.has(id)) await unsubscribe(id)
    }

    for (const id of wanted) {
      await subscribe(id)
    }
  }

  /** 全部退订并断开。 */
  async function stop(): Promise<void> {
    for (const id of [...subscribed]) {
      await unsubscribe(id)
    }

    const hub = connection.value
    connection.value = null

    if (hub) {
      try {
        await hub.stop()
      } catch {
        // 已断开
      }
    }

    status.value = 'idle'
  }

  /** 页面隐藏 / 恢复：隐藏时退订，回来时由调用方重新 sync。 */
  function onVisibilityChange(): void {
    if (document.hidden) {
      void stop()
    }
  }

  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', onVisibilityChange)
  }

  onUnmounted(() => {
    if (typeof document !== 'undefined') {
      document.removeEventListener('visibilitychange', onVisibilityChange)
    }

    void stop()
  })

  return { series, status, sync, stop }
}
