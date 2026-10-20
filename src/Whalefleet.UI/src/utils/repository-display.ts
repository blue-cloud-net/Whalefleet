/**
 * 私有仓库 的展示逻辑（纯函数，无 vue-i18n 依赖，可脱离组件单测）。
 *
 * ## 为什么这里的规则要「照抄后端」
 * `repositoryAddressOf()` 复刻的是后端 `ImageReference.ExtractServerAddress`。
 * 前端用它做的事是**预测**「后端会去连哪台 仓库、会用哪条凭据」，
 * 用来在推送前把目标地址摆给用户看。两边的判据一旦漂移，
 * 界面就会显示一个地址、后端实际连另一个——而后者才决定推成功还是失败。
 *
 * ## 为什么不用「冒号后是数字就算端口」这类规则
 * 标签允许纯数字（`nginx:1`）。任何基于「冒号右侧内容长什么样」的判断
 * 都会把 `registry:5000/nginx` 误判成「已含标签」。唯一可靠的判据是
 * **冒号是否在最后一个斜杠之后**，这也是 Docker 自己的规则。
 */

/**
 * 无前缀仓库的归属（Docker Hub 的 API 端点）。
 *
 * ⚠️ 不是 `docker.io`：`docker.io` 是给用户看的名字，Docker 客户端与
 * `AuthConfig.ServerAddress` 用的是 `registry-1.docker.io`。两者混用会让
 * 「匹配已登记凭据」永远匹配不上 Docker Hub。
 */
export const DOCKER_HUB_SERVER_ADDRESS = 'registry-1.docker.io'

/**
 * 从镜像引用（`仓库名` 或 `仓库名:标签`）切出 仓库地址段。
 *
 * 三步，顺序不能换：
 * 1. 去掉标签（冒号在最后一个斜杠之后才是标签，否则那是端口）
 * 2. 取第一个路径段
 * 3. 只有「首段含 `.` 或 `:`，或整段等于 `localhost`」才算前缀
 *
 * 第 3 步是**关键**：否则 `library/nginx` 这种官方镜像命名空间会被当成
 * 一个叫 `library` 的仓库主机，推镜像时会去连它，得到一个
 * 「主机不存在」而不是「这是 Docker Hub 镜像」。
 */
export function repositoryAddressOf(input: string): string {
  const repository = stripTag(input.trim())
  if (repository.length === 0) return ''

  const slash = repository.indexOf('/')
  if (slash <= 0) return DOCKER_HUB_SERVER_ADDRESS

  const prefix = repository.slice(0, slash)
  const looksLikeRegistry =
    prefix.includes('.') || prefix.includes(':') || prefix.toLowerCase() === 'localhost'

  return looksLikeRegistry ? prefix.toLowerCase() : DOCKER_HUB_SERVER_ADDRESS
}

/**
 * 去掉引用里的标签段。
 *
 * `harbor.local:5000/app` 的 `:5000` 是**端口**，不是标签——
 * 因此判据只能是「冒号在最后一个斜杠之后」。
 */
export function stripImageTag(reference: string): string {
  return stripTag(reference.trim())
}

function stripTag(reference: string): string {
  const trimmed = reference.replace(/:+$/, '')
  const colon = trimmed.lastIndexOf(':')

  if (colon <= trimmed.lastIndexOf('/')) return trimmed

  return trimmed.slice(0, colon)
}

/** 推送前提示用的「结果形态」，`messageKey` 是可翻译的 key 而不是文案。 */
export interface RepositoryTestVerdict {
  tone: 'ok' | 'warn' | 'error'
  /** i18n key（`registry.test.*`） */
  messageKey: string
}

/**
 * 把「连通性 + 认证」两个布尔值翻译成**一个可执行的结论**。
 *
 * 为什么必须拿到两个字段才下结论：Registry 的三种常见状态
 * 「网络不通」「连得上但凭据无效」「连得上且凭据有效」需要三种完全不同的处置
 * （查网络防火墙 / 重填口令 / 无需动作）。压成一个布尔值后，界面只能显示
 * 「成功 / 失败」，用户不知道该改哪儿。
 *
 * `authenticated` 为真而 `reachable` 为假是**不可能的组合**（没连上不可能认证过）；
 * 后端不会给，但夹具或改坏的后端可能给，这里按「不通」处理——
 * 把矛盾数据解释成成功，比显示一个保守的失败要危险得多。
 */
export function describeRepositoryTest(result: {
  reachable: boolean
  authenticated: boolean
}): RepositoryTestVerdict {
  if (!result.reachable) {
    return { tone: 'error', messageKey: 'repository.test.unreachable' }
  }

  if (!result.authenticated) {
    // 连通但认证没过：既可能是「凭据错了」，也可能是「这条配置本来就没给账号
    // 而仓库要求登录」。文案要同时覆盖两种，否则用户只会一直重填同一个口令。
    return { tone: 'warn', messageKey: 'repository.test.anonymousOnly' }
  }

  return { tone: 'ok', messageKey: 'repository.test.ok' }
}

/**
 * 在已登记的配置里找「推送时会用哪一条」。
 *
 * 判据是**规范化地址的小写相等**：后端按 `server_address` 查（`COLLATE NOCASE`），
 * 前端这里不比对就猜不准「到底会不会带上凭据」。
 */
export function findRepositoryForReference<T extends { serverAddress: string }>(
  reference: string,
  repositories: readonly T[]
): T | null {
  const address = repositoryAddressOf(reference)
  if (address.length === 0) return null

  return repositories.find((item) => item.serverAddress.toLowerCase() === address) ?? null
}
