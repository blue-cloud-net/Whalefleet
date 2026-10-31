import { shallowRef } from 'vue'
import type { HubConnection } from '@microsoft/signalr'
import { createHubConnection } from '@/realtime/connection'
import { createMockNotificationSource } from '@/realtime/mock'
import { shouldUseMock } from '@/mocks/switch'
import type {
  ContainerDiedEvent,
  OperationProgress,
  StackTaskFailureEvent
} from '@/types/api'

/**
 * 全局通知 Hub 的**共享连接**（0.5.0）。
 *
 * ## 为什么共享
 * 通知 Hub 上有两类订阅：容器退出事件（全局）与后台操作进度（按令牌）。
 * 它们本来会由多个地方分别建连接——顶栏铃铛、容器列表、镜像进度对话框、编排任务——
 * 多个 SignalR 连接就是多条到服务端的长连接，纯粹是浪费。
 *
 * 这里把它们**复用**到一条连接上：第一个订阅者到达时建立连接，
 * 最后一个订阅者离开时关闭连接。中间不管有几个订阅者，连接始终只有一条。
 *
 * ## 重连后要重新订阅
 * 服务端看到的是**新连接**（新的 `ConnectionId`），订阅关系不随重连迁移。
 * 因此 `onreconnected` 里要把所有进度令牌重新订阅一遍。
 * 容器退出事件是**服务端广播**（不需要逐条订阅），重连后自然恢复。
 */

/** 容器退出事件的处理函数。 */
export type ContainerDiedHandler = (event: ContainerDiedEvent) => void

/** 后台操作进度的处理函数。 */
export type ProgressHandler = (progress: OperationProgress) => void

/** 编排任务失败的处理函数。 */
export type StackTaskFailedHandler = (event: StackTaskFailureEvent) => void

/** 容器退出事件的订阅者。 */
const diedHandlers = new Set<ContainerDiedHandler>()

/** 编排任务失败的订阅者。 */
const stackFailedHandlers = new Set<StackTaskFailedHandler>()

/** 进度订阅（按令牌）。 */
const progressHandlers = new Map<string, ProgressHandler>()

/** 当前连接。 */
const connection = shallowRef<HubConnection | null>(null)

/** mock 模式下的数据源。 */
let stopMock: (() => void) | null = null

/** 是否正在建立连接（避免并发建立两条）。 */
let connecting = false

/**
 * 处理容器退出事件。
 *
 * @param event 事件。
 */
function dispatchDied(event: ContainerDiedEvent): void {
  for (const handler of diedHandlers) {
    handler(event)
  }
}

/**
 * 处理编排任务失败。
 *
 * @param event 事件。
 */
function dispatchStackFailed(event: StackTaskFailureEvent): void {
  for (const handler of stackFailedHandlers) {
    handler(event)
  }
}

/**
 * 处理进度推送。
 *
 * @param progress 进度。
 */
function dispatchProgress(progress: OperationProgress): void {
  const handler = progressHandlers.get(progress.token)
  handler?.(progress)
}

/**
 * 在已建立的连接上订阅所有进度令牌。
 *
 * @param hub 连接。
 */
async function subscribeAllProgress(hub: HubConnection): Promise<void> {
  for (const token of progressHandlers.keys()) {
    try {
      await hub.invoke('SubscribeOperation', token)
    } catch {
      // 订阅失败不影响其他订阅者；服务端会把失败写进日志。
    }
  }
}

/**
 * 确保连接已建立。
 */
async function ensureConnected(): Promise<void> {
  if (connection.value || connecting) return

  connecting = true
  try {
    if (import.meta.env.DEV && shouldUseMock()) {
      const source = createMockNotificationSource()
      stopMock = source.start(dispatchDied, (token, progress) => {
        dispatchProgress({
          token,
          operation: 'pull',
          percent: progress.percent,
          status: progress.status,
          done: false,
          error: null
        })
      })
      return
    }

    const hub = createHubConnection('notification')
    hub.on('OnContainerDied', dispatchDied)
    hub.on('OnOperationProgress', dispatchProgress)
    hub.on('OnStackTaskFailed', dispatchStackFailed)
    hub.onreconnected(() => {
      void subscribeAllProgress(hub)
    })

    await hub.start()
    connection.value = hub
    await subscribeAllProgress(hub)
  } catch {
    // 连不上不是致命错误——没有通知而已。关闭连接让下一次订阅重试。
    connection.value = null
  } finally {
    connecting = false
  }
}

/**
 * 没有任何订阅者时关闭连接。
 */
async function releaseIfIdle(): Promise<void> {
  if (diedHandlers.size > 0 || progressHandlers.size > 0 || stackFailedHandlers.size > 0) return

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
}

/**
 * 订阅容器退出事件。
 *
 * @param handler 处理函数。
 * @returns 退订函数。
 */
export function subscribeContainerDied(handler: ContainerDiedHandler): () => void {
  diedHandlers.add(handler)
  void ensureConnected()

  return () => {
    diedHandlers.delete(handler)
    void releaseIfIdle()
  }
}

/**
 * 订阅「编排任务失败」事件（0.10.0）。
 *
 * ⚠️ **只推失败、不推成功**：成功时用户正盯着界面看着进度条走完，
 * 界面自己就会刷新；而失败可能发生在用户已经离开页面之后
 * （webhook 触发的 CI 部署），那种情况下没有别的途径让用户知道出事了。
 *
 * @param handler 处理函数。
 * @returns 退订函数。
 */
export function subscribeStackTaskFailed(handler: StackTaskFailedHandler): () => void {
  stackFailedHandlers.add(handler)
  void ensureConnected()

  return () => {
    stackFailedHandlers.delete(handler)
    void releaseIfIdle()
  }
}

/**
 * 订阅某个镜像操作进度。
 *
 * @param token 进度令牌。
 * @param handler 处理函数。
 * @returns 退订函数。
 */
export function subscribeProgress(token: string, handler: ProgressHandler): () => void {
  progressHandlers.set(token, handler)
  void ensureConnected()

  const hub = connection.value
  if (hub) {
    void hub.invoke('SubscribeOperation', token).catch(() => undefined)
  }

  return () => {
    progressHandlers.delete(token)
    void releaseIfIdle()
  }
}
