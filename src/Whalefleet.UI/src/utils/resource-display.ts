import type { NetworkSummary, VolumeSummary } from '@/types/api'

/**
 * 数据卷与网络的展示逻辑（0.2.0）。
 *
 * 纯函数、返回 i18n key 而非文案——这样可以脱离组件单测，
 * 也避免同一句话在多个组件里各写一遍后逐渐漂移。
 */

/** 内置网络名：不可删除。与后端 `NetworkService.BuiltInNetworks` 同一份清单。 */
export const BUILT_IN_NETWORK_NAMES: readonly string[] = ['bridge', 'host', 'none', 'null']

/**
 * 数据卷的用量是否可以展示。
 *
 * ⚠️ **`size` 为 0 不代表空卷**：这两个值来自守护进程的 `UsageData`，
 * 只在 Docker API 1.42+ 的列表接口里出现；老守护进程根本不返回该字段，
 * 反序列化后同样是 0。两种 0 无法区分，因此界面一律显示「—」而不是「0 B」——
 * 谎报一个精确的数字比留空更糟。
 */
export function hasVolumeUsage(volume: Pick<VolumeSummary, 'size'>): boolean {
  return volume.size > 0
}

/**
 * 数据卷的引用数是否可以展示。
 *
 * 与 <see cref="hasVolumeUsage"/> 同源：`refCount` 同样可能因字段缺失而是 0。
 */
export function hasVolumeRefCount(volume: Pick<VolumeSummary, 'refCount'>): boolean {
  return volume.refCount > 0
}

/** 数据卷是否仍被容器引用（决定删除前的提示文案）。 */
export function isVolumeInUse(volume: Pick<VolumeSummary, 'refCount'>): boolean {
  return volume.refCount > 0
}

/**
 * 网络是否为 Docker 内置（不可删除）。
 *
 * **同时认 `none` 与 `null`**：Docker 26 把「禁用网络」从 `null` 改名成 `none`，
 * 两个名字在各自的版本上都是内置的。只认一个的话，
 * 新机器上用户点不删 `none`、旧机器上点不掉 `null`。
 */
export function isBuiltInNetwork(network: Pick<NetworkSummary, 'name'>): boolean {
  return BUILT_IN_NETWORK_NAMES.includes(network.name)
}

/**
 * 网络是否「有东西在用」——即**不能**直接删除。
 *
 * ⚠️ 列表项**没有容器数**（Docker 的列表接口不返回 `Containers`），
 * 因此这里判的是「是不是内置」，而不是「有没有容器接入」。
 * 真正的接入情况要看详情页的 `containers`。
 */
export function isNetworkRemovable(network: Pick<NetworkSummary, 'name'>): boolean {
  return !isBuiltInNetwork(network)
}

/**
 * 网络的子网展示文本。
 *
 * 没有 IPAM 时返回「—」而不是空串：自定义驱动（`macvlan`）本就没有 IPAM，
 * 那是正常状态而非缺数据。
 */
export function networkSubnetLabel(network: Pick<NetworkSummary, 'subnets'>): string {
  return network.subnets.length > 0 ? network.subnets.join(' · ') : '—'
}

/**
 * 网络是否开启了 IPv6。
 *
 * 列表项里没有 `enableIPv6`（那是详情字段），因此只能按子网推断：
 * 子网是 IPv6 就一定是开了的；反之不一定。
 */
export function looksLikeIpv6(network: Pick<NetworkSummary, 'subnets'>): boolean {
  return network.subnets.some((subnet) => subnet.includes(':'))
}

/**
 * 数据卷名的合法性（与后端 `DockerResourceName` 同一条正则）。
 *
 * 前端也校验是为了**即时反馈**，不是为了替代后端校验——
 * 后端那道永远在，而前端这道能把红字直接摆在输入框下面。
 */
export function isValidResourceName(name: string): boolean {
  return /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(name)
}

/**
 * 网段（CIDR）格式的合法性（与后端 `NormalizeIpParameter` 同口径）。
 *
 * 要求<b>必须带前缀长度</b>——只写 `172.18.0.0` 会被后端判 1000。
 *
 * ⚠️ 八位组必须落在 0–255：后端用的是 `IPAddress.TryParse`，它拒绝 `999.1.1.1`。
 * 这里若只写 `\d{1,3}`，前端就会放行一个后端必然拒绝的输入——
 * 用户在输入框看到「合法」，提交后才吃一个 1000，且看不出哪里错。
 */
export function isValidSubnet(subnet: string): boolean {
  const trimmed = subnet.trim()
  if (trimmed.length === 0) return true

  const slash = trimmed.indexOf('/')
  if (slash < 0) return false

  const address = trimmed.slice(0, slash)
  const prefix = trimmed.slice(slash + 1)

  if (!/^\d+$/.test(prefix)) return false

  // 前缀长度上限随地址族变化：IPv4 是 32，IPv6 是 128
  const max = address.includes(':') ? 128 : 32
  const value = Number(prefix)
  if (value < 0 || value > max) return false

  if (address.includes(':')) return /^[0-9a-fA-F:]+$/.test(address)

  return isIpv4(address)
}

/**
 * 网关是否合法（地址，不必带前缀长度）。
 *
 * 与 <see cref="isValidSubnet"/> 同理，八位组范围必须对齐后端。
 */
export function isValidGateway(gateway: string): boolean {
  const trimmed = gateway.trim()
  if (trimmed.length === 0) return true

  if (trimmed.includes(':')) return /^[0-9a-fA-F:]+$/.test(trimmed)

  return isIpv4(trimmed)
}

/** 四段十进制且每段落在 0–255（等价于 .NET 的 `IPAddress.TryParse` 对 IPv4 的判定）。 */
function isIpv4(address: string): boolean {
  const octets = address.split('.')

  if (octets.length !== 4) return false

  return octets.every((octet) => /^\d{1,3}$/.test(octet) && Number(octet) <= 255)
}
