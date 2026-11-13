import { http, request } from '@/api'
import type {
  ContainerRunSpec,
  ContainerRunSpecFormat,
  FileListResult,
  TransferJobAccepted,
  TransferJobStatus
} from '@/types/api'

/**
 * 文件传输与文件浏览端点（0.14.0，主机作用域）。
 *
 * ## 导出是**后台任务**，「导出成功」不等于「文件已就绪」
 * 发起导出只拿到 `jobId`，真正的产物要靠 `getTransferStatus` 轮询到
 * `succeeded`，那时的 `downloadUrl` 才可用。SignalR 的进度推送只是**提示**，
 * 权威状态永远在这条轮询上——断线重连后仍要能收敛。
 *
 * ## 下载地址**不要自己拼**
 * `downloadUrl` 里带你一次性票据（短时效、绑定产物文件）。自己拼一个
 * `/images/download` 只会拿到 `1408`。
 *
 * ## 上传为什么不用 `request()`
 * `request()` 走 axios 的 JSON 拦截器，会把 `FormData` / `Blob` 序列化坏；
 * 且上传要显示进度、要能取消。因此导入走 `uploadBinary()`（见本文件末尾），
 * 它直接用裸 axios 实例 + `onUploadProgress`。
 *
 * ## 路径编码
 * `hostId` 等于主机名，一律 `encodeURIComponent`——含 `/`、`?`、`#` 时
 * 不编码会把请求打到别的端点上。
 */

function base(hostId: string): string {
  return `/api/v1/hosts/${encodeURIComponent(hostId)}`
}

/**
 * 发起镜像导出（`docker save`）。
 *
 * 导出按**镜像引用**（如 `nginx:1.27`）或镜像 ID 都行；用引用导出会保留全部
 * `RepoTags`，而用 ID 导出的包再导入时没有标签。
 *
 * 同一镜像已有进行中的导出时会**复用**那个任务（`reused: true`），
 * 界面不该把它当新任务提示，否则用户会以为点了两次。
 */
export function exportImage(
  hostId: string,
  resourceId: string,
  progressToken?: string
): Promise<TransferJobAccepted> {
  return request<TransferJobAccepted>({
    url: `${base(hostId)}/images/export`,
    method: 'post',
    data: { resourceId, progressToken }
  })
}

/** 查询镜像导出任务状态（**权威状态源**）。 */
export function getImageExportStatus(
  hostId: string,
  jobId: string
): Promise<TransferJobStatus> {
  return request<TransferJobStatus>({
    url: `${base(hostId)}/images/export/status`,
    method: 'post',
    data: { jobId }
  })
}

/** 发起容器导出（`docker export`）。 */
export function exportContainer(
  hostId: string,
  resourceId: string,
  progressToken?: string
): Promise<TransferJobAccepted> {
  return request<TransferJobAccepted>({
    url: `${base(hostId)}/containers/export`,
    method: 'post',
    data: { resourceId, progressToken }
  })
}

/** 查询容器导出任务状态（**权威状态源**）。 */
export function getContainerExportStatus(
  hostId: string,
  jobId: string
): Promise<TransferJobStatus> {
  return request<TransferJobStatus>({
    url: `${base(hostId)}/containers/export/status`,
    method: 'post',
    data: { jobId }
  })
}

/**
 * 容器转镜像（`docker commit`）。
 *
 * `pause` 省略时后端**暂停容器再提交**（默认 `true`）：不暂停可能提交出一份
 * 写了一半的文件系统。代价是运行中的服务会在提交期间短暂停住。
 */
export function commitContainer(
  hostId: string,
  containerId: string,
  payload: {
    repository: string
    tag?: string
    comment?: string
    author?: string
    pause?: boolean
    changes?: string[]
  }
): Promise<import('@/types/api').CommitContainerResult> {
  return request({
    url: `${base(hostId)}/containers/commit`,
    method: 'post',
    data: { containerId, ...payload }
  })
}

/**
 * 列举容器内的目录。
 *
 * 省略 `path` 等价于根目录 `/`。目录列举靠读 `<path>` 的 tar 条目实现，
 * 因此**大目录会被截断**（`truncated: true`）——界面必须提示。
 */
export function listContainerFiles(
  hostId: string,
  containerId: string,
  path?: string
): Promise<FileListResult> {
  return request<FileListResult>({
    url: `${base(hostId)}/containers/files/list`,
    method: 'post',
    data: { containerId, path }
  })
}

/** 下载容器内的单个文件（返回 Blob，由调用方触发保存）。 */
export function downloadContainerFile(
  hostId: string,
  containerId: string,
  path: string
): Promise<Blob> {
  return request<Blob>({
    url: `${base(hostId)}/containers/files/download`,
    method: 'post',
    data: { containerId, path },
    responseType: 'blob'
  })
}

/**
 * 往容器内写文件（上传）。
 *
 * 目标目录不存在会 `1406`；管理员关掉写入时 `1409`；超过 `Files:MaxUploadBytes`
 * 会 `1407`（后端**边读边数**，因此界面不会看到「传到 90% 才失败」之外的形态）。
 * 同名文件会被**覆盖**（不是追加），界面必须二次确认。
 */
export function writeContainerFile(
  hostId: string,
  containerId: string,
  directoryPath: string,
  file: File,
  onProgress?: UploadProgress
): Promise<number> {
  const form = new FormData()
  form.append('containerId', containerId)
  form.append('directoryPath', directoryPath)
  form.append('file', file, file.name)

  return uploadForm<number>(`${base(hostId)}/containers/files/write`, form, onProgress)
}

