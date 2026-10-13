/**
 * 日期时间格式化工具。
 *
 * 刻意不引入 dayjs / date-fns —— 本项目只需要「运行时长」与「绝对时间」两种展示，
 * 自实现可避免为一个格式化函数引入依赖。
 *
 * 约定：入参缺失或非法时统一返回 `EMPTY`，由调用方决定是否渲染占位。
 */

/** 缺失 / 非法值的统一占位符 */
export const EMPTY = '—'

const MINUTE = 60
const HOUR = MINUTE * 60
const DAY = HOUR * 24

/**
 * 解析 ISO 8601 字符串为 Date；空值或非法值返回 null。
 */
function parse(iso: string | undefined | null): Date | null {
  if (!iso) return null
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? null : d
}

/**
 * 把毫秒时长格式化为「3 天 4 小时」这类紧凑时长。
 *
 * 最多两级单位（天 + 小时 / 小时 + 分钟 / 分钟 + 秒），避免在窄卡片里换行。
 * 未来时间（时钟回拨等）返回 `EMPTY` 而非负数。
 *
 * 单位文案由调用方传入（走 i18n），本模块不硬编码任何语言。
 */
export function formatDuration(ms: number, units: DurationUnits): string {
  if (!Number.isFinite(ms) || ms < 0) return EMPTY

  const totalSeconds = Math.floor(ms / 1000)
  const days = Math.floor(totalSeconds / DAY)
  const hours = Math.floor((totalSeconds % DAY) / HOUR)
  const minutes = Math.floor((totalSeconds % HOUR) / MINUTE)
  const seconds = totalSeconds % MINUTE

  const join = (value: number, unit: string) => `${value} ${unit}`

  if (days > 0) return hours > 0 ? `${join(days, units.day)} ${join(hours, units.hour)}` : join(days, units.day)
  if (hours > 0) {
    return minutes > 0
      ? `${join(hours, units.hour)} ${join(minutes, units.minute)}`
      : join(hours, units.hour)
  }
  if (minutes > 0) return join(minutes, units.minute)
  return join(seconds, units.second)
}

/** 运行时长的单位文案（由调用方从 i18n 传入） */
export interface DurationUnits {
  day: string
  hour: string
  minute: string
  second: string
}

/**
 * 计算「已运行」时长。
 *
 * @param startedAt 启动时间（ISO 8601）
 * @param units 单位文案（i18n）
 * @param now 参考时间，默认当前时间；测试可注入固定值
 */
export function formatUptime(
  startedAt: string | undefined | null,
  units: DurationUnits,
  now: number = Date.now()
): string {
  const start = parse(startedAt)
  if (!start) return EMPTY
  return formatDuration(now - start.getTime(), units)
}

/**
 * 格式化绝对时间（按浏览器本地时区）。
 *
 * 例：`2026-10-10 14:32:05`。入参缺失 / 非法时返回 `EMPTY`。
 */
export function formatDateTime(iso: string | undefined | null): string {
  const d = parse(iso)
  if (!d) return EMPTY

  const pad = (n: number) => String(n).padStart(2, '0')
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
  )
}