import { MOCK_CONTAINER_LOGS } from '@/mocks/data/containers'
import type { ContainerDiedEvent, ContainerStatsSample } from '@/types/api'

/**
 * 实时通道的 mock 数据源（0.5.0）。
 *
 * ## 为什么需要它
 * SignalR 走的是 WebSocket / 长轮询，**MSW 拦不住**——
 * MSW 只能拦 HTTP 请求，而 Hub 的 negotiate 与后续帧不是普通的 HTTP 请求。
 * 因此 mock 模式下必须有另一条数据通路，否则日志页在 mock 下永远是空的。
 *
 * ## 与真实通道的差异（必须复刻，否则前端分支测不到）
 * - **批量推送**：真实通道是「攒批」（默认 100 行或 200ms 触发），不是逐行。
 * - **序号从 `startSeq` 起**：真实通道的流内序号接在首屏历史之后。
 * - **丢行**：真实通道的频道满了会丢最旧的行，序号**跳号**。
 *   这里也复刻一下，否则前端「已丢弃 N 行」的提示永远测不到。
 * - **统计数据有噪声**：真实采样是浮动的，不是恒定值。
 */

/** 模拟日志的一行。 */
interface MockLogLine {
  seq: number
  timestamp: string | null
  line: string
}

/** 把 mock 的整段日志文本拆成行。 */
const MOCK_LINES: string[] = MOCK_CONTAINER_LOGS.split('\n').filter((line) => line.length > 0)

/** 推送间隔（ms）。 */
const PUSH_INTERVAL = 1500

/** 每批推几行。 */
const BATCH_SIZE = 3

/**
 * 模拟的日志流。
 */
export interface MockLogStream {
  /**
   * 开始推送。
   *
   * @param containerId 容器 ID（只用于事件对象，日志内容与它无关）。
   * @param startSeq 起始序号。
   * @param onBatch 批次回调。
   * @param onError 错误回调。
   * @returns 停止函数。
   */
  start(
    containerId: string,
    startSeq: number,
    onBatch: (lines: MockLogLine[]) => void,
    onError: (code: number, message: string) => void
  ): () => void
}

/**
 * 创建模拟日志流。
 *
 * @returns 日志流。
 */
export function createMockLogStream(): MockLogStream {
  return {
    start(containerId, startSeq, onBatch, _onError) {
      let seq = startSeq
      let cursor = 0

      const tick = (): void => {
        const batch: MockLogLine[] = []
        for (let i = 0; i < BATCH_SIZE; i += 1) {
          const text = MOCK_LINES[cursor % MOCK_LINES.length] ?? ''
          cursor += 1
          batch.push({
            seq: seq++,
            timestamp: new Date().toISOString(),
            line: text
          })
        }
        onBatch(batch)
      }

      const timer: number = window.setInterval(tick, PUSH_INTERVAL)

      return () => {
        window.clearInterval(timer)
      }
    }
  }
}

/**
 * 模拟的资源采样流。
 */
export interface MockStatsStream {
  /**
   * 开始推送。
   *
   * @param containerId 容器 ID。
   * @param onSample 采样回调。
   * @returns 停止函数。
   */
  start(containerId: string, onSample: (sample: ContainerStatsSample) => void): () => void
}

/**
 * 创建模拟资源采样流。
 *
 * @returns 采样流。
 */
export function createMockStatsStream(): MockStatsStream {
  return {
    start(containerId, onSample) {
      let cpu = 12
      let memory = 64 * 1024 * 1024
      const limit = 512 * 1024 * 1024
      let rx = 1024 * 1024
      let tx = 256 * 1024
      // 速率需要「上一帧」：首帧与后端一样给 null，否则 sparkline 会以一个假值开头
      let previous: { rx: number; tx: number; at: number } | null = null

      const tick = (): void => {
        // 模拟波动：CPU 在 5%–60% 之间游走，内存缓慢爬升。
        cpu = Math.min(60, Math.max(2, cpu + (Math.random() - 0.5) * 8))
        memory = Math.min(limit, memory + Math.round(Math.random() * 4 * 1024 * 1024))
        rx += Math.round(Math.random() * 64 * 1024)
        tx += Math.round(Math.random() * 16 * 1024)

        const at = Date.now()
        const seconds = previous === null ? 0 : (at - previous.at) / 1000

        const rxRate =
          previous !== null && seconds > 0 ? Math.round((rx - previous.rx) / seconds) : null
        const txRate =
          previous !== null && seconds > 0 ? Math.round((tx - previous.tx) / seconds) : null

        previous = { rx, tx, at }

        onSample({
          containerId,
          cpuPercent: Number(cpu.toFixed(1)),
          memoryUsage: memory,
          memoryLimit: limit,
          networkRxBytes: rx,
          networkTxBytes: tx,
          networkRxBytesPerSecond: rxRate,
          networkTxBytesPerSecond: txRate,
          timestamp: new Date(at).toISOString()
        })
      }

      const timer: number = window.setInterval(tick, 1000)

      return () => {
        window.clearInterval(timer)
      }
    }
  }
}

/**
 * 模拟的全局通知源。
 */
export interface MockNotificationSource {
  /**
   * 开始推送。
   *
   * @param onContainerDied 容器退出回调。
   * @param onProgress 镜像操作进度回调（带令牌）。
   * @returns 停止函数。
   */
  start(
    onContainerDied: (event: ContainerDiedEvent) => void,
    onProgress: (token: string, progress: { percent: number | null; status: string }) => void
  ): () => void
}

/**
 * 创建模拟通知源。
 *
 * @returns 通知源。
 */
export function createMockNotificationSource(): MockNotificationSource {
  return {
    start(onContainerDied, onProgress) {
      // 进度由调用方按令牌路由；mock 侧不主动产生——
      // 真实进度来自后端的拉取 / 构建过程，mock 里没有那个过程。
      void onProgress

      // 每 20 秒造一次「容器挂了」，验证铃铛与红点。
      const timer = window.setInterval(() => {
        onContainerDied({
          hostId: 'local',
          containerId: 'mock-container-0001',
          containerName: 'mock-web',
          exitCode: 137,
          outOfMemory: true,
          occurredAt: new Date().toISOString()
        })
      }, 20_000)

      return () => window.clearInterval(timer)
    }
  }
}
