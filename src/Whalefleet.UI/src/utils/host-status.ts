import type { HostStatus } from '@/types/api'

/**
 * 主机连通状态的语义映射。
 *
 * 之前 `online / offline / unknown` 三态的颜色与文案在 `StatusDot`、`HostCard`、
 * `HostSelector`、`HostStatusPanel` 四处各写一遍，已经漂移过一次（有的带 dark 变体、
 * 有的没有）。这里收口成一处纯函数，组件只拿 `tone` 再自行 `t()`。
 *
 * 刻意不引入 vue-i18n：返回 i18n **key** 而非文案，工具函数因此可以脱离组件单测。
 */

/** ok = 在线 / down = 离线 / idle = 未知 */
export type HostTone = 'ok' | 'down' | 'idle'

const TONE_BY_STATUS: Record<string, HostTone> = {
  online: 'ok',
  offline: 'down'
}

/** 任意状态值（可能为 undefined）→ 语义色调；未登记的一律当「未知」。 */
export function hostTone(status?: HostStatus | string | null): HostTone {
  return TONE_BY_STATUS[String(status ?? '')] ?? 'idle'
}

/** 状态文案对应的 i18n key（中文「在线」/ 英文「Online」由 locale 文件决定）。 */
export const HOST_TONE_LABEL_KEY: Record<HostTone, string> = {
  ok: 'host.status.online',
  down: 'host.status.offline',
  idle: 'host.status.unknown'
}

/**
 * 状态药丸的配色。
 *
 * 药丸由「柔和底色 + 同色系文字 + 同色系描边」三层构成：
 * 只染文字在密集列表里扫不出来，只染底色又容易糊成一团。
 */
export const HOST_TONE_CHIP: Record<HostTone, string> = {
  ok: 'bg-status-ok-soft/60 text-status-ok ring-status-ok/25',
  down: 'bg-status-down-soft/60 text-status-down ring-status-down/25',
  idle: 'bg-status-idle-soft/60 text-status-idle ring-status-idle/30'
}

/** 状态点的实心色（无底色时用，例如侧栏折叠态那个圆点）。 */
export const HOST_TONE_DOT: Record<HostTone, string> = {
  ok: 'bg-status-ok text-status-ok',
  down: 'bg-status-down text-status-down',
  idle: 'bg-status-idle text-status-idle'
}

/** 卡片左侧状态色条：在线绿色、离线红色、未知中性。 */
export const HOST_TONE_RAIL: Record<HostTone, string> = {
  ok: 'bg-status-ok',
  down: 'bg-status-down',
  idle: 'bg-status-idle'
}
