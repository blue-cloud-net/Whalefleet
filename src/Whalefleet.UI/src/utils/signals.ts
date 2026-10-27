/**
 * 容器终止信号（0.7.0）。
 *
 * ## 为什么要有这个模块
 *
 * `docker kill` 的语义是「把一个 POSIX 信号送到容器主进程」。
 * 默认是 `SIGKILL`（直接终结），但也允许 `SIGHUP` 这类**非终止信号**——
 * 而后者可能根本不会让容器退出（那取决于容器主进程怎么处理信号）。
 *
 * 因此界面上必须能选信号，而且必须把「非终止信号可能不终止容器」这件事写出来，
 * 否则用户会以为「点了终止就没了」，而容器还在跑。
 *
 * ## 与后端的分工
 *
 * 本模块只做**输入合法性**的本地校验（非空 / 无空白 / 长度上限），
 * 与后端的 `RequireSignal` 同口径。
 * 信号**是否存在**由守护进程判定（信号集来自内核的 `signal(7)`，
 * 本地维护一张白名单等于把内核的表拓一份——它会过时，
 * 而过时后会把「合法的自定义信号」误报成非法）。
 */

/** 常用信号（与 `signal(7)` 的名称一致，`SIG` 前缀可省略）。 */
export interface SignalOption {
  /** 信号名，如 `SIGKILL` */
  value: string
  /** i18n key（描述这个信号对容器意味着什么） */
  labelKey: string
}

/**
 * 常用信号清单。
 *
 * 顺序即下拉框里的顺序：先放「一定会终止」的，再放「不一定终止」的——
 * 用户的默认预期是「终止」，因此最可能想要的 SIGKILL 排在最前。
 */
export const COMMON_SIGNALS: readonly SignalOption[] = [
  { value: 'SIGKILL', labelKey: 'container.signal.sigkill' },
  { value: 'SIGTERM', labelKey: 'container.signal.sigterm' },
  { value: 'SIGINT', labelKey: 'container.signal.sigint' },
  { value: 'SIGQUIT', labelKey: 'container.signal.sigquit' },
  { value: 'SIGHUP', labelKey: 'container.signal.sighup' },
  { value: 'SIGUSR1', labelKey: 'container.signal.sigusr1' },
  { value: 'SIGUSR2', labelKey: 'container.signal.sigusr2' }
]

/** 默认信号：与 `docker kill` 的默认一致。 */
export const DEFAULT_SIGNAL = 'SIGKILL'

/** 信号长度上限（与后端一致）。 */
export const MAX_SIGNAL_LENGTH = 32

/**
 * 校验信号输入。
 *
 * @param signal 用户输入（可能为空 / 含空白）。
 * @returns 合法时返回去空白后的信号；空输入返回 `null`（表示「用默认值」）。
 */
export function validateSignal(signal: string | null | undefined): string | null {
  if (signal === null || signal === undefined) return null

  const trimmed = signal.trim()

  if (trimmed.length === 0) return null
  if (trimmed.length > MAX_SIGNAL_LENGTH) return null
  if (/\s/.test(trimmed)) return null

  return trimmed
}

/**
 * 信号是否属于「**不保证终止**」的那一类。
 *
 * ⚠️ 这不是一个安全判断，只是给界面一个「要不要提醒」的依据：
 * 非终止信号发过去，容器**可能**继续运行（取决于它的主进程怎么处理信号）。
 */
export function isNonTerminating(signal: string | null | undefined): boolean {
  if (!signal) return false

  const normalized = signal.trim().toUpperCase().replace(/^SIG/, '')

  // SIGKILL / SIGTERM 是「终止类」；其余（INT / QUIT / HUP / USR* …）都可能只是通知。
  // SIGSTOP 同理——它甚至不让进程处理信号。
  return !['KILL', 'TERM'].includes(normalized)
}
