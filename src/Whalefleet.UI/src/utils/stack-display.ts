import type { StackServicePort, StackStatus } from '@/types/api'

/**
 * 编排状态的展示辅助。
 *
 * ## 为什么单独一个模块
 * 编排页与详情页都要把同一个状态映射成「色调 + 文案」，
 * 而文案必须走 i18n（不能在这里拼死）——因此这里只产出
 * **i18n key 与色调**，文案由调用方 `t()` 出来。
 *
 * ## 为什么 `partial` 是 `warn` 而不是 `down`
 * 「部分服务没起来」既不是「全好」也不是「全停」，
 * 用 `down` 会让它和「已停止」长得一样，而两者的处置完全不同：
 * 前者要去看「哪个服务挂了」，后者要重新启动。
 */

/** 编排状态 → 语义色调。 */
export function stackTone(status: StackStatus): 'ok' | 'down' | 'warn' | 'idle' {
  switch (status) {
    case 'running':
      return 'ok'
    case 'partial':
      return 'warn'
    case 'stopped':
      return 'down'
    default:
      return 'idle'
  }
}

/**
 * 编排状态 → i18n key。
 *
 * @param status 状态。
 * @returns i18n key。
 *
 * @remarks
 * ⚠️ **返回 key 而不是文案**：文案要能被翻译，
 * 而同一句描述会出现在列表、详情与通知三处。
 */
export function stackStatusKey(status: StackStatus): string {
  switch (status) {
    case 'running':
      return 'stack.status.running'
    case 'partial':
      return 'stack.status.partial'
    case 'stopped':
      return 'stack.status.stopped'
    default:
      return 'stack.status.unknown'
  }
}

/**
 * 判断状态是否需要提醒。
 *
 * @param status 状态。
 * @returns 需要提醒返回 `true`。
 *
 * @remarks
 * 「部分服务没起来」是最需要被看见的状态——它容易被当成「正常」而一直放着。
 */
export function stackNeedsAttention(status: StackStatus): boolean {
  return status === 'partial'
}

/**
 * 把编排服务的端口列表拼成一行文本。
 *
 * ## 为什么不复用容器的 `describePorts`
 * 那个函数的输入是守护进程给的端口记录（`publicPort` / `privatePort` / `type` / `ip`），
 * 而编排这边是 **YAML 解析出来的**端口声明（`hostPort` / `containerPort` / `protocol` / `hostIp`）——
 * 字段名与语义都不同（编排这边没有「未映射」这种状态：声明了就是声明了）。
 * 硬套过来会得到一个处处为 `null` 的展示。
 *
 * ## 展示格式
 * `宿主端口→容器端口/协议`，多个用 `、` 分隔。
 * 没写宿主端口（只暴露不映射）时只显示 `容器端口/协议`——
 * 但那类端口从宿主机外连不上，因此**不加链接**。
 *
 * @param ports 端口列表。
 * @returns 展示文本；没有端口时返回空串。
 */
export function describeStackPorts(ports: StackServicePort[]): string {
  if (ports.length === 0) return ''

  return ports
    .map((port) => {
      const target = `${port.containerPort}/${port.protocol}`

      return port.hostPort === null ? target : `${port.hostPort}→${target}`
    })
    .join('、')
}
