/**
 * 镜像的展示逻辑（纯函数，无 vue-i18n 依赖，可脱离组件单测）。
 *
 * 与 `container-status.ts` 同构：这里只做「数据 → 展示形态」的映射，
 * 文案一律交给调用方 `t()`。分成独立文件而不是塞进 `container-status.ts`：
 * 容器的语义是生命周期状态，镜像是仓库 / 标签 / 层，两者没有共享词汇。
 *
 * 字节格式化**不在这里重复实现**——镜像页直接用 `container-status.ts` 的
 * `formatBytes`：同一种展示有两份实现，迟早会在某一边修 bug 时漏掉另一边。
 */

/** 悬挂镜像的展示文本（Docker 内部用 `<none>:<none>` 占位，后端已过滤掉该标签） */
export const DANGLING_IMAGE_LABEL = '<none>'

/**
 * 镜像的「主展示名」。
 *
 * 一个镜像常有多个标签（`nginx:latest` + `nginx:1.27`）。列表里逐行铺开会让
 * 「同一个镜像出现两次」看起来像两个镜像，因此只取第一个当主名，
 * 其余用 `+N` 表达（`stackedTagCount()` 给出数量）。
 *
 * ⚠️ 顺序即服务端给的顺序（Docker 按标签排序返回），**不要在前端重排**——
 * 那会让「刷新后主名变了」这种困惑无从解释。
 */
export function primaryImageName(repoTags: readonly string[] | null | undefined): string {
  const tags = repoTags ?? []

  return tags.length > 0 ? tags[0] : DANGLING_IMAGE_LABEL
}

/** 被折叠起来的标签数量（主名之外还剩几个）。 */
export function stackedTagCount(repoTags: readonly string[] | null | undefined): number {
  const tags = repoTags ?? []

  return Math.max(tags.length - 1, 0)
}

/** 是否是悬挂镜像（没有任何标签）。 */
export function isDangling(repoTags: readonly string[] | null | undefined): boolean {
  return (repoTags ?? []).length === 0
}

/**
 * 镜像 ID 的短形态。
 *
 * 去掉 `sha256:` 前缀并截到 12 位——这是 `docker images` 与 `docker ps` 的惯例宽度，
 * 也是人眼能分辨的宽度。完整 ID 仍在详情页与 tooltip 里给出。
 */
export function shortImageId(id: string | null | undefined): string {
  const raw = String(id ?? '').replace(/^sha256:/, '')

  return raw.length > 12 ? raw.slice(0, 12) : raw
}

/**
 * 由 `repoDigests` 取内容寻址摘要的短形态。
 *
 * 形式为 `nginx@sha256:abc…`；只保留 `@` 之后的部分并截断，
 * 因为前面的仓库名在界面上已经由 `repoTags` 表达了。
 */
export function shortDigest(digest: string | null | undefined): string {
  const raw = String(digest ?? '')
  const at = raw.indexOf('@')
  const value = at >= 0 ? raw.slice(at + 1) : raw

  return shortImageId(value)
}

/**
 * 把用户输入的 `仓库:标签` 拆成后端要的两个参数。
 *
 * 后端规则：`tag` 省略时按 `latest`；**若 `image` 已含标签又另传 `tag`，返回 `1000`/400**
 * （而不是拼成 `nginx:1.27:alpine`）。因此拉取表单允许用户整串写 `仓库:标签`，
 * 由本函数拆开——两处各写一半最容易被拼错。
 *
 * 判据是**冒号是否在最后一个斜杠之后**，与 Docker 一致。
 * ⚠️ 不能用「冒号后是否纯数字」：标签允许纯数字（`nginx:1`），
 * 那种规则会把带端口的仓库地址（`registry:5000/nginx`）误判成「已含标签」。
 */
export function splitImageReference(input: string): { image: string; tag?: string } {
  const value = input.trim()
  if (value.length === 0) return { image: '' }

  const lastSlash = value.lastIndexOf('/')
  const lastColon = value.lastIndexOf(':')

  // 冒号在斜杠之前 → 那是仓库地址的端口，不是标签
  if (lastColon <= lastSlash) return { image: value }

  const image = value.slice(0, lastColon)
  const tag = value.slice(lastColon + 1)

  // `nginx:` 这种残缺输入按「未填标签」处理，交给后端补 latest
  return tag.length === 0 ? { image } : { image, tag }
}
