import { request } from '@/api'
import type {
  BatchOperationResult,
  PruneResult,
  VolumeDetail,
  VolumeListResult,
  VolumeSummary
} from '@/types/api'

/**
 * 数据卷端点（主机作用域，路径含 `hostId`）。
 *
 * ## 为什么 `hostId` 一律 `encodeURIComponent`
 * 它等于**主机名**（可以是任意合法主机名），直接拼进路径时若含 `/`、`?`、`#`
 * 会被解析成路径分隔符或查询串的开头，请求就会打到别的端点上。
 *
 * ## 失败分两类，别混为一谈
 * - **入参 / 主机级**（`1000` / `1300` / `1301`）：整批失败，走错误响应。
 * - **逐条**（`1401` 卷不存在 / `1404` 仍被引用）：进 `failures`，**整体仍是 200**。
 */

/**
 * 数据卷列表（服务端内存分页，口径与容器 / 镜像一致）。
 *
 * 省略分页参数时后端按「第 0 页、每页 20 条」处理；`pageSize` 超过 100 会被**夹紧**，
 * 回显的 `pageIndex` / `pageSize` 是夹紧后的实际取值。
 *
 * `search` 在服务端内存过滤（卷名 / 驱动 / 宿主挂载路径，不区分大小写）：
 * 分页也在服务端做，前端过滤只能筛到当前页。
 */
export function listVolumes(
  hostId: string,
  params: { search?: string; pageIndex?: number; pageSize?: number } = {}
): Promise<VolumeListResult> {
  return request<VolumeListResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/volumes/list`,
    method: 'post',
    data: params
  })
}

/** 数据卷详情（`name` 即卷名，大小写敏感）。 */
export function inspectVolume(hostId: string, name: string): Promise<VolumeDetail> {
  return request<VolumeDetail>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/volumes/inspect`,
    method: 'post',
    data: { name }
  })
}

/**
 * 创建数据卷。
 *
 * **同名创建不报错**，后端直接返回已存在的那一个（Docker 的既定行为）。
 * 因此界面在「创建成功」后必须刷新列表，否则会以为自己凭空造出了一个卷。
 *
 * 卷名规则 `^[a-zA-Z0-9][a-zA-Z0-9_.-]*$` 由**后端**强制，不满足时返回 `1000`。
 */
export function createVolume(
  hostId: string,
  payload: {
    name: string
    driver?: string
    driverOptions?: Record<string, string>
    labels?: Record<string, string>
  }
): Promise<VolumeSummary> {
  return request<VolumeSummary>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/volumes/create`,
    method: 'post',
    data: payload
  })
}

/**
 * 删除数据卷。
 *
 * ⚠️ **`force` 只能让「卷不存在」变成成功（幂等），删不掉仍被引用的卷**
 * ——后者无论带不带 `force` 都是 `1404`（已实测）。
 * 所以二次确认里应该说「先删掉使用它的容器」，而不是「勾选强制」。
 *
 * 不带 `force` 删除不存在的卷返回 `1401`/404。
 */
export function removeVolume(hostId: string, name: string, force = false): Promise<void> {
  return request<void>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/volumes/remove`,
    method: 'post',
    data: { name, force }
  })
}

/**
 * 批量删除数据卷。
 *
 * **部分成功也是 200**：逐条失败在 `failures` 里，错误码与单条端点完全一致。
 * 返回的 `requested` 是**去重后**的个数（重复项会被归一为一个）。
 */
export function batchRemoveVolumes(
  hostId: string,
  names: string[],
  force = false
): Promise<BatchOperationResult> {
  return request<BatchOperationResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/volumes/batch-remove`,
    method: 'post',
    data: { names, force }
  })
}

/**
 * 清理未被引用的数据卷（0.7.0，**不可逆**）。
 *
 * ⚠️ **请求里没有「要删哪些」**：选哪些由守护进程按「没有被任何容器引用」判断。
 * 它还会**跳过带挂载选项的卷**（那些其实不是本地卷），因此预览里可能列出来、实际却删不掉。
 *
 * ⚠️ **服务端固定按「连具名卷一起清」下发**（守护进程自 Engine API 1.42 起
 * 默认只清匿名卷，而预览列的是全部未被引用的卷）——因此预览与实际清理是同一套口径。
 *
 * `label` 把范围收窄到带该标签的卷（`key` 或 `key=value`）。
 * ⚠️ **省略它就等于「清掉本机全部未被引用的卷」**——在共享的开发机上那是别人的数据。
 *
 * 返回的 `deleted` 是**实际**删掉的卷名。
 */
export function pruneVolumes(
  hostId: string,
  payload: { label?: string } = {}
): Promise<PruneResult> {
  return request<PruneResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/volumes/prune`,
    method: 'post',
    data: payload
  })
}
