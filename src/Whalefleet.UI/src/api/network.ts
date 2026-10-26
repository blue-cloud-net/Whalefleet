import { request } from '@/api'
import type { NetworkDetail, NetworkListResult, NetworkSummary } from '@/types/api'

/**
 * 网络端点（主机作用域，路径含 `hostId`）。
 *
 * ## 为什么 `hostId` 一律 `encodeURIComponent`
 * 同 `api/volume.ts`：主机名可能含 `/`、`?`、`#`，不编码就会打到别的端点上。
 */

/**
 * 网络列表（服务端内存分页，口径与容器 / 镜像一致）。
 *
 * ⚠️ 列表项**没有容器数**：Docker 的列表接口不返回 `Containers`（只有详情接口返回）。
 * 「这个网络上有几个容器」要打开详情看，别在列表上编一个 0 出来。
 *
 * 省略分页参数时后端按「第 0 页、每页 20 条」处理；`pageSize` 超过 100 会被**夹紧**。
 *
 * `search` 在服务端内存过滤（网络名 / 网络 ID / 驱动，不区分大小写）：
 * 分页也在服务端做，前端过滤只能筛到当前页。
 */
export function listNetworks(
  hostId: string,
  params: { search?: string; pageIndex?: number; pageSize?: number } = {}
): Promise<NetworkListResult> {
  return request<NetworkListResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/networks/list`,
    method: 'post',
    data: params
  })
}

/** 网络详情（`networkId` 可传网络名或那串 64 位十六进制 ID）。 */
export function inspectNetwork(hostId: string, networkId: string): Promise<NetworkDetail> {
  return request<NetworkDetail>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/networks/inspect`,
    method: 'post',
    data: { networkId }
  })
}

/**
 * 创建网络。
 *
 * `subnet` 必须是 CIDR（`172.18.0.0/16`）；格式非法后端返回 `1000`/400 并在消息里点名是哪个字段。
 * 省略 `subnet` 时由守护进程从默认地址池分配。
 * 传 IPv6 网段时后端会**自动打开 `enableIPv6`**——否则会建出一个「有 IPv6 地址池却不通」的网。
 *
 * 失败有两类，界面都要能显示：
 * - `1000`：字段格式不对（用户改一下输入就行）；
 * - `1404`/409：同名网络已存在，或子网与现有网络重叠——**后端保留了守护进程给的原因文本**，
 *   据此能区分该改名字还是改子网。
 */
export function createNetwork(
  hostId: string,
  payload: {
    name: string
    driver?: string
    subnet?: string
    gateway?: string
    ipRange?: string
    attachable?: boolean
    labels?: Record<string, string>
  }
): Promise<NetworkSummary> {
  return request<NetworkSummary>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/networks/create`,
    method: 'post',
    data: payload
  })
}

/**
 * 删除网络。
 *
 * ⚠️ **没有 `force`**，因为 Docker 的删除网络接口不提供该参数：
 * 只要还有容器接入就必然 `1404`/409，后端会在消息里点明「还有几个容器」。
 * 正确顺序是先把容器从该网络断开（或删掉容器）。
 *
 * 内置网络（`bridge` / `host` / `none`，老版本还有 `null`）同样返回 `1404`。
 */
export function removeNetwork(hostId: string, networkId: string): Promise<void> {
  return request<void>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/networks/remove`,
    method: 'post',
    data: { networkId }
  })
}

/**
 * Docker 内置网络名，界面上禁用删除按钮。
 *
 * **同时列 `none` 与 `null`**：Docker 26 把「禁用网络」从 `null` 改名成 `none`，
 * 而两个名字在各自的版本上都是内置的。只列一个的话，新旧机器上各有一个会漏掉。
 */
export const BUILT_IN_NETWORKS: readonly string[] = ['bridge', 'host', 'none', 'null']

/**
 * 把容器接入网络（0.7.0）。
 *
 * **幂等**：容器已经在该网络上时后端直接返回成功（它先读实际状态再决定要不要调守护进程），
 * 因此重复点击不会得到一个含义不明的错误。
 *
 * ⚠️ **容器必须处于运行中**：守护进程只允许接入运行中的容器
 * （停止的容器没有网络命名空间可接入）。
 */
export function connectNetwork(
  hostId: string,
  containerId: string,
  networkId: string
): Promise<void> {
  return request<void>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/networks/connect`,
    method: 'post',
    data: { containerId, networkId }
  })
}

/**
 * 把容器从网络断开（0.7.0）。
 *
 * **幂等**：容器本来就不在该网络上时后端直接返回成功。
 *
 * ⚠️ **断开会让容器立刻失去该网络的连通性**，界面必须二次确认。
 *
 * ⚠️ **`force` 会连累别人**：Docker 的强制断开会把**共用同一网络命名空间**的容器
 * 一起踢掉，而那些容器用户可能根本不知道存在。因此默认不强制。
 */
export function disconnectNetwork(
  hostId: string,
  containerId: string,
  networkId: string,
  force = false
): Promise<void> {
  return request<void>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/networks/disconnect`,
    method: 'post',
    data: { containerId, networkId, force }
  })
}
