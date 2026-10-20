/**
 * 0.3.0 高级创建参数的**纯逻辑**组装（行式文本 → 请求片段）。
 *
 * ## 为什么单独成文件
 * 创建对话框已经很长了，而这一块是纯函数（给定文本行，产出结构化片段）、
 * 不碰任何响应式状态。放在组件里会让「哪些字段会发出去、哪些被丢弃」这件事
 * 淹没在大段模板与提交逻辑中间。
 *
 * 纯函数也让 vitest 可以直接单测行解析，不必挂载组件。
 *
 * ## 一条总的约定：**空输入 = 不传**
 * 与 `container-input.ts` 一致。空输入不发字段而不是发空数组/空串——
 * 后者会被守护进程当成「显式清空」（例如空 `cmd` 会让容器不带任何启动命令），
 * 而用户的意思只是「我没填」。
 */

/** 内存下限：6 MiB（Docker 守护进程硬性下限，实测 6291455 被拒）。 */
export const MIN_MEMORY_LIMIT_BYTES = 6 * 1024 * 1024

/** CPU 配额上限：1024 核。 */
export const MAX_NANO_CPUS = 1024 * 1_000_000_000

/** 设备权限允许的字符。 */
const DEVICE_PERMISSION_CHARS = /^[rwm]+$/

/** 本行解析的结果：合法片段、错误、或「忽略空行」。 */
export type LineParse<T> =
  | { kind: 'ok'; value: T }
  | { kind: 'skip' }
  | { kind: 'error'; reason: 'format' | 'duplicate' }

/**
 * 解析 capability 列表（每行一个）。
 *
 * ⚠️ **空项是错误而不是「跳过」**：前端一个空输入框提交上来就是 `["", "NET_ADMIN"]`，
 * 而空项几乎总是拼装逻辑出错。静默跳过会让用户以为「我勾的那项生效了」。
 *
 * ⚠️ 因此这里**不能**先用 `splitLines` 把空行滤掉：滤掉之后「空项检查」永远不成立，
 * 而看起来它是在工作的（测试里那条 `NET_ADMIN\n\nSYS_PTRACE` 就是这么漏过去的）。
 */
export function parseCapabilities(text: string): LineParse<string[]> {
  const raw = text.split('\n').map((line) => line.trim())

  // ⚠️ 只有「整个输入都是空白」才是 skip；中间或末尾夹一个空行是 error
  const meaningful = raw.filter((line) => line.length > 0)

  if (meaningful.length === 0) return { kind: 'skip' }
  if (meaningful.length !== raw.length) return { kind: 'error', reason: 'format' }

  const seen = new Set<string>()

  for (const value of meaningful) {
    if (!/^[A-Z][A-Z0-9_-]*$/.test(value)) return { kind: 'error', reason: 'format' }
    if (seen.has(value)) return { kind: 'error', reason: 'duplicate' }

    seen.add(value)
  }

  return { kind: 'ok', value: meaningful }
}

/**
 * 校验能力增删列表不冲突（既增又删同一项）。
 *
 * Docker 不报错（两个列表都会发过去），生效哪一个取决于守护进程内部的合并顺序。
 * 这类请求几乎总是拼错了，在提交前拦下比让用户猜结果可靠。
 */
export function hasCapabilityConflict(add: string[], drop: string[]): boolean {
  const dropped = new Set(drop)

  return add.some((capability) => dropped.has(capability))
}

/** sysctl 输入：`key=value` 每行一对。 */
export function parseSysctls(
  text: string
): LineParse<Record<string, string>> {
  const lines = splitLines(text)

  if (lines.length === 0) return { kind: 'skip' }

  const result: Record<string, string> = {}

  for (const line of lines) {
    const equals = line.indexOf('=')

    // 判据是**第一个**等号：值里可以有 `=`（如 `net.ipv4.ip_forward=1` 之外的场景）
    if (equals <= 0) return { kind: 'error', reason: 'format' }

    const key = line.slice(0, equals).trim()

    if (key.length === 0) return { kind: 'error', reason: 'format' }
    if (key in result) return { kind: 'error', reason: 'duplicate' }

    // 值允许为空串（部分 sysctl 置空即恢复默认）
    result[key] = line.slice(equals + 1).trim()
  }

  return { kind: 'ok', value: result }
}

