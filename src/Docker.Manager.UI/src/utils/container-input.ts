/**
 * 容器创建输入的解析与格式化（纯函数，无 vue-i18n 依赖，可脱离组件单测）。
 *
 * ## 为什么拆出来而不是留在 `CreateContainerDialog.vue`
 * 0.0.14 之前，端口 / 挂载 / 环境变量的解析函数是该对话框的内部实现，
 * 既没法脱离组件测试（要挂载一个 el-dialog 才能调），也没法被**批量粘贴**
 * 与**结构化行编辑**两条路径共用——两条路径一旦各写一份解析，
 * 「粘贴 `8080:80` 能建、手填一行建不了」这种不一致就会发生。
 *
 * 因此本文件只做「文本 ↔ 结构」的转换与校验，组件只管渲染与收集。
 *
 * ## 与后端的分工
 * 后端也会校验一遍（返回 `1000`），但它的消息不带行号，用户得自己数。
 * 前端提前拦下并指出第几行，是为了让错误**在输入时就可见**；
 * 后端的校验则是最后一道防线（也覆盖非浏览器调用方）。
 */

/** 一条环境变量。`value` 允许为空串——`FOO=` 是合法的「显式置空」。 */
export interface EnvEntry {
  key: string
  value: string
}

/** 一条端口映射。`hostPort` 为 `null` 表示「只暴露不绑定」。 */
export interface PortMapping {
  /** 宿主地址；`null` 表示所有网卡 */
  hostIp: string | null
  /** 宿主端口；`null` = 不映射（随机分配） */
  hostPort: number | null
  /** 容器内端口 */
  containerPort: number
  protocol: 'tcp' | 'udp'
}

/** 一条挂载。`type` 表达用户的**意图**，后端按 source 自行判定实际类型。 */
export interface VolumeMount {
  /**
   * `auto` = 不指定（交给 Docker 判定，行为与 `docker run` 完全一致），
   * `bind` / `volume` = 用户显式指定。
   *
   * 保留 `auto` 作为默认是因为它是唯一能同时覆盖两种来源的写法；
   * 界面上的「自动判定」选项对应它。
   */
  type: 'auto' | 'bind' | 'volume'
  source: string
  destination: string
  readOnly: boolean
}

/**
 * 解析结果。`errorLine` 是**出错的行号**（从 1 开始）；`null` 表示全部合法。
 *
 * ⚠️ 出错时 `values` 是**出错行之前**已成功解析的部分，不是空数组。
 *
 * 这一点让批量粘贴的编辑器能在报错时保住用户已输入的内容：若失败就返回空，
 * 用户纠正第 2 行时会连第 1 行一起丢掉，只能从头重输。
 */
export interface ParseResult<T> {
  values: T[]
  errorLine: number | null
}

/**
 * 端口号是否合法（1–65535）。
 *
 * 单独导出是因为结构化编辑器逐行校验时也要用，与文本解析共用同一份判据——
 * 两处各写一遍 `x >= 1 && x <= 65535`，迟早会在改端口范围时漏掉一边。
 */
export function isValidPort(port: number): boolean {
  return Number.isInteger(port) && port >= 1 && port <= 65535
}

/**
 * 环境变量名是否合法。
 *
 * 规则与后端一致：`[A-Za-z_][A-Za-z0-9_]*`。刻意**不**放宽到允许 `-`：
 * 环境变量名会被 shell 与各种语言生态当标识符用，含连字符的键在下游
 * 几乎处处要转义，而 Docker 自己也不校验，放宽等于把问题推给用户。
 */
export function isValidEnvKey(key: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(key)
}

/**
 * 容器内路径是否合法：必须是绝对路径。
 *
 * 「必须是绝对路径」不是洁癖——Docker 自己就会把相对路径按 daemon 的工作
 * 目录去解析，那个目录随启动方式变化，用户猜不到自己挂到了哪儿。
 */
export function isValidContainerPath(path: string): boolean {
  return path.startsWith('/') || /^[A-Za-z]:[\\/]/.test(path)
}

/**
 * 把文本按行拆成「行号 + 去空白后的内容」，丢弃空行。
 *
 * 刻意**保留行号**：用户粘贴多行命令时难免带上空行，
 * 错误提示要能指到原始文本里的第几行，否则「第 2 行有错」会指错位置。
 */
