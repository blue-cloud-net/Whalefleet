/**
 * 容器状态的语义映射。
 *
 * 与 `host-status.ts` 同构：颜色与文案 key 收口在一处纯函数，组件只拿 `tone` 再自行 `t()`。
 * 单独一个文件而不是塞进 `host-status.ts`——两者的状态集合、语义色含义都不同
 * （主机是「这台机器连不连得上」，容器是「这个实例处于哪个生命周期阶段」）。
 *
 * 刻意不引入 vue-i18n：返回 i18n **key** 而非文案，因此可以脱离组件单测。
 */

import { buildPortUrl, ipRank, isWildcardIp } from '@/utils/port-link'

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
 * 端口映射的最小结构（与 `types/api.ts` 的 `ContainerPort` 同形，独立声明避免 util 依赖 API 层）。
 */
export interface PortLike {
  /** 容器内端口 */
  privatePort: number
  /** 宿主端口；未映射时为 `null` */
  publicPort: number | null
  /** 协议（`tcp` / `udp`） */
  type: string
  /** 绑定的宿主地址；未绑定时为 `null` */
  ip: string | null
}

/**
 * 已映射到宿主的端口（`publicPort` 必然有值）。
 *
 * `describePorts` 入口先把未映射的那些滤掉，之后全链路都用这个类型——
 * 这样「一定非空」的事实由**类型**表达，而不靠每处再判一次 `null`。
 */
type MappedPort = PortLike & { publicPort: number }

/**
 * 一组端口的展示结果。
 *
 * 一组 = 同一对（宿主端口、容器端口、协议）。同一个端口被绑到多个地址时
 * （`-p 8080:80` 会被 Docker 记成 `0.0.0.0` 与 `::` 两条）合并成**一行**。
 */
export interface PortDisplay {
  /** 展示文本（如 `8080→80/tcp` 或 `192.168.1.10:8080→80/tcp`） */
  text: string
  /** 可打开的地址；`null` 表示不能点（非 TCP 协议） */
  url: string | null
  /** 同组内其余的**具体**绑定地址（界面显示成「+N」，tooltip 里列出来） */
  extraIps: string[]
}

/**
 * 把端口列表整理成「一行一个映射」的展示结果。
 *
 * ## 未映射的端口**不显示**
 *
 * 守护进程会把镜像 `EXPOSE` 过的端口一并报回来，即使它**没有**映射到宿主
 * （`publicPort` 为 `null`）。MySQL 官方镜像就是典型：它 `EXPOSE` 了
 * 3306（MySQL 协议）与 **33060**（X Protocol），而实际部署只会映射 3306——
 * 于是每一行 MySQL 都凭空多出一行 `33060/tcp`。
 *
 * 那行信息是**纯噪声**：没映射到宿主的端口，从宿主机外根本连不上，
 * 列出来只会让人以为「这里还有个服务」，
 * 进而去排查一个并不存在的问题。
 * 因此这一列只展示**真正映射到宿主**的端口。
 *
 * ## 为什么要合并
 * Docker 对 `-p 8080:80` 会记**两条**：一条 `0.0.0.0`（IPv4）、一条 `::`（IPv6）。
 * 逐条渲染时同一个端口占两行，而且两行长得几乎一样——用户会以为是两个端口。
 * 合并后一行就够，且保留「绑到了哪些具体地址」这个信息。
 *
 * ## 展示与链接用的是**两套地址**
 * - **展示**：只显示**具体**地址（`192.168.1.10`）。通配地址（`0.0.0.0` / `::`）
 *   只表达「监听所有网卡」，把它写进文本是噪声，还会让用户以为「访问 `0.0.0.0` 就能连上」。
 * - **链接**：用**指定**的那个地址，通配也照用（用户明确要求「用指定 IP，即使 0.0.0.0」）。
 *
 * @param ports 端口映射列表
 * @returns 展示结果列表
 */
