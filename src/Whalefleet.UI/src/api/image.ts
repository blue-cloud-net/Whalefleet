import { request } from '@/api'
import type {
  BatchOperationResult,
  ImageBuildResult,
  ImageDetail,
  ImageListResult,
  ImagePullResult,
  ImagePushResult,
  ImageRemovedResult,
  ImageTaggedResult,
  PruneResult
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
  payload: { image: string; tag?: string; platform?: string; progressToken?: string }
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

/**
 * 批量删除镜像。
 *
 * **部分成功也是 200**：只有「入参非法」（`1000`）与「主机不可达」（`1301`/`1302`）才是真正的错误响应，
 * 而这两类都是**整批**失败——逐条删失败时结果在 `failures` 里，错误码与单条端点完全一致。
 *
 * 返回的 `requested` 是**去重后**的个数，因此界面可以直接用
 * `succeeded + failures.length` 展示进度，不必自己先去重。
 */
export function batchRemoveImages(
  hostId: string,
  imageIds: string[],
  force = false
): Promise<BatchOperationResult> {
  return request<BatchOperationResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/batch-remove`,
    method: 'post',
    data: { imageIds, force }
  })
}

/**
 * 给镜像打标签（对应 `docker tag`，0.3.0）。
 *
 * ⚠️ **tag 不复制镜像**：新标签与源标签指向**同一个镜像 ID**，磁盘占用不变。
 * 因此 UI 必须把行更新成「同一镜像的另一个标签」，而**不是**新增一行镜像——
 * 后者会让用户以为副本可以随便删（删掉最后一个标签时镜像确实会消失）。
 *
 * `repository` **不能含标签**：用户很容易照着「源镜像」那一栏把 `nginx:1.27` 整个粘进来，
 * 后端会拒为 `1000`/400（而不是拼成 `nginx:1.27:alpine`）。
 * UI 应当**在输入框就分开填仓库名与标签**，并在提交前本地拦一道。
 *
 * `tag` 省略或为空时后端取 `latest`。
 *
 * 返回的 `repoTags` 是**打标签之后**该镜像 ID 上的全部标签，
 * 界面据此刷新那一行而不必重新拉整个列表。
 */
export function tagImage(
  hostId: string,
  imageId: string,
  repository: string,
  tag?: string
): Promise<ImageTaggedResult> {
  return request<ImageTaggedResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/tag`,
    method: 'post',
    data: { imageId, repository, tag }
  })
}

/**
 * 用上传的构建上下文 tar 构建镜像（`multipart/form-data`，0.4.0）。
 *
 * ## ⚠️ 三处必踩的坑
 *
 * 1. **必须显式覆盖 `Content-Type`**。`http` 实例的默认头是 `application/json`，
 *    而 axios 的 `transformRequest` 见到 FormData 且带 JSON Content-Type 时会
 *    `JSON.stringify(formDataToJSON(data))` —— **文件被静默丢掉**，
 *    服务端报「context 字段是必填的」（`1000`）。改成 `multipart/form-data`
 *    才会走 FormData 分支；浏览器环境下 axios 随后会**清掉**这个头，
 *    由浏览器自己补 boundary（自己拼 boundary 一定拼错）。
 * 2. **文件段必须带文件名**。表单里没有 `filename` 的段会被当成普通字段，
 *    `Request.Form.Files` 为空——症状同样是「context 是必填的」。
 * 3. **必须放宽超时**。实例默认 30 秒，而一次真实构建是分钟级；
 *    不放宽就会在 30 秒时收到一个与构建无关的「网络超时」，
 *    用户以为是后端挂了，实际上构建还在跑。
 *
 * ## `buildArgs` / `labels` 走 JSON 字符串
 * 表单没有天然字典形态，后端约定收 **JSON 对象的字符串**。
 * 界面的校验在 `utils/image-build.ts`，这里不再解析一遍。
 *
 * 失败：`1806`（构建失败，消息里带守护进程给的原因，`buildLog` 有完整日志）、
 * `1000`（参数非法）、`1301`（主机不可达）。
 */
export function buildImage(
  hostId: string,
  payload: {
    /** 构建上下文 tar */
    context: File
    /** 产物引用（`仓库名` 或 `仓库名:标签`）；必填 */
    tag: string
    /** tar 包内的 Dockerfile 路径；省略为 `Dockerfile` */
    dockerfile?: string
    /** JSON 对象字符串，如 `{"VERSION":"1.0"}` */
    buildArgs?: string
    /** JSON 对象字符串 */
    labels?: string
    target?: string
    platform?: string
    noCache?: boolean
    /** 实时进度令牌（0.5.0）；带上后构建过程会向通知 Hub 推进度 */
    progressToken?: string
  }
): Promise<ImageBuildResult> {
  const form = new FormData()

  // 顺序无关，但文件名**必须有**（见上文坑 2）
  form.append('context', payload.context, payload.context.name)
  form.append('tag', payload.tag)

  if (payload.dockerfile) form.append('dockerfile', payload.dockerfile)
  if (payload.buildArgs) form.append('buildArgs', payload.buildArgs)
  if (payload.labels) form.append('labels', payload.labels)
  if (payload.target) form.append('target', payload.target)
  if (payload.platform) form.append('platform', payload.platform)
  if (payload.noCache) form.append('noCache', 'true')
  if (payload.progressToken) form.append('progressToken', payload.progressToken)

  return request<ImageBuildResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/build`,
    method: 'post',
    data: form,
    headers: { 'Content-Type': 'multipart/form-data' },
    // 0 = 不限时：构建可能是分钟级，30 秒的默认值会误报成「网络超时」
    timeout: 0
  })
}

/**
 * 把本地镜像推送到仓库（0.4.0）。
 *
 * `repositoryId` 省略时后端**按镜像引用里的地址段自动匹配**已登记的配置。
 * 显式指定用于「本地标签不带仓库前缀，但就是要推到内网 Harbor」这种场景。
 *
 * 失败：`1807`（推送失败）、`1804`（凭据被拒）、`1800`（指定的配置不存在）、
 * `1000`（镜像没有标签——推送必须用 `仓库:标签` 形态）。
 */
export function pushImage(
  hostId: string,
  payload: { image: string; repositoryId?: string; platform?: string }
): Promise<ImagePushResult> {
  return request<ImagePushResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/push`,
    method: 'post',
    data: payload,
    // 大镜像推送是分钟级，理由同 buildImage
    timeout: 0
  })
}

/**
 * 清理未被使用的镜像（0.7.0，**不可逆**）。
 *
 * ⚠️ **请求里没有「要删哪些」**：选哪些由守护进程按「没有被任何容器使用」判断，
 * 而**是否只清悬空镜像**由 `danglingOnly` 决定：
 * - 省略或 `true` = 只清**悬空镜像**（一个标签都不剩的）；
 * - `false` = **不止悬空**（等价 `docker image prune -a`），
 *   任何未被容器使用的镜像都是候选。
 *
 * ⚠️ **`false` 的语义不是「只要非悬空」**——写反会让用户以为只会删掉无标签的那一批。
 *
 * `label` 把范围收窄到带该标签的镜像（`key` 或 `key=value`）。
 * ⚠️ **省略它就等于「清掉本机全部悬空镜像」**——在共享的开发机上那是别人的构建产物。
 *
 * 返回的 `deleted` 是**实际**删掉的（可能与预览的候选清单不一致）。
 */
export function pruneImages(
  hostId: string,
  payload: { danglingOnly?: boolean; label?: string } = {}
): Promise<PruneResult> {
  return request<PruneResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/images/prune`,
    method: 'post',
    data: payload
  })
}
