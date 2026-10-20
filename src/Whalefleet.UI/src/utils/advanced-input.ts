/**
 * 容器名与资源限制的**纯逻辑**判定（0.3.0）。
 *
 * ## 为什么要单独成文件
 * 这些规则被三个对话框（重命名 / 复制 / 热更新）和创建表单共用。
 * 放在组件里各写一遍，迟早只改一处——而漏掉的那处会让同一个输入
 * 在两个界面里判定不同，用户看到的是「这里能填那里不能填」��
 *
 * 纯函数、无 DOM 依赖，因此可被 vitest 直接单测（见 `tests/advanced-input.spec.ts`），
 * 不用为了测一个正则去挂载组件。
 *
 * ⚠️ **前端校验不是安全边界**：后端一定会再校验一遍。这里存在的意义是
 * 「把提示落到对应输入框」，以及**提前拦掉那些「后端会返回 200 但没生效」的请求**
 * （见 `MIN_MEMORY_LIMIT_BYTES` 与热更新的 0 值规则）。
 */

/** 容器名是否合法（与后端 `ContainerService.RequireContainerName` 同规则）。 */
const CONTAINER_NAME_PATTERN = /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/

/**
 * 内存下限：6 MiB。
 *
 * ⚠️ **不是 4 MiB**。Docker 把这个硬性下限从 4 MiB 提到了 6 MiB
 * （实测 `6291455` 被拒、`6291456` 才过）。写 4 MiB 会让用户填了一个
 * 「看起来很合理」的值，然后拿到一句指不出原因的报错。
 */
export const MIN_MEMORY_LIMIT_BYTES = 6 * 1024 * 1024

/** CPU 配额上限：1024 核 × 10⁹。 */
export const MAX_NANO_CPUS = 1024 * 1_000_000_000

/** 重启策略的合法取值（与后端 `BuildRestartPolicy` 一致）。 */
export const RESTART_POLICIES = ['no', 'always', 'on-failure', 'unless-stopped'] as const

export type RestartPolicy = (typeof RESTART_POLICIES)[number]

/** 容器名是否合法。 */
export function isValidContainerName(name: string): boolean {
  return CONTAINER_NAME_PATTERN.test(name)
}

/**
 * 把用户填的「内存上限」解析成**字节**。
 *
 * ⚠️ **入参是 MiB，出参是字节**：输入框的标签写的是 MiB，而请求字段是
 * `memoryLimitBytes`。两处单位不同，所以**换算必须发生在这个函数里**——
 * 之前分成「按 MiB 判下限」与「按字节发出去」两步，结果下限时按 MiB 判
 * （512 MiB 通过）、发出去时又当成 512 字节，用户得到的是一个 512 字节的限制。
 *
 * @returns `null` = 未填写；`number` = 字节数；`string` = 错误 i18n key。
 *
 * ⚠️ **这里不接受 0**：Docker 的热更新对 `0` 是「返回成功但不生效」
 * （守护进程按「字段非 0 才应用」处理）。因此本项目**不提供「取消内存限制」**——
 * 那是一个点了没反应却显示成功的控件，比没有更糟。要取消限制只能重建容器。
 */
export function parseMemoryLimit(input: string): number | null | 'error' {
  const trimmed = input.trim()

  if (trimmed.length === 0) return null

  const value = Number(trimmed)

  if (!Number.isInteger(value) || value <= 0) return 'error'

  const bytes = value * 1024 * 1024

  return bytes < MIN_MEMORY_LIMIT_BYTES ? 'error' : bytes
}

/**
 * 把用户填的「CPU 配额」解析成**十亿分之一核**。
 *
 * ⚠️ 与内存同理：入参是「核」（可含小数），出参是 `nanoCpus`（整数）。
 *
 * @returns `null` = 未填写；`number` = 十亿分之一核；`string` = 错误 i18n key。
 *
 * 同样**不接受 0**（理由见 `parseMemoryLimit`）。
 */
export function parseNanoCpus(input: string): number | null | 'error' {
  const trimmed = input.trim()

  if (trimmed.length === 0) return null

  const value = Number(trimmed)

  if (!Number.isFinite(value) || value <= 0) return 'error'
  if (value * 1e9 > MAX_NANO_CPUS) return 'error'

  return Math.round(value * 1e9)
}

/**
 * 校验标签是否合法（与后端 `ImageService.RequireTag` 一致）。
 *
 * 规则来自 Docker：首字符必须是字母或下划线，其后是字母 / 数字 / `_ . -`，最长 128。
 * **斜杠不允许**——路径分隔属于仓库名部分。
 *
 * @returns `null` = 合法；否则返回错误 i18n key。
 */
export function validateImageTag(tag: string): string | null {
  if (tag.length === 0) return null

  if (!/^[a-zA-Z_][a-zA-Z0-9_.-]*$/.test(tag)) return 'image.tag.invalid'

  return tag.length > 128 ? 'image.tag.tooLong' : null
}

/**
 * 校验「目标仓库名」不���含标签（0.3.0 打标签）。
 *
 * ⚠️ 这条几乎必然会被踩到：用户照着「源镜像」那一栏把 `nginx:1.27` 整个粘进仓库名。
 * 后端会拒（`1000`），但更糟的情况是拼成 `nginx:1.27:alpine` 打到另一个镜像上——
 * 因此这里在提交前就拦下。
 *
 * 判据与 Docker 一致：**冒号是否在最后一个斜杠之后**。`registry:5000/app` 的冒号
 * 在斜杠左边，是端口；而 `nginx:1.27` 的冒号在斜杠之后，是标签。
 *
 * @returns `null` = 合法；否则返回错误 i18n key。
 */
export function validateRepository(repository: string): string | null {
  const colon = repository.lastIndexOf(':')

  return colon > repository.lastIndexOf('/') ? 'image.tag.repositoryHasTag' : null
}
