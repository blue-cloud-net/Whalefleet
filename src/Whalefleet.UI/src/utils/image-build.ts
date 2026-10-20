/**
 * 镜像构建表单的校验逻辑（纯函数，返回 i18n key 而非文案）。
 *
 * ## 为什么字典字段不做成「键值对编辑器」
 * `buildArgs` / `labels` 的键是 `ARG` 名、值是字面量，用户大多从
 * `docker build --build-arg A=B` 的命令行或 CI 配置里**整段粘贴**过来。
 * 做成逐行编辑器要先拆再拼，粘贴一段 `A=1 B=2` 反而变成了手工劳动。
 * 因此收一个 JSON 对象文本，并在提交前**解析一次**告诉用户哪里不对。
 *
 * ## 为什么必须在前端先解析
 * 后端收到非法 JSON 只会回一个 `1000`（参数校验失败）——用户看不出
 * 是自己的 JSON 写错了还是别的字段错了，而这里能精确到字段名。
 *
 * ⚠️ 前端校验**不是**安全边界：后端会再解析一遍并同样拒绝。这里只是把
 * 错误提前到「点提交的那一刻」。
 */

/** 构建上下文允许的扩展名。Docker 只认 tar（含 `tar.gz` / `tgz`） */
const CONTEXT_EXTENSIONS = ['.tar', '.tar.gz', '.tgz'] as const

/**
 * 上传的文件是否像一个 tar 归档。
 *
 * 只看扩展名，**不看魔数**：`tar.gz` 的魔数是 `1f 8b`，读它要先把文件读进
 * `ArrayBuffer`（大文件会一次性占满内存），而扩展名错了的代价只是后端回一个
 * 「不是合法 tar」。前端拦这一步是为了在**选文件的瞬间**给出提示，
 * 不是替代后端校验。
 */
export function isTarArchive(filename: string): boolean {
  const lower = filename.trim().toLowerCase()

  return CONTEXT_EXTENSIONS.some((extension) => lower.endsWith(extension))
}

/** 字典字段的解析结果：`ok` 时带解析结果（空输入为 `undefined`）。 */
export type KeyValueParseResult =
  | { ok: true; value?: Record<string, string> }
  | { ok: false; errorKey: string }

/**
 * 解析 `buildArgs` / `labels` 这类「JSON 对象字符串」字段。
 *
 * 空输入 → `{ ok: true, value: undefined }`（不发送该字段，而不是发送 `{}`：
 * 后端对 `null` 与 `{}` 的处理相同，但少一个空字段少一次困惑）。
 *
 * 只接受**扁平**对象且值必须是字符串：嵌套对象与数字会被后端
 * `Dictionary<string, string>` 反序列化拒绝（数字会被静默转成字符串，
 * 而 `{"a":{"b":1}}` 直接失败），这里提前拦住并说明。
 */
export function parseKeyValueJson(text: string): KeyValueParseResult {
  const trimmed = text.trim()
  if (trimmed.length === 0) return { ok: true }

  let parsed: unknown

  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return { ok: false, errorKey: 'image.build.error.badJson' }
  }

  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { ok: false, errorKey: 'image.build.error.notObject' }
  }

  const result: Record<string, string> = {}

  for (const [key, value] of Object.entries(parsed as Record<string, unknown>)) {
    if (key.trim().length === 0) {
      return { ok: false, errorKey: 'image.build.error.emptyKey' }
    }

    // 数字 / 布尔也接受并转成字符串：`{"VERSION":1.0}` 是很容易写出来的笔误，
    // 而它的语义在 Docker 侧就是字符串，没必要为这件事拒绝用户。
    if (typeof value === 'string') {
      result[key] = value
      continue
    }

    if (typeof value === 'number' || typeof value === 'boolean') {
      result[key] = String(value)
      continue
    }

    return { ok: false, errorKey: 'image.build.error.badValue' }
  }

  return { ok: true, value: result }
}

/**
 * 构建产物引用的本地校验。
 *
 * 只拦「空」与「含空白」两类：前者必填，后者在 Docker 的引用语法里非法
 * （`my app:v1` 会被拆成两个参数）。仓库名的大小写、长度、字符集由后端
 * 的 `RegistryServerAddress` 与 Docker 自己判定——前端复制一份规则必然漂移。
 */
export function validateBuildTag(tag: string): string | null {
  const trimmed = tag.trim()

  if (trimmed.length === 0) return 'image.build.error.tagRequired'
  if (/\s/.test(trimmed)) return 'image.build.error.tagHasSpace'

  return null
}
