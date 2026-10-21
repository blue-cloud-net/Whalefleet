import { onUnmounted, ref } from 'vue'
import { subscribeProgress } from '@/realtime/notification'
import type { ImageOperationProgress } from '@/types/api'

/**
 * 镜像操作（拉取 / 构建）的实时进度（0.5.0）。
 *
 * ## 为什么要客户端生成令牌
 * 拉取 / 构建是一个**独立的 HTTP 请求**，与任何 Hub 连接都没有天然关联——
 * 服务端没法知道「这次拉取对应哪条连接」。
 * 于是由客户端先生成一个随机令牌，三件事用它串起来：
 * 1. 订阅 `SubscribePull(token)`
 * 2. 把同一个令牌放进拉取 / 构建请求（`progressToken`）
 * 3. 服务端把进度推给订阅了它的连接
 *
 * ## 为什么进度走**全局**通知 Hub 而不是主机作用域的 Hub
 * 拉取 / 构建的进度**不属于某台主机**（虽然操作发生在某台主机上），
 * 而 Hub 的路径决定了权限判定。放在全局 Hub 上，进度不依赖主机可见性。
 */

/** 进度令牌的字符集（与后端 `NotificationHub.IsValidToken` 一致）。 */
const TOKEN_ALPHABET = 'abcdefghijklmnopqrstuvwxyz0123456789'

/** 令牌长度（后端要求至少 32 个字符）。 */
const TOKEN_LENGTH = 40

/**
 * 生成一个进度令牌。
 *
 * @returns 令牌。
 */
export function createProgressToken(): string {
  const bytes = new Uint8Array(TOKEN_LENGTH)
  crypto.getRandomValues(bytes)

  let token = ''
  for (const byte of bytes) {
    token += TOKEN_ALPHABET[byte % TOKEN_ALPHABET.length]
  }

  return token
}

/**
 * 订阅某次镜像操作的进度。
 *
 * @returns 进度状态与操作。
 */
export function useImageProgress() {
  const progress = ref<ImageOperationProgress | null>(null)
  const error = ref<string | null>(null)
  const connected = ref(false)

  /** 退订函数。 */
  let unsubscribe: (() => void) | null = null

  /**
   * 建立订阅。
   *
   * @param progressToken 进度令牌。
   */
  function start(progressToken: string): void {
    error.value = null
    connected.value = false

    unsubscribe?.()
    unsubscribe = subscribeProgress(progressToken, (value) => {
      progress.value = value
      connected.value = true
    })
  }

  /**
   * 停止订阅。
   */
  function stop(): void {
    unsubscribe?.()
    unsubscribe = null
    connected.value = false
  }

  onUnmounted(stop)

  return { progress, error, connected, start, stop }
}
