import { request } from '@/api'
import type {
  ImageDetail,
  ImageListResult,
  ImagePullResult,
  ImageRemovedResult
} from '@/types/api'

/**
 * 镜像端点（主机作用域，路径含 `hostId`）。
 *
 * ## 为什么 `hostId` 一律 `encodeURIComponent`
 * 它等于**主机名**（可以是任意合法主机名），直接拼进路径时若含 `/`、`?`、`#`
 * 会被解析成路径分隔符或查询串的开头，请求就会打到别的端点上。
 * 主机名当前被限制为安全字符，但那是**后端的**约束——前端不该依赖它来保证自己的 URL 正确。
 *
 * 路径与请求/响应结构必须与 `docs/api-spec.md` 的「镜像管理」一致；
 * 失败统一走 `ApiResult` + 全局异常中间件，因此这里不写 `try/catch`。
 */

/** 镜像列表（`search` 在服务端内存过滤，不分大小写）。 */
export function listImages(
  hostId: string,
  params: {
    all?: boolean
    search?: string
    pageIndex?: number
    pageSize?: number
    /** 排序字段；省略时保持后端返回顺序（创建时间降序）。未知值后端按 `created` 处理 */
    sortBy?: ImageSortField
    /** 排序方向；`asc` 为升序，其余（含省略）按降序 */
    sortDir?: SortDirection
  } = {}
): Promise<ImageListResult> {
  return request<ImageListResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/list`,
    method: 'post',
    data: params
  })
}

/** 镜像列表可排序的字段，与后端 `ImageSortField` 常量一一对应。 */
export type ImageSortField = 'repository' | 'size' | 'created' | 'usedBy'

/** 排序方向，与容器的同名类型取值相同。 */
export type SortDirection = 'asc' | 'desc'

/**
 * 镜像详情（`imageId` 可传完整 ID、short ID、仓库名或 `仓库:标签`）。
 */
export function inspectImage(hostId: string, imageId: string): Promise<ImageDetail> {
  return request<ImageDetail>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/inspect`,
    method: 'post',
    data: { imageId }
  })
}

/**
 * 拉取镜像。
 *
 * `tag` 省略或为空时后端按 `latest` 处理；**若 `image` 已经含标签又传了另一个 `tag`，
 * 后端返回 `1000`/400**（而不是拼成 `nginx:1.27:alpine`）——因此调用方应把整个
 * `仓库:标签` 直接填进 `image`，不要在两处各写一半。
 *
 * 失败有两类，界面都要能显示：`1401`（镜像 / 仓库不存在）、`1402`（仓库拒绝或网络中断——
 * 后者在 Docker 侧 HTTP 仍是 200，只有进度流里带错误，由后端翻译）。
 */
export function pullImage(
  hostId: string,
  payload: { image: string; tag?: string; platform?: string }
): Promise<ImagePullResult> {
  return request<ImagePullResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/pull`,
    method: 'post',
    data: payload
  })
}

/**
 * 删除镜像。
 *
 * 不带 `force` 删除仍被容器占用的镜像 → `1404`/409（界面据此提示「先删除容器」）。
 * 返回体里的 `untagged` 是**关键信息**：同一个镜像 ID 常挂多个标签，
 * 删掉一个标签时镜像本体可能仍在，不展示就会让用户以为删除无效。
 */
export function removeImage(
  hostId: string,
  imageId: string,
  force = false
): Promise<ImageRemovedResult> {
  return request<ImageRemovedResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/remove`,
    method: 'post',
    data: { imageId, force }
  })
}
