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
 * 容器在列表里的**展示状态**：把 Docker 原始状态与健康检查结果合成一个。
 *
 * ## 为什么要合成
 * 容器列表里「运行中」和「健康」是**两件事**，但它们回答的是同一个问题——
 * 「这个服务还好吗」。分成两列时，一行里出现「运行中 / 不健康」两个词，
 * 读者得自己判断哪个才是结论；合成一列后只有一个结论。
 *
 * 合成后的取值与 Docker 原始状态**不是一一对应**：`running` 可能是
 * 「运行中」（没配健康检查）也可能是「健康」（配了且通过）。
 */
export type ContainerDisplayState =
  | 'healthy'
  | 'unhealthy'
  | 'running'
  | 'stopped'
  | 'transition'
  | 'unknown'

/** 健康检查结果（与 `types/api.ts` 的 `ContainerHealthStatus` 同值，独立声明避免 util 依赖 API 层） */
export type ContainerHealthState = 'none' | 'starting' | 'healthy' | 'unhealthy'

/**
 * 合成「运行状态 + 健康检查」→ 展示状态。
 *
 * ⚠️ **先判是否在运行**：停掉的容器没有健康检查可言（健康状态是 `none`），
 * 先判 `running` 才不会把一个停掉的容器显示成「运行中」。
 * 反过来，**运行中**的容器才谈得上健康：健康 / 不健康只覆盖配了健康检查的那部分。
 *
 * @param state Docker 原始状态文本（如 `running` / `exited`）
 * @param health 健康检查结果；`null` 表示「还没查到」
 */
export function containerDisplayState(
  state?: string | null,
  health?: ContainerHealthState | null
): ContainerDisplayState {
  const normalized = String(state ?? '').toLowerCase()

  if (normalized === 'running') {
    if (health === 'healthy') return 'healthy'
    // ⚠️ 不健康是**黄**不是红：容器还在跑，红会让人以为它已经挂了。
    // 「不健康」是「有问题但还活着」，红色会把「重启它」变成「立刻处理」。
    if (health === 'unhealthy') return 'unhealthy'
    return 'running'
  }

  const tone = containerTone(state)
  if (tone === 'down') return 'stopped'
  if (tone === 'warn') return 'transition'

  return 'unknown'
}

/** 展示状态 → 语义色调 */
export const CONTAINER_DISPLAY_TONE: Record<ContainerDisplayState, ContainerTone> = {
  healthy: 'ok',
  unhealthy: 'warn',
  running: 'ok',
  stopped: 'down',
  transition: 'warn',
  unknown: 'idle'
}

/** 展示状态 → 文案的 i18n key */
export const CONTAINER_DISPLAY_LABEL_KEY: Record<ContainerDisplayState, string> = {
  healthy: 'container.health.healthy',
  unhealthy: 'container.health.unhealthy',
  running: 'container.state.running',
  stopped: 'container.state.stopped',
  transition: 'container.state.transition',
  unknown: 'container.state.unknown'
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
