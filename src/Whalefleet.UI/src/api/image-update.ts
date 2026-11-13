import { request } from '@/api'
import type {
  ImageUpdateCheckResult,
  ImageUpdateMode,
  ImageUpdatePolicy
} from '@/types/api'

/**
 * 镜像自动更新策略端点（0.15.0，**主机作用域**）。
 *
 * 路径：`/api/v1/hosts/{hostId}/images/update-policies/*`。
 * 读用 `image.read`，写用 `image.write`（「立即检查」也算写：
 * `auto` 模式下它会真的拉镜像并重建容器）。
 *
 * ## 界面上的三条判据
 * - `state` 有**五档**，其中 `unknown` 与 `up-to-date` 必须分开展示：
 *   前者是「还没查过」（显示 —），后者是「查过、确实是最新的」。
 * - `baselineDigest` 为 `null` 表示**尚未建立基线**，此时不该报警——
 *   刚加上的策略一次都没查过，而首次检查只建立基线。
 * - 检查失败返回 **200**，原因在 `state=failed` + `message` 里。所以不要靠
 *   状态码判断成败，要看 `state`。
 */

/** 列出某台主机的全部策略。 */
export function listImageUpdatePolicies(hostId: string): Promise<ImageUpdatePolicy[]> {
  return request<ImageUpdatePolicy[]>({
    url: `/api/v1/hosts/${hostId}/images/update-policies/list`,
    method: 'post'
  })
}

/**
 * 新增策略。
 *
 * `reference` 省略标签时后端补 `latest`；**摘要引用（`repo@sha256:…`）会被拒绝**——
 * 摘要永不变化，跟踪它等于永远「已是最新」，属于误配置。
 */
export function createImageUpdatePolicy(
  hostId: string,
  payload: { reference: string; mode?: ImageUpdateMode; enabled?: boolean }
): Promise<ImageUpdatePolicy> {
  return request<ImageUpdatePolicy>({
    url: `/api/v1/hosts/${hostId}/images/update-policies/create`,
    method: 'post',
    data: payload
  })
}

/**
 * 修改策略的执行模式与启用状态。
 *
 * ⚠️ **镜像引用不可改**：改引用等于换一条策略（历史结论与基线摘要都失去意义）。
 * 要换镜像请删掉旧策略再新建，这样审计里能清楚看到「换过镜像」。
 */
export function updateImageUpdatePolicy(
  hostId: string,
  payload: { id: string; mode?: ImageUpdateMode; enabled?: boolean }
): Promise<ImageUpdatePolicy> {
  return request<ImageUpdatePolicy>({
    url: `/api/v1/hosts/${hostId}/images/update-policies/update`,
    method: 'post',
    data: payload
  })
}

/** 删除策略。 */
export function removeImageUpdatePolicy(hostId: string, id: string): Promise<void> {
  return request<void>({
    url: `/api/v1/hosts/${hostId}/images/update-policies/remove`,
    method: 'post',
    data: { id }
  })
}

/** 立即检查一条策略（`auto` 模式下会拉取并重建）。 */
export function checkImageUpdatePolicy(
  hostId: string,
  id: string
): Promise<ImageUpdateCheckResult> {
  return request<ImageUpdateCheckResult>({
    url: `/api/v1/hosts/${hostId}/images/update-policies/check`,
    method: 'post',
    data: { id }
  })
}