/**
 * 往数据卷内写文件（上传）。
 *
 * 后端借**一次性辅助容器**完成写入（卷没有 Docker 文件接口），
 * 因此比容器内写入多一次容器创建 / 删除的开销——大文件请优先直连容器。
 */
export function writeVolumeFile(
  hostId: string,
  volumeName: string,
  directoryPath: string,
  file: File,
  onProgress?: UploadProgress
): Promise<number> {
  const form = new FormData()
  form.append('volumeName', volumeName)
  form.append('directoryPath', directoryPath)
  form.append('file', file, file.name)

  return uploadForm<number>(`${base(hostId)}/volumes/files/write`, form, onProgress)
}

/** 列举数据卷内的目录（后端借一次性辅助容器挂载，行为与容器内一致）。 */
export function listVolumeFiles(
  hostId: string,
  volumeName: string,
  path?: string
): Promise<FileListResult> {
  return request<FileListResult>({
    url: `${base(hostId)}/volumes/files/list`,
    method: 'post',
    data: { volumeName, path }
  })
}

/** 下载数据卷内的单个文件。 */
export function downloadVolumeFile(
  hostId: string,
  volumeName: string,
  path: string
): Promise<Blob> {
  return request<Blob>({
    url: `${base(hostId)}/volumes/files/download`,
    method: 'post',
    data: { volumeName, path },
    responseType: 'blob'
  })
}

/**
 * 生成容器的可复制定义。
 *
 * `format` 省略时给 `dockerRun`。生成结果里**含明文环境变量**——
 * 界面必须提示这一点（详情页对 `env` 也是同样的处理）。
 */
export function buildContainerRunSpec(
  hostId: string,
  containerId: string,
  format: ContainerRunSpecFormat = 'dockerRun'
): Promise<ContainerRunSpec> {
  return request<ContainerRunSpec>({
    url: `${base(hostId)}/containers/run-command`,
    method: 'post',
    data: { containerId, format }
  })
}

/**
 * 上传进度的回调（`0`–`100`；无法确定总大小时为 `null`）。
 *
 * 用 `null` 而不是 `0`：`0` 会被读成「还没开始」，而实际是「不知道总大小」。
 */
export type UploadProgress = (percent: number | null) => void

/**
 * 导入镜像（`docker load`）。
 *
 * 请求体是 `<code>application/octet-stream</code>` 的**原始 tar 字节**——
 * 不用 multipart，因为这里只有一个东西要传，套一层表单只会多一份拷贝。
 *
 * `1407` 表示超过 `Files:MaxUploadBytes`：后端**边读边数**，所以不会先把 20 GB
 * 收完再告诉你太大。
 */
export function importImage(
  hostId: string,
  file: File,
  onProgress?: UploadProgress
): Promise<import('@/types/api').ImageImportResult> {
  return uploadBinary(`${base(hostId)}/images/import`, file, onProgress)
}

/**
 * 导入容器文件系统（`docker import`）。
 *
 * 与镜像导入的区别：产出的是一张**没有历史层**的单层镜像，可同时指定
 * `repository` / `tag` 给它打标签。
 */
export function importContainer(
  hostId: string,
  file: File,
  options: { repository?: string; tag?: string } = {},
  onProgress?: UploadProgress
): Promise<import('@/types/api').ContainerImportResult> {
  const query = new URLSearchParams()

  if (options.repository) {
    query.set('repository', options.repository)
  }

  if (options.tag) {
    query.set('tag', options.tag)
  }

  const suffix = query.size > 0 ? `?${query.toString()}` : ''

  return uploadBinary(`${base(hostId)}/containers/import${suffix}`, file, onProgress)
}

/**
 * 以原始字节上传（`application/octet-stream`）。
 *
 * **不经 `request()`**：它把响应当 `ApiResult` 解包，而上传要带上
 * `onUploadProgress` 与自定义 `Content-Type`。走同一个 `http` 实例因此仍享受
 * 401 静默续期与统一错误提示。
 */
async function uploadBinary<T>(
  url: string,
  file: File,
  onProgress?: UploadProgress
): Promise<T> {
  const response = await http.request<T>({
    url,
    method: 'post',
    data: file,
    headers: { 'Content-Type': 'application/octet-stream' },
    // 大文件上传可能要几分钟：默认 30s 超时会把它掐断，而失败表现为
    // 「网络错误」——用户完全看不出是超时。
    timeout: 0,
    onUploadProgress: (event) => reportProgress(event.loaded, event.total, onProgress)
  })

  return response.data
}

/** 以 `multipart/form-data` 上传（文件写入用）。 */
async function uploadForm<T>(
  url: string,
  form: FormData,
  onProgress?: UploadProgress
): Promise<T> {
  const response = await http.request<T>({
    url,
    method: 'post',
    data: form,
    // 必须显式声明：`http` 的默认头是 `application/json`，
    // 带着它发 FormData 会让服务端解析不出任何字段（表现为「参数为空」）。
    headers: { 'Content-Type': 'multipart/form-data' },
    timeout: 0,
    onUploadProgress: (event) => reportProgress(event.loaded, event.total, onProgress)
  })

  return response.data
}

/** 把上传事件换算成百分比。 */
function reportProgress(
  loaded: number,
  total: number | undefined,
  onProgress?: UploadProgress
): void {
  if (!onProgress) {
    return
  }

  onProgress(total && total > 0 ? Math.min(100, Math.round((loaded / total) * 100)) : null)
}