/** ulimit 输入：`name` / `name=soft` / `name=soft:hard` 每行一条（与 `docker run --ulimit` 同形）。 */
export function parseUlimits(
  text: string
): LineParse<Array<{ name: string; soft?: number; hard?: number }>> {
  const lines = splitLines(text)

  if (lines.length === 0) return { kind: 'skip' }

  const seen = new Set<string>()
  const result: Array<{ name: string; soft?: number; hard?: number }> = []

  for (const line of lines) {
    const equals = line.indexOf('=')
    const name = (equals < 0 ? line : line.slice(0, equals)).trim()

    if (name.length === 0) return { kind: 'error', reason: 'format' }
    if (seen.has(name)) return { kind: 'error', reason: 'duplicate' }

    seen.add(name)

    if (equals < 0) {
      result.push({ name })
      continue
    }

    const rawLimits = line.slice(equals + 1).trim()

    if (rawLimits.length === 0) return { kind: 'error', reason: 'format' }

    // 软硬用 `:` 分隔（`nofile=1024:2048`）；软硬缺一时后端会互相补齐，
    // 这里保持原样发出去，让「只给一个」这个意图显式可见。
    const [rawSoft, rawHard] = rawLimits.split(':', 2)
    const soft = Number(rawSoft)
    const hard = rawHard === undefined ? undefined : Number(rawHard)

    if (!Number.isInteger(soft) || soft < 0) return { kind: 'error', reason: 'format' }
    if (hard !== undefined && (!Number.isInteger(hard) || hard < 0)) {
      return { kind: 'error', reason: 'format' }
    }

    result.push({ name, soft, ...(hard === undefined ? {} : { hard }) })
  }

  return { kind: 'ok', value: result }
}

/** 设备映射输入：`/dev/snd` / `/dev/snd:/dev/snd:r` 每行一条。 */
export function parseDevices(
  text: string
): LineParse<
  Array<{ hostPath: string; containerPath?: string; permissions?: string }>
> {
  const lines = splitLines(text)

  if (lines.length === 0) return { kind: 'skip' }

  const result: Array<{ hostPath: string; containerPath?: string; permissions?: string }> = []

  for (const line of lines) {
    const [hostPath, containerPath, permissions] = line.split(':')

    if (hostPath === undefined || hostPath.trim().length === 0) {
      return { kind: 'error', reason: 'format' }
    }

    if (permissions !== undefined && !DEVICE_PERMISSION_CHARS.test(permissions.trim())) {
      return { kind: 'error', reason: 'format' }
    }

    result.push({
      hostPath: hostPath.trim(),
      ...(containerPath !== undefined && containerPath.trim().length > 0
        ? { containerPath: containerPath.trim() }
        : {}),
      ...(permissions !== undefined && permissions.trim().length > 0
        ? { permissions: permissions.trim() }
        : {})
    })
  }

  return { kind: 'ok', value: result }
}

/**
 * 健康检查的「开关」状态。
 *
 * ⚠️ 三态而不是布尔：`undefined` = **不传**（继承镜像里的 `HEALTHCHECK`）；
 * `none` = 显式关闭（覆盖镜像）；否则是命令。
 * 压成布尔就分不出「继承」与「关闭」，而这两者在容器行为上完全不同。
 */
export type HealthCheckInput =
  | { kind: 'inherit' }
  | { kind: 'none' }
  | {
      kind: 'custom'
      test: string[]
      intervalSeconds?: number
      timeoutSeconds?: number
      startPeriodSeconds?: number
      retries?: number
    }

/**
 * 组装健康检查。
 *
 * @param enabled 用户是否勾了「自定义健康检查」。
 * @param command 检查命令（`CMD-SHELL …` / `CMD …`）。
 * @param numbers 四项可选数值（秒 / 次）。
 */
