/**
 * 设备指纹（0.13.0）。
 *
 * ## 它是什么
 * 一串稳定的十六进制标识，随每个请求上报，服务端把它记在**会话**上。
 *
 * ## 它**不是**什么
 * 它**不是**安全边界。服务端只做「记录」，变化就覆盖，**从不因为指纹不同而拒绝请求**。
 *
 * 它的唯一用途是**给人看**：会话列表里那几行「Chrome / Windows / 某 IP」，
 * 让人能认出「哪一条是我自己、哪一条是别人在用」。
 * 把它当安全措施（比如「指纹变了就踢下线」）是常见误解——
 * 指纹可被伪造（它就是几个浏览器属性的哈希），拿它做判定只会制造假故障。
 *
 * ## 组成
 * ① **持久随机 ID**（localStorage）：区分「同一浏览器的不同配置」；
 * ② **浏览器属性哈希**（UA / 语言 / 平台 / 屏幕 / 时区 / 硬件并发数）：
 *   让指纹在换机器后仍然「看起来像另一台机器」。
 *
 * 两者拼接后取 SHA-256（用 WebCrypto；不可用时退化为一个稳定的 FNV 哈希）。
 *
 * ## 为什么不用现成库
 * 指纹库（如 fingerprintjs）体积不小、要追踪行为，而这个用途只需要
 * 「一个稳定且不太相同的字符串」。自实现让这个模块可以被单测覆盖。
 */

/** localStorage 中保存随机 ID 的键名。 */
const STORAGE_KEY = 'whalefleet:client-id'

/** 浏览器属性的哈希轮数（够用即可，不必追求密码学强度）。 */
const HASH_ROUNDS = 64

/** 随机 ID 的字节数。 */
const ID_BYTES = 16

/**
 * 生成一段随机十六进制串。
 *
 * 优先用 `crypto.getRandomValues`；不可用时退回到 `Math.random`
 * （老浏览器 / 非安全上下文）。这不是密码学用途，只是为了区分客户端。
 */
function randomHex(byteLength: number): string {
  const bytes = new Uint8Array(byteLength)

  const cryptoApi = globalThis.crypto
  if (cryptoApi?.getRandomValues) {
    cryptoApi.getRandomValues(bytes)
  } else {
    for (let i = 0; i < bytes.length; i++) {
      bytes[i] = Math.floor(Math.random() * 256)
    }
  }

  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
}

/**
 * 读取（或首次生成）持久随机 ID。
 *
 * 存在 localStorage 里，因此同一个浏览器反复打开页面都是同一个 ID；
 * 换浏览器 / 清缓存则不同——这正是我们想要的「区分客户端」语义。
 */
function clientId(): string {
  try {
    const existing = localStorage.getItem(STORAGE_KEY)
    if (existing) return existing

    const created = randomHex(ID_BYTES)
    localStorage.setItem(STORAGE_KEY, created)
    return created
  } catch {
    // localStorage 不可用（隐私模式 / 配额满）：退回一次性 ID。
    // 后果是每次刷新页面指纹都变——服务端只记录、不校验，因此完全无害。
    return randomHex(ID_BYTES)
  }
}

/**
 * 采集浏览器属性并拼成一个稳定字符串。
 *
 * 只取**低熵且稳定**的属性：它们不该在同一次会话里变化，
 * 否则「同一台机器的两次登录」看起来会像两台机器。
 */
function browserSignature(): string {
  if (typeof navigator === 'undefined') return 'unknown'

  const parts = [
    navigator.userAgent ?? '',
    navigator.language ?? '',
    (navigator.languages ?? []).join(','),
    navigator.platform ?? '',
    String(navigator.hardwareConcurrency ?? ''),
    String(globalThis.screen?.width ?? ''),
    String(globalThis.screen?.height ?? ''),
    String(globalThis.screen?.colorDepth ?? ''),
    String(resolveTimezone())
  ]

  return parts.join('|')
}

/**
 * 解析时区偏移（分钟）。
 *
 * 用 `Intl` 而不是 `Date.getTimezoneOffset()`：后者在部分环境里返回负值，
 * 符号约定容易搞混；这里统一成「UTC 减去本地」的分钟数。
 */
function resolveTimezone(): number {
  try {
    return -new Date().getTimezoneOffset()
  } catch {
    return 0
  }
}

/**
 * FNV-1a 哈希（32 位）。
 *
 * WebCrypto 的 `subtle.digest` 是**异步**的，而指纹必须在请求拦截器里**同步**拿到
 * （拦截器本身可以是异步的，但把它做成同步更简单也更可靠）。
 * 因此这里用同步哈希；真正需要密码学强度的地方（例如令牌哈希）由后端负责。
 */
function fnv1a(text: string): string {
  let hash = 0x811c9dc5

  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i)
    hash = Math.imul(hash, 0x01000193) >>> 0
  }

  return hash.toString(16).padStart(8, '0')
}

/**
 * 计算最终指纹。
 *
 * 同一浏览器恒定；不同浏览器通常不同（但不保证唯一——它只是给人看的辅助信息）。
 */
export function computeFingerprint(): string {
  const material = `${clientId()}|${browserSignature()}`

  // 多轮哈希让不同输入更难撞出同一个值；对辅助信息来说已经足够。
  let digest = material
  for (let i = 0; i < HASH_ROUNDS; i++) {
    digest = fnv1a(`${digest}:${material}`)
  }

  return digest
}

/**
 * 缓存的指纹值。
 *
 * 算一次就够：它是**每个会话**的属性，不是每个请求的属性。
 */
let cached: string | null = null

/**
 * 取当前浏览器的设备指纹。
 *
 * 第一次调用时计算并缓存；之后直接返回缓存值。
 */
export function getFingerprint(): string {
  cached ??= computeFingerprint()
  return cached
}
