import { computed, onUnmounted, ref } from 'vue'
import { subscribeProgress } from '@/realtime/notification'
import { createProgressToken } from '@/composables/useImageProgress'
import { getStackTask } from '@/api/stack'
import type { OperationProgress, StackTaskSnapshot } from '@/types/api'

/**
 * 编排任务（0.10.0）的进度跟踪。
 *
 * ## 为什么「实时推送 + 轮询」两条腿走路
 * - **实时推送**给的是进度条（百分比与当前步骤），走的是与镜像操作同一条
 *   `OnOperationProgress` 通道——它不需要额外连接（通知 Hub 是共享的）。
 * - **轮询任务快照**给的是**终态与错误**（`succeeded` / `failed` + 失败原因）。
 *
 * ⚠️ **只有轮询能给出终态**：进度通道在成功结束时只推一条 `done=true`，
 * 而失败的那条 `error` 并不携带后端的错误码与完整原因；
 * 任务快照才有 `errorCode` / `errorMessage`。
 * 只订阅不轮询的话，界面永远停在「等待中」。
 *
 * ## 为什么令牌由客户端生成
 * 与镜像拉取同一理由：服务端没法知道「这次部署对应哪条 Hub 连接」。
 * 客户端先生成令牌，订阅它，再把它当作 `taskId` 带进请求，三者由此串起来。
 */

/** 轮询间隔（毫秒）。 */
const POLL_INTERVAL_MS = 1000

/** 轮询上限（毫秒）。超时后停止轮询，界面提示「仍在后台执行」。 */
const POLL_TIMEOUT_MS = 10 * 60 * 1000

/**
 * 编排任务的进度状态。
 *
 * @returns 任务状态与操作。
 */
export function useStackTask() {
  /** 最近一次任务快照。 */
  const task = ref<StackTaskSnapshot | null>(null)

  /** 实时进度（可能为 null——推送比轮询先到或后到都正常）。 */
  const progress = ref<OperationProgress | null>(null)

  /** 失败原因（任务失败时）。 */
  const error = ref<string | null>(null)

  /** 是否正在等待任务结束。 */
  const busy = ref(false)

  /** 退订函数。 */
  let unsubscribe: (() => void) | null = null

  /** 轮询计时器。 */
  let timer: ReturnType<typeof setTimeout> | null = null

  /** 当前任务标识。 */
  let currentTaskId: string | null = null

  /** 本次运行是否已被取消（组件卸载 / 新的操作覆盖）。 */
  let cancelled = false

  /** 任务是否处于终态。 */
  const terminal = computed(
    () => task.value?.status === 'succeeded' || task.value?.status === 'failed'
  )

  /** 进度百分比：优先用快照的，其次用实时推送的。 */
  const percent = computed(() => {
    if (typeof task.value?.percent === 'number') return task.value.percent
    if (typeof progress.value?.percent === 'number') return progress.value.percent

    return null
  })

  /** 当前步骤描述。 */
  const step = computed(() => task.value?.step ?? progress.value?.status ?? null)

  /**
   * 停止轮询与订阅。
   */
  function stop(): void {
    cancelled = true

    if (timer) {
      clearTimeout(timer)
      timer = null
    }

    unsubscribe?.()
    unsubscribe = null
  }

  /**
   * 轮询任务快照直到终态。
   *
   * @param hostId 主机标识。
   * @param taskId 任务标识。
   * @returns 终态快照；超时或出错时返回当前快照。
   */
  async function poll(hostId: string, taskId: string): Promise<StackTaskSnapshot | null> {
    const deadline = Date.now() + POLL_TIMEOUT_MS

    while (!cancelled && Date.now() < deadline) {
      try {
        const snapshot = await getStackTask(hostId, taskId)

        if (snapshot) {
          task.value = snapshot

          if (snapshot.status === 'succeeded' || snapshot.status === 'failed') {
            return snapshot
          }
        }
      } catch {
        // 单次轮询失败不放弃整个任务——可能只是网络抖动。
        // 任务仍在后台执行，界面上保留最后一次成功的快照。
      }

      await new Promise<void>((resolve) => {
        timer = setTimeout(resolve, POLL_INTERVAL_MS)
      })
    }

    return task.value
  }

  /**
   * 发起一次编排操作并跟踪到终态。
   *
   * @param hostId 主机标识。
   * @param action 受理动作：拿到任务标识后调用对应端点。
   * @param onFinished 任务结束后的回调（通常是刷新列表）。
   * @returns 终态快照。
   */
  async function run(
    hostId: string,
    action: (taskId: string) => Promise<unknown>,
    onFinished?: () => void | Promise<void>
  ): Promise<StackTaskSnapshot | null> {
    stop()
    cancelled = false

    busy.value = true
    error.value = null
    task.value = null
    progress.value = null

    const taskId = createProgressToken()
    currentTaskId = taskId

    unsubscribe = subscribeProgress(taskId, (value) => {
      progress.value = value
    })

    try {
      await action(taskId)

      const snapshot = await poll(hostId, taskId)

      if (snapshot?.status === 'failed') {
        error.value = snapshot.errorMessage ?? ''
      }

      await onFinished?.()

      return snapshot
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
      return null
    } finally {
      busy.value = false
      stop()
    }
  }

  onUnmounted(() => {
    stop()
  })

  return {
    task,
    progress,
    error,
    busy,
    terminal,
    percent,
    step,
    currentTaskId,
    run,
    stop
  }
}