export function buildHealthCheck(
  enabled: boolean,
  command: string,
  numbers: {
    interval: string
    timeout: string
    startPeriod: string
    retries: string
  }
): LineParse<HealthCheckInput> {
  if (!enabled) return { kind: 'skip' }

  const trimmed = command.trim()

  // 命令为空的常见原因是勾了开关却没填命令——直接报错，
  // 而发一个 `test: []` 过去只会被后端拒成一句「healthCheck.test 不能为空」。
  if (trimmed.length === 0) return { kind: 'error', reason: 'format' }

  const parts = splitCommand(trimmed)

  if (parts.length === 0) return { kind: 'error', reason: 'format' }

  const interval = parsePositiveNumber(numbers.interval)

  if (numbers.interval.trim().length > 0 && interval === null) {
    return { kind: 'error', reason: 'format' }
  }

  const timeout = parsePositiveNumber(numbers.timeout)

  if (numbers.timeout.trim().length > 0 && timeout === null) {
    return { kind: 'error', reason: 'format' }
  }

  const startPeriod = parsePositiveNumber(numbers.startPeriod)

  if (numbers.startPeriod.trim().length > 0 && startPeriod === null) {
    return { kind: 'error', reason: 'format' }
  }

  const retries = parsePositiveNumber(numbers.retries)

  if (numbers.retries.trim().length > 0 && retries === null) {
    return { kind: 'error', reason: 'format' }
  }

  return {
    kind: 'ok',
    value: {
      kind: 'custom',
      test: parts,
      ...(interval !== null ? { intervalSeconds: Math.round(interval) } : {}),
      ...(timeout !== null ? { timeoutSeconds: Math.round(timeout) } : {}),
      ...(startPeriod !== null ? { startPeriodSeconds: Math.round(startPeriod) } : {}),
      ...(retries !== null ? { retries: Math.round(retries) } : {})
    }
  }
}

/**
 * 内存上限（MiB → 字节）。
 *
 * ⚠️ 创建与热更新在这里**允许 0**（= 不限制），与热更新端点不同——
 * 那是刻意的差异，见 `advanced-input.ts` 的 `parseMemoryLimit` 注释。
 */
export function parseMemoryMiB(input: string): LineParse<number> {
  const trimmed = input.trim()

  if (trimmed.length === 0) return { kind: 'skip' }

  const value = Number(trimmed)

  if (!Number.isInteger(value) || value < 0) return { kind: 'error', reason: 'format' }
  if (value > 0 && value * 1024 * 1024 < MIN_MEMORY_LIMIT_BYTES) {
    return { kind: 'error', reason: 'format' }
  }

  return { kind: 'ok', value: value * 1024 * 1024 }
}

/** CPU 配额（核 → 十亿分之一核）。 */
export function parseCpuCores(input: string): LineParse<number> {
  const trimmed = input.trim()

  if (trimmed.length === 0) return { kind: 'skip' }

  const value = Number(trimmed)

  if (!Number.isFinite(value) || value < 0) return { kind: 'error', reason: 'format' }
  if (value > 1024) return { kind: 'error', reason: 'format' }

  return { kind: 'ok', value: Math.round(value * 1e9) }
}

/** 日志驱动选项：`max-size=10m` 每行一对。 */
export function parseLogOptions(text: string): LineParse<Record<string, string>> {
  return parseSysctls(text)
}

/** 把文本拆成非空行（去空白、去空行）。 */
function splitLines(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}

/**
 * 按空白拆命令。
 *
 * ⚠️ **不支持引号分组**：Docker 的 healthcheck `test` 数组本身就是「参数已分好」的形状，
 * 用户要带空格参数时应当用 `CMD` 形态写成 `CMD curl -f "http://x/y"` 之外的多个 token，
 * 或直接用 `CMD-SHELL` 把整条命令交给 shell。
 * 在这里实现一套引号解析只会与 Docker 的规则部分重合，而不一致的那些恰好是最难查的。
 */
function splitCommand(command: string): string[] {
  return command.split(/\s+/).filter((part) => part.length > 0)
}

/** 解析正数（非数字返回 null）。 */
function parsePositiveNumber(input: string): number | null {
  const trimmed = input.trim()

  if (trimmed.length === 0) return null

  const value = Number(trimmed)

  return Number.isFinite(value) && value > 0 ? value : null
}