function toLines(text: string): Array<{ line: number; value: string }> {
  return text
    .split('\n')
    .map((raw, index) => ({ line: index + 1, value: raw.trim() }))
    .filter((entry) => entry.value.length > 0)
}

/**
 * 解析一行 `KEY=VALUE`。
 *
 * 只在**第一个** `=` 处切分，因此 `KEY=a=b` 得到值 `a=b`——这是 POSIX 的约定，
 * 连接串（`DATABASE_URL=postgres://u:p@h/db?x=1`）里满是等号，按最后一个切必然出错。
 *
 * @returns `null` 表示该行不合法
 */
export function parseEnvLine(value: string): EnvEntry | null {
  const separator = value.indexOf('=')

  // separator <= 0：没有 `=`，或 `=` 出现在首位（`=foo` 没有键名）
  if (separator <= 0) return null

  const key = value.slice(0, separator)
  if (!isValidEnvKey(key)) return null

  return { key, value: value.slice(separator + 1) }
}

/** 解析多行环境变量文本（每行 `KEY=VALUE`）。 */
export function parseEnvText(text: string): ParseResult<EnvEntry> {
  const values: EnvEntry[] = []

  for (const { line, value } of toLines(text)) {
    const parsed = parseEnvLine(value)
    if (parsed === null) return { values, errorLine: line }

    values.push(parsed)
  }

  return { values, errorLine: null }
}

/**
 * 解析一行端口映射，语法同 Docker 的 `-p`：
 * `[hostIp:][hostPort:]containerPort[/protocol]`
 *
 * 四种常见写法都要支持，因为实际使用中出现频率接近：
 * - `80` —— 只暴露不绑定（宿主端口随机）
 * - `8080:80` —— 指定宿主端口
 * - `:80` —— 同样随机分配宿主端口（显式留空，与 `80` 等价）
 * - `127.0.0.1:8080:80` —— 绑到指定网卡
 *
 * ## `8080:` 这类「端口位留空」一律拒绝
 * 写 `8080:` 的人多半以为它是「容器 8080、宿主随机」，但按 Docker 的字段顺序
 * 它是「宿主 8080、容器为空」——而 Docker 自己会用 "no port specified" 拒绝它。
 * 与其猜一个**安全但可能错误**的端口映射（本容器可能被意外暴露到宿主 8080），
 * 不如直接拒绝并报错。空位只有写在**前面**（`:80`）才是明确的「宿主随机」。
 *
 * @returns `null` 表示该行不合法
 */
export function parsePortLine(value: string): PortMapping | null {
  const slash = value.indexOf('/')
  const addressPart = slash >= 0 ? value.slice(0, slash) : value
  const protocolPart = slash >= 0 ? value.slice(slash + 1).trim().toLowerCase() : 'tcp'

  if (protocolPart !== 'tcp' && protocolPart !== 'udp') return null

  const parts = addressPart.split(':')
  if (parts.length > 3) return null

  // 各端口字段：留空（空白串）= 随机分配；非空但不是合法端口 = 整行不合法
  let hostIp: string | null = null
  let hostPort: number | null = null
  let containerPort: number | null = null

  if (parts.length === 3) {
    hostIp = parts[0].trim().length > 0 ? parts[0].trim() : null
    if (!assignPort(parts[1], (value) => (hostPort = value))) return null
    if (!assignPort(parts[2], (value) => (containerPort = value))) return null
  } else if (parts.length === 2) {
    // 前置空位（`:80`）= 宿主端口随机；否则宿主端口写在左边
    if (!assignPort(parts[0], (value) => (hostPort = value))) return null
    if (!assignPort(parts[1], (value) => (containerPort = value))) return null
  } else if (!assignPort(parts[0], (value) => (containerPort = value))) {
    return null
  }

  // 容器端口必填：留空意味着「什么都没映射」，那不如不写这一行
  if (containerPort === null) return null

  return { hostIp, hostPort, containerPort, protocol: protocolPart }
}

/**
 * 把一个端口字段赋给目标。
 *
 * - 空白 → `null`（合法留空 = 随机分配），返回 `true`
 * - 合法端口 → 赋值，返回 `true`
 * - 其余 → 返回 `false`，调用方据此判定整行不合法
 */
function assignPort(raw: string, assign: (value: number | null) => void): boolean {
  const trimmed = raw.trim()
  if (trimmed.length === 0) {
    assign(null)
    return true
  }

  const value = Number(trimmed)
  if (!isValidPort(value)) return false

  assign(value)
  return true
}

