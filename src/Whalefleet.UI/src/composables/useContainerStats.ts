import { onUnmounted, ref, shallowRef } from 'vue'
import type { HubConnection } from '@microsoft/signalr'
import { createHubConnection, hostHubPath } from '@/realtime/connection'
import { createMockStatsStream } from '@/realtime/mock'
import { shouldUseMock } from '@/mocks/switch'
import type {
  ContainerStatsHistoryPayload,
  ContainerStatsPayload,
  ContainerStatsSample,
  RealtimeError
} from '@/types/api'

/**
 * 容器运行指标的实时订阅（0.5.0）。
 *
 * ## 为什么是「每容器一条订阅」而不是「每主机一条广播」
 * 一台机器上可能有几十个容器，但**只有当前打开详情页的那个**需要指标。
 * 按容器订阅意味着：关掉页面就退订，Docker 侧那条 `/stats` 流也随之结束——
 * 不会给一个没人看的容器持续采样。
 *
 * ## 开关
 * 「设置 → 实时更新 → 容器详情页指标」关掉时，**根本不建立连接**。
 * 这不只是省流量：那是一条到守护进程的长连接，默认连着总归是不对。
 */

/** 指标订阅的连接状态。 */
export type StatsStreamStatus = 'idle' | 'connecting' | 'live' | 'error'

/** 订阅参数。 */
export interface UseContainerStatsOptions {
  /** 主机标识（主机名）。 */
  hostId: () => string
  /** 容器标识。 */
  containerId: () => string
  /** 是否启用（对应偏好里的「容器详情页指标」开关）。 */
  enabled: () => boolean
}

/**
 * 容器运行指标实时订阅。
 *
 * @param options 订阅参数。
 * @returns 指标状态与操作。
 */
export function useContainerStats(options: UseContainerStatsOptions) {
  const sample = ref<ContainerStatsSample | null>(null)
  /**
   * 最近一段采样（旧 → 新，最多 60 点）。
   *
   * 服务端在订阅成功后会先推一条历史（进程内环形缓冲），因此新打开的页面不必
   * 从空图开始攒——这也正是「采样缓存」那条要求的目的。
   */
  const samples = ref<ContainerStatsSample[]>([])
  const status = ref<StatsStreamStatus>('idle')
  const error = ref<string | null>(null)

  const MAX_POINTS = 60

  const connection = shallowRef<HubConnection | null>(null)
  let stopMock: (() => void) | null = null

  /** 追加一个采样并裁剪到 60 点。 */
  function append(value: ContainerStatsSample): void {
    samples.value = [...samples.value, value].slice(-MAX_POINTS)
    sample.value = value
  }

  /**
   * 处理一次采样。
   *
   * @param payload 采样载荷。
   */
  function onStats(payload: ContainerStatsPayload): void {
    if (!payload?.stats) return
    append(payload.stats)
  }

  /**
   * 处理订阅时补发的历史采样。
   *
   * @param payload 历史载荷（旧 → 新）。
   */
  function onStatsHistory(payload: ContainerStatsHistoryPayload): void {
    if (!payload?.samples?.length) return
    samples.value = payload.samples.slice(-MAX_POINTS)
    sample.value = payload.samples[payload.samples.length - 1] ?? null
  }

  /**
   * 处理服务端错误。
   *
   * @param payload 错误载荷。
   */
  function onStreamError(payload: RealtimeError): void {
    error.value = payload?.message || '指标通道出错'
    status.value = 'error'
  }

  /**
   * 建立连接并订阅。
   */
  async function start(): Promise<void> {
    const host = options.hostId()
    const container = options.containerId()

    if (!options.enabled() || !host || !container) {
      status.value = 'idle'
      return
    }

    status.value = 'connecting'
    error.value = null

    if (import.meta.env.DEV && shouldUseMock()) {
      stopMock = createMockStatsStream().start(container, (value) => {
        append(value)
        status.value = 'live'
      })
      return
    }

    const hub = createHubConnection(hostHubPath(host, 'container-stats'))
    connection.value = hub

    hub.on('OnStats', onStats)
    hub.on('OnStatsHistory', onStatsHistory)
    hub.on('OnStreamError', onStreamError)
    hub.onreconnected(() => {
      void subscribe(hub, container)
    })

    try {
      await hub.start()
      await subscribe(hub, container)
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
      status.value = 'error'
    }
  }

  /**
   * 在已建立的连接上订阅指标。
   *
   * @param hub 连接。
   * @param container 容器标识。
   */
  async function subscribe(hub: HubConnection, container: string): Promise<void> {
    const result = await hub.invoke('SubscribeStats', container)

    if (result && result.success === false) {
      error.value = result.message || '订阅失败'
      status.value = 'error'
      return
    }

    status.value = 'live'
  }

  /**
   * 停止订阅。
   */
  async function stop(): Promise<void> {
    stopMock?.()
    stopMock = null

    const hub = connection.value
    connection.value = null

    if (hub) {
      try {
        await hub.stop()
      } catch {
        // 连接可能已经断了。
      }
    }

    status.value = 'idle'
  }

  onUnmounted(() => {
    void stop()
  })

  return { sample, samples, status, error, start, stop }
}
