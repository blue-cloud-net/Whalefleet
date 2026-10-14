/**
 * 容器状态的语义映射。
 *
 * 与 `host-status.ts` 同构：颜色与文案 key 收口在一处纯函数，组件只拿 `tone` 再自行 `t()`。
 * 单独一个文件而不是塞进 `host-status.ts`——两者的状态集合、语义色含义都不同
 * （主机是「这台机器连不连得上」，容器是「这个实例处于哪个生命周期阶段」）。
 *
 * 刻意不引入 vue-i18n：返回 i18n **key** 而非文案，因此可以脱离组件单测。
 */

/**
 * 语义色调。
 * ok = 运行中 / down = 已停止 / warn = 过渡态（重启、创建中）/ idle = 未知
 */
export type ContainerTone = 'ok' | 'down' | 'warn' | 'idle'

/**
 * Docker 状态文本 → 语义色调。
 *
 * ⚠️ 这张表**不完整是有意的**：状态取值来自守护进程，新版本 Docker 会加新的。
 * 未登记的落到 `idle`（灰）而不是随便挑一个颜色——猜错的颜色比灰色更容易误导。
 * 新增状态时补这一处即可，组件不需要改。
 */
const TONE_BY_STATE: Record<string, ContainerTone> = {
  running: 'ok',
  exited: 'down',
  dead: 'down',
  paused: 'warn',
  restarting: 'warn',
  created: 'idle',
  removing: 'warn'
}

/**
 * 任意状态文本 → 语义色调；未登记的一律当「未知」。
 *
 * @param state Docker 原始状态文本（如 `running`）
 */
export function containerTone(state?: string | null): ContainerTone {
  return TONE_BY_STATE[String(state ?? '').toLowerCase()] ?? 'idle'
}

/**
 * 状态文案对应的 i18n key。
 *
 * 只覆盖**已登记**的状态；未登记的用 `container.state.unknown`（组件里
 * 按 `containerTone` 之外的判断走它）。刻意不把 Docker 的原始文本当作展示文案——
 * 界面上出现一个没解释的英文单词，比显示「未知」更难懂。
 */
export const CONTAINER_TONE_LABEL_KEY: Record<ContainerTone, string> = {
  ok: 'container.state.running',
  down: 'container.state.stopped',
  warn: 'container.state.transition',
  idle: 'container.state.unknown'
}

/**
 * 端口的展示文本。
 *
 * 三种形态缺一不可，少一种就会误导：
 * - 有宿主端口 → `0.0.0.0:8080→80/tcp`（宿主的在左，容器的在右，与 `docker ps` 一致）
 * - 无宿主端口 → `80/tcp`（已暴露但未映射，只显示容器内那一侧）
 * - 无绑定地址 → 不显示 `0.0.0.0:`，否则会让用户以为它只在所有网卡上——
 *   后端已经会去掉 `0.0.0.0` / `::` 这类「等于没指定」的默认值了。
 *
 * @param hostIp 宿主地址；`null` / 空串表示「所有网卡」，不展示
 */
export function formatPort(port: {
  privatePort: number
  publicPort: number | null
  type: string
  ip: string | null
}): string {
  const target = `${port.privatePort}/${port.type}`
  const bind = port.ip ? `${port.ip}:` : ''

  return port.publicPort == null ? target : `${bind}${port.publicPort}→${target}`
}

/**
 * 字节数 → 人类可读大小。
 *
 * 单位文案由调用方传入（走 i18n），本模块不硬编码任何语言——
 * 与 `datetime.ts` 的做法一致。
 */
export function formatBytes(bytes: number | null | undefined, units: ByteUnits): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes < 0) return '—'

  const KIB = 1024
  const MIB = KIB * 1024
  const GIB = MIB * 1024

  if (bytes >= GIB) return `${(bytes / GIB).toFixed(1)} ${units.gib}`
  if (bytes >= MIB) return `${(bytes / MIB).toFixed(1)} ${units.mib}`
  if (bytes >= KIB) return `${(bytes / KIB).toFixed(1)} ${units.kib}`

  return `${Math.round(bytes)} ${units.b}`
}

/** 字节单位的 i18n 文案 */
export interface ByteUnits {
  b: string
  kib: string
  mib: string
  gib: string
}
