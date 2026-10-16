import { request } from '@/api'
import type {
  BatchOperationResult,
  ContainerCreatedResult,
  ContainerDetail,
  ContainerListResult,
  ContainerLogsResult
} from '@/types/api'

/**
 * 容器端点（对应后端 `/api/v1/hosts/{hostId}/containers/*`，0.0.9 已实现）。
 *
 * ## 路径约定（见 docs/api-spec.md）
 *
 * 全部是 **主机作用域**（路径含 `hostId`）与 **RPC 风格**（一律 POST）：
 * - 查询 `list` / `inspect` / `logs`
 * - 变更 `create` / `start` / `stop` / `restart` / `remove`
 *
 * `hostId` 要**编码**后拼进路径：主机名虽是 `[A-Za-z0-9._-]`（设计上可直接进 URL），
 * 但 encodeURIComponent 成本为零，而漏掉它一旦将来放开主机名字符集就是线上问题。
 *
 * ## 为什么全部返回 Promise<结果> 而不是 void
 * `start` / `stop` / `restart` / `remove` 后端不返回 `data`，但仍返回统一响应——
 * 拦截器已经解包，这里 `Promise<void>` 即可。保留 void 而不是 `Promise<unknown>`，
 * 是为了让调用方在类型层面就知道「这里没有东西可拿」。
 */

/** 主机作用域的容器端点基址。 */
function base(hostId: string): string {
  return `/api/v1/hosts/${encodeURIComponent(hostId)}/containers`
}

/** 列表查询参数（全部可选，对应后端的可空请求字段）。 */
export interface ListContainersParams {
  /** 是否含已停止的容器；省略时只列运行中的（等价 `docker ps`） */
  all?: boolean
  /** 页码，从 0 开始 */
  pageIndex?: number
  /** 每页条数；后端上限 100，超出会被**夹紧并回显实际值** */
  pageSize?: number
  /** 排序字段；省略时保持后端返回顺序（创建时间降序）。未知值后端按 `created` 处理 */
  sortBy?: ContainerSortField
  /** 排序方向；`asc` 为升序，其余（含省略）按降序 */
  sortDir?: SortDirection
}

/** 容器列表可排序的字段，与后端 `ContainerSortField` 常量一一对应。 */
export type ContainerSortField = 'name' | 'image' | 'state' | 'created'

/** 排序方向。 */
export type SortDirection = 'asc' | 'desc'

/**
 * 列出容器。
 *
 * 分页在**后端内存**完成（Docker API 无分页参数），因此返回的 `pageIndex` / `pageSize`
 * 是服务端实际使用的值——页面应以它们为准，而不是以请求里写的为准。
 */
export function listContainers(
  hostId: string,
  params: ListContainersParams = {}
): Promise<ContainerListResult> {
  return request<ContainerListResult>({
    url: `${base(hostId)}/list`,
    method: 'post',
    data: params
  })
}

/**
 * 查询容器详情。
 *
 * @param imageId 容器标识：完整 ID / 短 ID / 名称都接受。
 */
export function inspectContainer(hostId: string, imageId: string): Promise<ContainerDetail> {
  return request<ContainerDetail>({
    url: `${base(hostId)}/inspect`,
    method: 'post',
    data: { containerId: imageId }
  })
}

/** 容器日志的查询参数。 */
export interface ContainerLogsParams {
  /** 只取最后 N 行；省略表示全部。后端上限 10000，越界返回 1000。 */
  tail?: number
  /** 只取该 Unix 秒（含）之后的日志 */
  since?: number
  /** 是否给每行加时间戳 */
  timestamps?: boolean
  /** 是否包含 stderr；省略时包含 */
  includeStderr?: boolean
}

/**
 * 读取容器日志（一次性返回整段文本）。
 *
 * **不支持实时跟随**：后端刻意没暴露 `follow`（一次性 HTTP 响应跟着日志流只会把连接挂住）。
 * 实时流属 0.4.0 的 SignalR 推送。
 */
