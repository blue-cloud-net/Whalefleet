import { onUnmounted, ref, shallowRef } from 'vue'
import type { HubConnection } from '@microsoft/signalr'
import { getContainerLogs } from '@/api/container'
import { ApiRequestError } from '@/api'
import { createHubConnection, hostHubPath } from '@/realtime/connection'
import { createMockLogStream } from '@/realtime/mock'
import { shouldUseMock } from '@/mocks/switch'
import type { ContainerLogLine, ContainerLogPayload, RealtimeError } from '@/types/api'

/**
 * 容器日志的实时订阅（0.5.0）。
 *
 * ## 数据流
 * ```
 * 首屏历史（REST, tail=N）──→ lines[0..N-1]
 *                                  │
 * SignalR OnLog 批次 ─────────────┴──→ lines（追加）
 * ```
 * 序号 `seq` 由**本 composable**统一维护（永远等于「展示序号」= 数组下标 + 1），
 * 因此向上加载更早的日志后不需要任何一处的编号修正。
 *
 * ## 为什么首屏与实时流之间的空隙可以接受
 * 历史是一次 HTTP 请求、实时流是另一条长连接，两者之间有几百毫秒的窗口，
 * 窗口内产生的日志行会**漏掉**。
 * 另一种做法（把历史末行的时间戳当 `since` 传给实时流）会**重复**推送
 * 那一秒内的全部历史行——两害相权，取漏。
 * 重复比漏更难解释：用户看到同一行出现两次会以为程序有问题。
 *
 * ## 为什么不用 `tail=N` 开实时流
 * 那会把历史再推一遍。后端 `ContainerStreamService` 因此在无 `since` 时用
 * `tail=0`（只要新产生的行）。
 */

/** 日志流的连接状态。 */
export type LogStreamStatus = 'idle' | 'loading' | 'live' | 'error'

/**
 * 一次行集合变更的描述。
 *
 * ## 为什么需要它
 * 虚拟滚动的行高是**按索引**记的，「尾部追加」和「顶部插入」对索引的影响
 * 完全相反，光看行数变化分不出来。视图据此决定是追加还是前插。
 */
export interface LogMutation {
  /** 单调递增的版本号；每次变更 +1。 */
  version: number
  /** 本次变更的类型。 */
  kind: 'reset' | 'append' | 'prepend'
  /** 本次变更的行数。 */
  count: number
}

/** 订阅参数。 */
export interface UseContainerLogStreamOptions {
  /** 主机标识（主机名）。 */
  hostId: () => string
  /** 容器标识。 */
  containerId: () => string
  /** 首屏拉取的尾行数。 */
  tail: () => number
}

/**
 * 容器日志实时订阅。
 *
 * @param options 订阅参数。
 * @returns 日志流状态与操作。
 */