export function describePorts(ports: PortLike[]): PortDisplay[] {
  // ⚠️ **先丢掉未映射的端口**（见上文「未映射的端口不显示」）。
  // 过滤放在分组之前：一组里若混着未映射的成员，按 `publicPort` 做的分组键
  // 也会与已映射的那条对不上。
  const mapped = ports.filter((port): port is MappedPort => port.publicPort !== null)

  if (mapped.length === 0) return []

  // 分组：同一组 = 同一对（宿主端口、容器端口、协议）
  const groups = new Map<string, MappedPort[]>()

  for (const port of mapped) {
    const key = `${port.publicPort}|${port.privatePort}|${port.type}`
    const bucket = groups.get(key)

    if (bucket) bucket.push(port)
    else groups.set(key, [port])
  }

  const result: {
    display: PortDisplay
    publicPort: number
    privatePort: number
    type: string
  }[] = []

  for (const bucket of groups.values()) {
    const first = bucket[0]
    // 分组是从 map 里取出来的，理论上非空；但 `noUncheckedIndexedAccess` 下
    // 索引访问的类型是 `T | undefined`，而这里拿到 undefined 就直接跳过。
    if (first === undefined) continue

    const grouped = describePortGroup(bucket, first)

    result.push({
      display: { text: grouped.text, url: grouped.url, extraIps: grouped.extraIps },
      publicPort: grouped.publicPort,
      privatePort: grouped.privatePort,
      type: grouped.type
    })
  }

  // 排序：宿主端口 ↑ → 容器端口 ↑ → 协议
  result.sort((a, b) => {
    const byPublic = a.publicPort - b.publicPort
    if (byPublic !== 0) return byPublic

    return a.privatePort - b.privatePort || a.type.localeCompare(b.type)
  })

  return result.map((item) => item.display)
}

/**
 * 端口展示结果的内部形态（排序时要用到分组里的数字）。
 */
interface PortDisplayInternal extends PortDisplay {
  /** 宿主端口（**必然有值**：未映射的端口已在入口被滤掉） */
  publicPort: number
  /** 容器内端口 */
  privatePort: number
  /** 协议 */
  type: string
}

/**
 * 整理一组端口（同一对宿主端口 / 容器端口 / 协议）。
 *
 * @param bucket 组内成员
 * @param first 组内第一条（已确保存在且已映射）
 * @returns 展示结果
 */
function describePortGroup(bucket: MappedPort[], first: MappedPort): PortDisplayInternal {
  const target = `${first.privatePort}/${first.type}`

  // 具体地址（非通配）：进展示文本
  const specifics = bucket.filter((port) => !isWildcardIp(port.ip))
  // 链接地址：取「最该用」的那个——具体 IPv4 优先，其次 IPv4 通配
  const linkIp = pickBestIp(bucket)

  const shownIp = specifics.length > 0 ? pickBestIp(specifics) : null
  const text = shownIp ? `${shownIp}:${first.publicPort}→${target}` : `${first.publicPort}→${target}`

  // 其余的具体地址：界面显示成「+N」，tooltip 里列出来
  const extraIps = specifics
    .map((port) => port.ip ?? '')
    .filter((ip) => ip.length > 0 && ip !== shownIp)

  // ⚠️ **只有 TCP 才给链接**：UDP / SCTP 没有浏览器能直接打开的语义，
  // 做成链接只会让人点开发现「打不开」，而那与服务是否健康无关。
  const url = first.type === 'tcp' && linkIp ? buildPortUrl(linkIp, first.publicPort) : null

  return {
    ...baseDisplay(text),
    url,
    extraIps,
    publicPort: first.publicPort,
    privatePort: first.privatePort,
    type: first.type
  }
}

/**
 * 造一个只有文本的展示结果骨架。
 *
 * @param text 展示文本
 * @returns 骨架
 */
function baseDisplay(text: string): Omit<PortDisplayInternal, 'publicPort' | 'privatePort' | 'type'> {
  return { text, url: null, extraIps: [] }
}

/**
 * 挑出「最该用」的那个绑定地址。
 *
 * @param candidates 候选地址所属的端口项
 * @returns 地址文本；一个都没有时返回 `null`
 *
 * @remarks
 * 排序等级见 `ipRank`：具体 IPv4 → IPv4 通配 → 具体 IPv6 → IPv6 通配 → 未指定。
 * 多个候选取等级最小者，等级相同时按地址文本序数比较——保证结果确定。
 */
function pickBestIp(candidates: PortLike[]): string | null {
  let best: { ip: string; rank: number } | null = null

  for (const candidate of candidates) {
    const ip = (candidate.ip ?? '').trim()
    if (ip.length === 0) continue

    const rank = ipRank(ip)

    if (best === null || rank < best.rank || (rank === best.rank && ip < best.ip)) {
      best = { ip, rank }
    }
  }

  return best?.ip ?? null
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
