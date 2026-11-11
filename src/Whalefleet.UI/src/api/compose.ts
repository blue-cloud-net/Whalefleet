import { request } from '@/api'
import type {
  ComposeProjectDetail,
  ComposeProjectListResult,
  ComposeTaskAccepted,
  ComposeTaskSnapshot,
  ComposeWebhookInfo
} from '@/types/api'

/**
 * Compose 项目端点（主机作用域，路径含 `hostId`）。
 *
 * ## 为什么 `hostId` 一律 `encodeURIComponent`
 * 它等于**主机名**（可以是任意合法主机名），直接拼进路径时若含 `/`、`?`、`#`
 * 会被解析成路径分隔符或查询串的开头，请求就会打到别的端点上。
 *
 * ## 部署类端点返回 202 而不是 200
 * 部署可能要拉镜像（几分钟），而 HTTP 请求扛不住那么长——
 * 反代会把连接掐掉，客户端拿到的是「504 网关超时」而不是「部署失败」。
 * 因此端点立刻返回任务标识，进度靠实时通道推送、状态靠轮询任务快照。
 */

/** 编排列表（服务端内存分页，与容器 / 镜像 / 卷同一套口径）。 */
export function listComposeProjects(
  hostId: string,
  params: { search?: string; pageIndex?: number; pageSize?: number } = {}
): Promise<ComposeProjectListResult> {
  return request<ComposeProjectListResult>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/list`,
    method: 'post',
    data: params
  })
}

/** 编排详情（`name` 即 Compose 项目名称 = Compose 项目名）。 */
export function inspectComposeProject(hostId: string, name: string): Promise<ComposeProjectDetail> {
  return request<ComposeProjectDetail>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/inspect`,
    method: 'post',
    data: { name }
  })
}

/**
 * 部署Compose 项目（新建）。
 *
 * ⚠️ **名字已被占用时返回 `1902`**——此时应改用 `updateComposeProject`，
 * 而不是让用户以为「部署坏了」。
 *
 * ⚠️ **`taskId` 由客户端生成**：它同时是实时进度的订阅令牌，
 * 服务端没法知道「这次部署对应哪条 Hub 连接」。
 */
export function deployComposeProject(
  hostId: string,
  payload: {
    name: string
    composeContent: string
    taskId: string
    /** 是否总是拉取最新镜像（默认只在本地缺失时拉） */
    pullAlways?: boolean
    /** 是否清掉「已不在 YAML 里的」孤儿服务（部署时默认不清） */
    pruneOrphans?: boolean
  }
): Promise<ComposeTaskAccepted> {
  return request<ComposeTaskAccepted>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/deploy`,
    method: 'post',
    data: payload
  })
}

/**
 * 更新Compose 项目（重新应用 YAML）。
 *
 * ⚠️ **只有本平台部署过的项目才能更新**（`managed=true`）：
 * 外部 compose 项目（用户自己在命令行 `docker compose up` 的）我们没有它的 YAML，
 * 点「更新」必然失败——界面应当直接不给这个入口，而不是等后端报 `1903`。
 */
export function updateComposeProject(
  hostId: string,
  payload: {
    name: string
    composeContent: string
    taskId: string
    pullAlways?: boolean
    /** 更新时默认清理孤儿（与 `docker compose up -d` 的口径一致） */
    pruneOrphans?: boolean
  }
): Promise<ComposeTaskAccepted> {
  return request<ComposeTaskAccepted>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/update`,
    method: 'post',
    data: payload
  })
}

/** 停止Compose 项目（停掉全部服务，**容器保留**）。 */
export function stopComposeProject(
  hostId: string,
  payload: { name: string; taskId: string }
): Promise<ComposeTaskAccepted> {
  return request<ComposeTaskAccepted>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/stop`,
    method: 'post',
    data: payload
  })
}

/**
 * 删除Compose 项目（容器与网络一并清掉）。
 *
 * ⚠️ **卷默认保留**：`removeVolumes` 必须由用户显式勾选——
 * 卷里是用户的数据，删它的后果不可逆，而它可以事后单独清理。
 */
export function removeComposeProject(
  hostId: string,
  payload: { name: string; taskId: string; removeVolumes?: boolean }
): Promise<ComposeTaskAccepted> {
  return request<ComposeTaskAccepted>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/remove`,
    method: 'post',
    data: payload
  })
}

/**
 * 查询Compose 任务快照。
 *
 * 任务不存在时**返回 `null` 而不是报错**——任务登记表只保留有限时长，
 * 查不到是正常情况（例如已经过了保留期）。
 */
export function getComposeTask(hostId: string, taskId: string): Promise<ComposeTaskSnapshot | null> {
  return request<ComposeTaskSnapshot | null>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/task`,
    method: 'post',
    data: { taskId }
  })
}

/**
 * 生成（或轮换）Webhook 令牌。
 *
 * ⚠️ **返回的 `token` 是明文，且只此一次**：库里只存哈希。
 * 界面必须提示用户「请立刻保存，之后无法再查看」。
 */
export function rotateComposeWebhook(hostId: string, name: string): Promise<ComposeWebhookInfo> {
  return request<ComposeWebhookInfo>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/webhook/rotate`,
    method: 'post',
    data: { name }
  })
}

/** 吊销 Webhook 令牌（幂等：本来就没启用也算成功）。 */
export function revokeComposeWebhook(hostId: string, name: string): Promise<ComposeWebhookInfo> {
  return request<ComposeWebhookInfo>({
    url: `/api/v1/hosts/${encodeURIComponent(hostId)}/composes/webhook/revoke`,
    method: 'post',
    data: { name }
  })
}

/**
 * 由 Webhook 触发重放（**匿名**端点，凭据即令牌）。
 *
 * ⚠️ 令牌在**路径**里而不是请求头：CI 触发器（如 GitHub webhook）
 * 只能配一个 URL，而「URL 里带令牌」是这类触发器唯一能用的形态。
 */
export function triggerComposeWebhook(
  token: string,
  params: { pullAlways?: boolean; pruneOrphans?: boolean; wait?: boolean } = {}
): Promise<ComposeTaskAccepted> {
  return request<ComposeTaskAccepted>({
    url: `/api/v1/webhooks/composes/${encodeURIComponent(token)}`,
    method: 'post',
    data: params
  })
}