export function useContainerLogStream(options: UseContainerLogStreamOptions) {
  const lines = ref<ContainerLogLine[]>([])
  const mutation = ref<LogMutation>({ version: 0, kind: 'reset', count: 0 })
  const status = ref<LogStreamStatus>('idle')
  const error = ref<string | null>(null)
  /** 是否可能还有更早的日志。 */
  const hasMore = ref(false)
  /** 是否正在加载更早的日志。 */
  const loadingOlder = ref(false)
  /** 因服务端频道拥塞而丢弃的行数。 */
  const dropped = ref(0)
  /** 是否暂停接收（冻结渲染）。 */
  const paused = ref(false)
  /** 暂停期间缓冲的行数。 */
  const pendingCount = ref(0)

  const connection = shallowRef<HubConnection | null>(null)
  let stopMock: (() => void) | null = null
  /** 暂停期间的缓冲。 */
  let pending: ContainerLogLine[] = []
  /** 服务端流内序号的上一个值（用于检测丢行）。 */
  let lastStreamSeq = 0
  /** 是否已经发过首屏。 */
  let started = false

  const isMock = (): boolean => import.meta.env.DEV && shouldUseMock()

  /** 记录一次变更。 */
  function applyMutation(kind: LogMutation['kind'], count: number): void {
    mutation.value = { version: mutation.value.version + 1, kind, count }
  }

  /** 重新编号（展示序号 = 下标 + 1）。 */
  function renumber(list: ContainerLogLine[]): ContainerLogLine[] {
    return list.map((item, index) => ({ ...item, seq: index + 1 }))
  }

  /**
   * 追加实时到达的行。
   *
   * @param incoming 新行。
   */
  function appendLive(incoming: ContainerLogLine[]): void {
    if (incoming.length === 0) return

    if (paused.value) {
      pending = pending.concat(incoming)
      pendingCount.value = pending.length
      return
    }

    const next = lines.value.concat(incoming)
    lines.value = renumber(next)
    applyMutation('append', incoming.length)
  }

  /**
   * 处理服务端推来的一批日志。
   *
   * @param payload 批次载荷。
   */
  function onLog(payload: ContainerLogPayload): void {
    if (!payload?.lines?.length) return

    // 序号缺口 = 服务端丢弃了行（频道满时丢最旧的）。
    // 不静默吞掉：用户需要知道「这段日志不全」。
    const first = payload.lines[0]
    const last = payload.lines[payload.lines.length - 1]
    if (!first || !last) return

    if (lastStreamSeq > 0 && first.seq > lastStreamSeq + 1) {
      dropped.value += first.seq - lastStreamSeq - 1
    }
    lastStreamSeq = last.seq

    appendLive(payload.lines.map((item) => ({ ...item })))
  }

  /**
   * 处理服务端推来的错误。
   *
   * @param payload 错误载荷。
   */
  function onStreamError(payload: RealtimeError): void {
    error.value = payload?.message || '实时通道出错'
    status.value = 'error'
  }

  /**
   * 建立实时连接并订阅。
   */
  async function startStream(): Promise<void> {
    const host = options.hostId()
    const container = options.containerId()
    if (!host || !container) return

    if (isMock()) {
      stopMock = createMockLogStream().start(
        container,
        lines.value.length + 1,
        (batch) => onLog({ containerId: container, lines: batch }),
        (code, message) => onStreamError({ code, message })
      )
      status.value = 'live'
      return
    }

    const hub = createHubConnection(hostHubPath(host, 'container-log'))
    connection.value = hub

    hub.on('OnLog', onLog)
    hub.on('OnStreamError', onStreamError)

    // 重连后订阅关系会丢（服务端看到的是一条新连接），必须重新订阅。
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
   * 在已建立的连接上订阅日志。
   *
   * @param hub 连接。
   * @param container 容器标识。
   */
  async function subscribe(hub: HubConnection, container: string): Promise<void> {
    const result = await hub.invoke('SubscribeLogs', container, lines.value.length + 1, null)

    if (result && result.success === false) {
      error.value = result.message || '订阅失败'
      status.value = 'error'
      return
    }

    status.value = 'live'
  }

  /**
   * 加载首屏历史并建立实时流。
   */
  async function load(): Promise<void> {
    const host = options.hostId()
    const container = options.containerId()
    if (!host || !container) return

    status.value = 'loading'
    error.value = null
    dropped.value = 0
    pending = []
    pendingCount.value = 0
    lastStreamSeq = 0

    try {
      const result = await getContainerLogs(host, container, {
        tail: options.tail(),
        includeStderr: true
      })

      lines.value = renumber(result.lines.map((item) => ({ ...item })))
      hasMore.value = result.truncated
      applyMutation('reset', lines.value.length)
    } catch (e) {
      error.value = e instanceof ApiRequestError ? e.message : String(e)
      status.value = 'error'
      lines.value = []
      applyMutation('reset', 0)
      return
    }

    if (!started) {
      started = true
      await startStream()
    }
  }

  /**
   * 加载更早的日志（向上滚动时调用）。
   */
  async function loadOlder(): Promise<void> {
    if (loadingOlder.value || !hasMore.value) return

    const current = lines.value
    const first = current[0]
    if (!first?.timestamp) {
      // 没有时间戳就无法定位边界（守护进程没给时间戳）→ 不再尝试。
      hasMore.value = false
      return
    }

    loadingOlder.value = true
    try {
      const boundary = new Date(first.timestamp).getTime()
      const until = Math.floor(boundary / 1000)

      const result = await getContainerLogs(options.hostId(), options.containerId(), {
        until,
        tail: 1000,
        includeStderr: true
      })

      const older = result.lines.map((item) => ({ ...item }))

      // 边界秒内的重复行要去掉：`until` 是**含边界**的，
      // 所以更早的一页会把边界那一秒的行也带回来，而当前列表末尾已经有它们。
      const sameCount = current.filter((item) => item.timestamp === first.timestamp).length
      const deduped = dropBoundaryDuplicates(older, first.timestamp, sameCount)

      if (deduped.length === 0) {
        hasMore.value = false
        return
      }

      lines.value = renumber(deduped.concat(current))
      hasMore.value = result.truncated
      applyMutation('prepend', deduped.length)
    } catch (e) {
      error.value = e instanceof ApiRequestError ? e.message : String(e)
    } finally {
      loadingOlder.value = false
    }
  }

  /**
   * 暂停接收（冻结渲染）。
   *
   * 暂停期间到达的行会**缓冲**而不是丢弃——恢复后补上。
   */
  function pause(): void {
    paused.value = true
  }

  /**
   * 恢复接收。
   */
  function resume(): void {
    if (!paused.value) return

    paused.value = false
    if (pending.length > 0) {
      const flushed = pending
      pending = []
      pendingCount.value = 0
      lines.value = renumber(lines.value.concat(flushed))
      applyMutation('append', flushed.length)
    }
  }

  /**
   * 停止实时流。
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
        // 连接可能已经断了——停止失败不影响页面。
      }
    }
  }

  onUnmounted(() => {
    void stop()
  })

  return {
    lines,
    mutation,
    status,
    error,
    hasMore,
    loadingOlder,
    dropped,
    paused,
    pendingCount,
    load,
    loadOlder,
    pause,
    resume,
    stop
  }
}

/**
 * 去掉边界秒内的重复行。
 *
 * ## 为什么需要
 * `until` 参数是**含边界**的（Docker 的语义是「时间戳 ≤ until 的都返回」）。
 * 于是「更早的一页」会把边界那一秒的行也带回来，而当前列表末尾已经有它们——
 * 不去掉就会在往上加载时看到重复行。
 *
 * ## 做法
 * 边界秒的行在「更早的一页」里是**连续的前缀**。设该前缀长度为 `n`，
 * 当前列表末尾已有 `k` 行同秒的，则这一页多带回了 `n - k` 行**更早**的同秒行——
 * 它们要保留；多出来的是**最后 k 行**（与当前列表重复）——要去掉。
 *
 * @param older 更早的一页（按时间升序）。
 * @param boundaryTimestamp 边界时间戳（ISO 字符串）。
 * @param sameCount 当前列表末尾同秒的行数。
 * @returns 去重后的行。
 */
export function dropBoundaryDuplicates(
  older: ContainerLogLine[],
  boundaryTimestamp: string,
  sameCount: number
): ContainerLogLine[] {
  let prefixLength = 0
  while (prefixLength < older.length && older[prefixLength]?.timestamp === boundaryTimestamp) {
    prefixLength += 1
  }

  if (prefixLength === 0 || sameCount <= 0) return older

  // 前缀里最后 sameCount 行与当前列表重复 → 砍掉。
  const keep = Math.max(0, prefixLength - sameCount)
  return older.slice(0, keep).concat(older.slice(prefixLength))
}

/**
 * 判断日志流是否可用（供视图决定要不要显示「连接中」）。
 *
 * @param status 连接状态。
 * @returns 是否已连上。
 */
export function isLive(status: LogStreamStatus): boolean {
  return status === 'live'
}

/**
 * 计算日志流的「连接状态」文案键。
 *
 * @param status 连接状态。
 * @returns i18n key。
 */
export function statusKey(status: LogStreamStatus): string {
  switch (status) {
    case 'loading':
      return 'container.logs.status.loading'
    case 'live':
      return 'container.logs.status.live'
    case 'error':
      return 'container.logs.status.error'
    default:
      return 'container.logs.status.idle'
  }
}