export function getContainerLogs(
  hostId: string,
  containerId: string,
  params: ContainerLogsParams = {}
): Promise<ContainerLogsResult> {
  return request<ContainerLogsResult>({
    url: `${base(hostId)}/logs`,
    method: 'post',
    data: { containerId, ...params }
  })
}

/** 创建容器的请求体。字段与后端 `CreateContainerRequest` 逐字对应。 */
export interface CreateContainerPayload {
  /** 镜像名；必填 */
  image: string
  /** 容器名；省略则由 Docker 随机命名 */
  name?: string
  cmd?: string[]
  entrypoint?: string[]
  env?: string[]
  labels?: Record<string, string>
  ports?: Array<{
    /** 容器内端口 1–65535；必填 */
    containerPort: number
    /** 宿主端口；省略表示只暴露不绑定（随机映射） */
    hostPort?: number
    /** `tcp` / `udp`；省略为 tcp */
    type?: string
    /** 宿主地址；省略表示所有网卡 */
    hostIp?: string
  }>
  volumes?: Array<{
    /** 宿主路径或卷名；必填 */
    source: string
    /** 容器内路径；必填 */
    destination: string
    readOnly?: boolean
  }>
  /** `no` / `always` / `on-failure` / `unless-stopped`；省略等同 no */
  restartPolicy?: string
  autoRemove?: boolean
  /** 网络名或模式 */
  network?: string
  hostname?: string
  workingDir?: string
  tty?: boolean
  stdinOpen?: boolean
}

/**
 * 创建容器（**只创建，不自动启动**）。
 *
 * 返回的 `warnings` 是守护进程的提示（如「端口未映射」），**不是错误**——
 * 创建已经成功，调用方应当展示它们而不是当成失败。
 */
export function createContainer(
  hostId: string,
  payload: CreateContainerPayload
): Promise<ContainerCreatedResult> {
  return request<ContainerCreatedResult>({
    url: `${base(hostId)}/create`,
    method: 'post',
    data: payload
  })
}

/** 启动容器（幂等：已在运行也返回成功）。 */
export function startContainer(hostId: string, containerId: string): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/start`,
    method: 'post',
    data: { containerId }
  })
}

/**
 * 停止容器（幂等：已停止也返回成功）。
 *
 * @param waitSeconds SIGTERM 后等多久才 SIGKILL；省略则由守护进程用默认值（10 秒）
 */
export function stopContainer(
  hostId: string,
  containerId: string,
  waitSeconds?: number
): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/stop`,
    method: 'post',
    data: { containerId, waitSeconds }
  })
}

/** 重启容器（幂等）。 */
export function restartContainer(hostId: string, containerId: string): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/restart`,
    method: 'post',
    data: { containerId }
  })
}

/**
 * 删除容器（**不连带删除数据卷**）。
 *
 * 不带 `force` 删运行中的容器会得到 `1404`/409（前端据此提示先停止），
 * 因此「删不掉」是有意义的业务结果，调用方要把它渲染成可操作的提示而不是通用报错。
 */
export function removeContainer(
  hostId: string,
  containerId: string,
  force = false
): Promise<void> {
  return request<void>({
    url: `${base(hostId)}/remove`,
    method: 'post',
    data: { containerId, force }
  })
}

/**
 * 批量删除容器。
 *
 * **部分成功也是 200**：只有「入参非法」（`1000`）与「主机不可达」（`1301`/`1302`）才是真正的错误响应，
 * 而这两类都是**整批**失败。逐条删失败时结果在 `failures` 里，错误码与单条端点完全一致
 * （例如运行中且未 `force` 的容器是 `1404`）。
 *
 * 返回的 `requested` 是**去重后**的个数——同一个容器传两次不会显示成「成功 1、失败 1」。
 */
export function batchRemoveContainers(
  hostId: string,
  containerIds: string[],
  force = false
): Promise<BatchOperationResult> {
  return request<BatchOperationResult>({
    url: `${base(hostId)}/batch-remove`,
    method: 'post',
    data: { containerIds, force }
  })
}