/** 解析多行端口映射文本。 */
export function parsePortText(text: string): ParseResult<PortMapping> {
  const values: PortMapping[] = []

  for (const { line, value } of toLines(text)) {
    const parsed = parsePortLine(value)
    if (parsed === null) return { values, errorLine: line }

    values.push(parsed)
  }

  return { values, errorLine: null }
}

/**
 * 判断一段字符串是否形似**绝对路径**。
 *
 * 覆盖两类：`/data`（POSIX）与 `D:\data`（Windows 盘符）。
 * 第二个 `\` 也要放行——Docker 接受正斜杠的 Windows 路径。
 */
function looksAbsolute(path: string): boolean {
  return path.startsWith('/') || /^[A-Za-z]:[\\/]/.test(path)
}

/**
 * 拆 `来源:容器内路径`。
 *
 * ## 判据：找到**第一个**切分点，使冒号之后的部分是绝对路径
 *
 * 「按最后一个冒号切」在 Linux 上够用，但会把 Windows 路径切坏：
 * `C:\data:/data` 按最后一个冒号切 → 来源 `C:\data`、目标 `/data`（对），
 * 而按第一个冒号切 → 来源 `C`、目标 `\data:/data`（错）；
 * `C:\data:D:\data` 按最后一个冒号切 → 目标丢掉盘符变成 `\data`（错）。
 *
 * 改用「第一个让右侧成为绝对路径的冒号」，三种情形同时成立：
 * - `/host/data:/data` → 第 1 个冒号后是 `/data` ✓
 * - `C:\data:/data` → 冒号 1 后是 `\data:/data`（非绝对路径），
 *   冒号 6 后是 `/data` ✓ → 来源 `C:\data`
 * - `C:\data:D:\data` → 冒号 6 后是 `D:\data` ✓ → 来源 `C:\data`、目标 `D:\data`
 *
 * @returns `null` 表示缺少容器内路径
 */
function splitVolumeSpec(body: string): { source: string; destination: string } | null {
  for (let i = 0; i < body.length; i++) {
    if (body[i] !== ':') continue

    const destination = body.slice(i + 1)
    if (looksAbsolute(destination)) {
      const source = body.slice(0, i)
      return source.length > 0 ? { source, destination } : null
    }
  }

  return null
}

/**
 * 解析一行挂载：`来源:容器内路径[:ro]`。
 *
 * 来源既可以是宿主路径也可以是具名卷——Docker 自己根据它判定类型，
 * 因此文本解析出的结果一律标 `auto`，与 `docker run` 的行为完全一致。
 *
 * @returns `null` 表示该行不合法
 */
export function parseVolumeLine(value: string): VolumeMount | null {
  const readOnly = value.endsWith(':ro')
  const body = readOnly ? value.slice(0, -3) : value

  const split = splitVolumeSpec(body)
  if (split === null) return null
  if (!isValidContainerPath(split.destination)) return null

  return { type: 'auto', ...split, readOnly }
}

/** 解析多行挂载文本。 */
export function parseVolumeText(text: string): ParseResult<VolumeMount> {
  const values: VolumeMount[] = []

  for (const { line, value } of toLines(text)) {
    const parsed = parseVolumeLine(value)
    if (parsed === null) return { values, errorLine: line }

    values.push(parsed)
  }

  return { values, errorLine: null }
}

/** 环境变量 → `KEY=VALUE` 文本（用于「复制为命令行」与占位提示）。 */
export function formatEnvEntry(entry: EnvEntry): string {
  return `${entry.key}=${entry.value}`
}

/**
 * 端口映射 → `-p` 参数值。
 *
 * 省略 `hostIp` / `hostPort` 的空位，顺序与 Docker 一致（宿主在左）。
 */
export function formatPortMapping(mapping: PortMapping): string {
  const target = mapping.hostPort === null ? String(mapping.containerPort) : `${mapping.hostPort}:${mapping.containerPort}`
  const base = mapping.hostIp ? `${mapping.hostIp}:${target}` : target

  return mapping.protocol === 'tcp' ? base : `${base}/${mapping.protocol}`
}

/** 挂载 → `来源:容器内路径[:ro]` 文本。 */
export function formatVolumeMount(mount: VolumeMount): string {
  return `${mount.source}:${mount.destination}${mount.readOnly ? ':ro' : ''}`
}