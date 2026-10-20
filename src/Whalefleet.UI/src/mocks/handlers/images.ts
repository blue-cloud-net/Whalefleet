import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, hostsFor, type MockScenario } from '../scenarios'
import { MOCK_IMAGE_DETAILS, MOCK_IMAGES_DEFAULT } from '../data/images'
import { registriesFor } from './registries'
import type { BatchOperationFailure, ImageDetail, ImageSummary } from '@/types/api'

/**
 * 本次会话里「拉取进来」的镜像。
 *
 * 与容器那边的 `createdInSession` 同一个理由：`pull` 只返回结果的话，
 * 紧接着的列表请求看不到它，界面表现成「拉取成功但列表里没有」——
 * 而真实守护进程当然会把它放进列表。mock 必须同样有记忆。
 */
const pulledInSession = new Map<string, ImageDetail>()

/**
 * 本次会话里被**改过标签**的夹具镜像（id → 覆盖后的列表项）。
 *
 * 夹具本身是模块级常量，**刻意不原地改**：原地改会让「跑一次走查后夹具变了」，
 * 下一轮看到的数据与初始态不一致，并行测试也会互相污染。
 * 覆盖层随页面刷新重置，与真实接口「每次请求都反映当前状态」的观感一致。
 */
const untaggedInSession = new Map<string, ImageSummary>()

/** 本次会话里被**删除**的夹具镜像 id。 */
const removedInSession = new Set<string>()

/** 清空会话内的镜像改动（供测试隔离用）。 */
export function resetSessionImages(): void {
  pulledInSession.clear()
  untaggedInSession.clear()
  removedInSession.clear()
}

/**
 * 镜像端点 mock（对应后端 0.0.10 实现的四个端点，0.4.0 追加 build / push）。
 *
 * ## 复刻了后端的哪些「容易被忽略的语义」
 * 1. **`pageSize` 夹紧**（请求 5000 → 100）：前端若用请求值算下一页就会露馅。
 * 2. **`search` 在服务端内存过滤且不分大小写**：前端不该再做一次过滤，否则两处规则会漂移。
 * 3. **不带 `force` 删除被容器引用的镜像 → 409 / 1404**：界面据此提示「先删容器」。
 *    这条不复刻，那段提示永远测不到。
 * 4. **删除返回 `untagged` / `deleted`**：多标签镜像删掉一个标签时本体仍在，
 *    界面必须把「哪些标签被解绑」告诉用户，否则会出现「我删了，列表里还在」的困惑。
 * 5. **`image` 已含标签又另传 `tag` → 1000**（后端刻意拒绝，而不是拼成 `nginx:1.27:alpine`）。
 * 6. **拉取失败两条路径**：`missing*` → `1401`（Docker 返回 404，SDK 抛异常）；
 *    `denied*` → `1402`（仓库拒绝 / 网络中断，Docker 的 HTTP 层仍是 200，错误只在进度流里）。
 */
export function imageHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  /** 当前场景下的镜像（夹具叠加会话改动，再并入会话内拉取的）。 */
  function imagesFor(current: MockScenario): ImageSummary[] {
    if (current === 'empty') return []

    const fixtures = MOCK_IMAGES_DEFAULT.filter((image) => !removedInSession.has(image.id)).map(
      (image) => untaggedInSession.get(image.id) ?? image
    )

    return [...fixtures, ...[...pulledInSession.values()].map(toSummary)]
  }

  function detailsFor(current: MockScenario): ImageDetail[] {
    if (current === 'empty') return []

    return [
      ...MOCK_IMAGE_DETAILS.filter((image) => !removedInSession.has(image.id)),
      ...pulledInSession.values()
    ]
  }

  /** 按完整 ID / 仓库名 / `仓库:标签` / short ID 查镜像（与后端接受的形态一致）。 */
  function findImage(current: MockScenario, imageId: string): ImageSummary | undefined {
    const all = imagesFor(current)

    return (
      all.find((image) => image.id === imageId) ??
      all.find((image) => image.repoTags.includes(imageId)) ??
      all.find((image) => image.id.startsWith(imageId))
    )
  }

  return [
    http.post('*/api/v1/hosts/:hostId/images/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)
      const search = String(body.search ?? '').trim().toLowerCase()
      const pageIndex = Math.max(Number(body.pageIndex ?? 0) || 0, 0)
      const pageSize = Math.min(Math.max(Number(body.pageSize ?? 20) || 20, 1), 100)

      // `search` 在后端是**服务端内存过滤**（按仓库名 / 标签模糊匹配，不分大小写）
      const all = imagesFor(scenario())
      const filtered =
        search.length === 0
          ? all
          : all.filter((image) => image.repoTags.some((tag) => tag.toLowerCase().includes(search)))

      // ⚠️ 排序必须在**分页之前**，否则 total 不变而每页内容错位——
      // 这正是后端 Sort() 的位置，前端排序之所以正确全靠这一点。
      // 规则与后端 ImageService.Sort 逐条对齐（同值用 id 兜底、未知字段回落 created）。
      const sortBy = String(body.sortBy ?? '').trim()
      const sortDir = String(body.sortDir ?? '').trim()
      const descending = sortDir.toLowerCase() !== 'asc'

      let ordered = filtered
      if (sortBy.length > 0) {
        const pick = (image: (typeof filtered)[number]): string | number => {
          switch (sortBy) {
            case 'repository':
              return image.repoTags[0] ?? ''
            case 'size':
              return image.size
            case 'usedBy':
              return image.containerCount
            default:
              return image.created
          }
        }

        ordered = [...filtered].sort((a, b) => {
          const ka = pick(a)
          const kb = pick(b)
          const result =
            typeof ka === 'number' && typeof kb === 'number'
              ? ka - kb
              : String(ka).localeCompare(String(kb))
          if (result !== 0) return descending ? -result : result
          return a.id.localeCompare(b.id)
        })
      }

      return HttpResponse.json(
        ok({
          items: ordered.slice(pageIndex * pageSize, (pageIndex + 1) * pageSize),
          total: ordered.length,
          pageIndex,
          // 回显**夹紧后**的值：前端必须用它而不是请求里的值。
          pageSize
        })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/images/inspect', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const imageId = String(body.imageId ?? '')
      const hit = findImage(scenario(), imageId)
      const detail = hit ? detailsFor(scenario()).find((image) => image.id === hit.id) : undefined

      if (!hit || !detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // 标签以列表侧为准：解绑后详情也该跟着变，否则两边会互相矛盾
      return HttpResponse.json(ok({ ...detail, repoTags: hit.repoTags }))
    }),

    http.post('*/api/v1/hosts/:hostId/images/pull', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const image = String(body.image ?? '').trim()
      const tag = String(body.tag ?? '').trim()

      if (image.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 后端硬规则：已含标签又另传 tag → 1000（不会拼成 nginx:1.27:alpine）
      if (tag.length > 0 && hasTag(image)) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '镜像名已包含标签，请勿重复指定 tag'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      // 仓库 / 镜像不存在：Docker 返回 404，SDK 抛异常 → 1401
      if (image.startsWith('missing')) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // 仓库拒绝 / 网络中断：Docker 的 HTTP 层仍是 200，错误只在进度流里 → 后端翻译成 1402
      if (image.startsWith('denied')) {
        return HttpResponse.json(
          fail(ErrorCode.DockerApiFailed, 'pull access denied for this repository'),
          { status: httpStatusOf(ErrorCode.DockerApiFailed) }
        )
      }

      const reference = tag.length > 0 ? `${image}:${tag}` : hasTag(image) ? image : `${image}:latest`
      const detail = createFixture(reference, pulledInSession.size)

      pulledInSession.set(detail.id, detail)
      removedInSession.delete(detail.id)

      return HttpResponse.json(
        ok({ imageId: detail.id, reference, size: detail.size, repoTags: detail.repoTags })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/images/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const imageId = String(body.imageId ?? '')
      const force = body.force === true
      const image = findImage(scenario(), imageId)

      if (!image) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // ⚠️ 最容易被漏的一条：被容器引用的镜像不带 force 删除 → 409。
      // 前端靠它提示「先删除引用它的容器」，不复刻这段提示就永远测不到。
      if (image.containerCount > 0 && !force) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceConflict), {
          status: httpStatusOf(ErrorCode.DockerResourceConflict)
        })
      }

      // 多标签：只解绑被点名的那个标签，镜像本体与其标签都保留
      if (image.repoTags.length > 1) {
        applyUntag(image, image.repoTags.filter((tag) => tag !== imageId))

        return HttpResponse.json(ok({ untagged: [imageId], deleted: [] }))
      }

      // 只剩一个标签，或悬挂镜像按 ID 删：整条移出列表
      pulledInSession.delete(image.id)
      untaggedInSession.delete(image.id)
      removedInSession.add(image.id)

      return HttpResponse.json(ok({ untagged: image.repoTags, deleted: [image.id] }))
    }),

    http.post('*/api/v1/hosts/:hostId/images/batch-remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const force = body.force === true
      const raw = Array.isArray(body.imageIds) ? (body.imageIds as unknown[]) : []

      // 主机不存在是**整批**失败（1300）。真实后端由 CurrentHostMiddleware 在进入端点前抛出，
      // mock 没有中间件，因此必须在这里补上。
      const hostId = new URL(request.url).pathname.match(/\/hosts\/([^/]+)\/images\/batch-remove$/)?.[1]
      if (hostId === undefined || !hostsFor(scenario()).some((host) => host.name === decodeURIComponent(hostId))) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      // 入参非法是**整批**失败（1000）；逐条失败才走 failures，两者界面上要显示成不同的东西
      const requested = normalizeBatchIds(raw)

      if (requested === null) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, batchValidationMessage(raw, 'imageIds')),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const failures: BatchOperationFailure[] = []
      let succeeded = 0

      for (const imageId of requested) {
        const image = findImage(scenario(), imageId)

        if (!image) {
          failures.push({
            resourceId: imageId,
            code: ErrorCode.DockerResourceNotFound,
            message: '镜像不存在'
          })
          continue
        }

        if (image.containerCount > 0 && !force) {
          failures.push({
            resourceId: imageId,
            code: ErrorCode.DockerResourceConflict,
            message: '镜像正被容器引用，需指定 force'
          })
          continue
        }

        pulledInSession.delete(image.id)
        untaggedInSession.delete(image.id)
        removedInSession.add(image.id)
        succeeded += 1
      }

      // 批量响应**不含**每条镜像解绑了哪些标签——那会让响应体随选中数量膨胀，
      // 而用户在批量场景下需要知道的只有「删没删掉」。
      return HttpResponse.json(ok({ requested: requested.length, succeeded, failures }))
    }),

    http.post('*/api/v1/hosts/:hostId/images/build', async ({ request }) => {
      await delay(delayFor(scenario()))

      // ⚠️ 必须走 `formData()` 而不是 `json()`：这条请求是 multipart。
      // 而**前端若忘了覆盖 Content-Type**，axios 会把 FormData 序列化成 JSON
      // （文件直接丢掉），于是这里 `context` 拿不到 —— 与真实后端表现一致，
      // 这正是这条 mock 要复刻的那个坑。
      const form = await request.formData()
      const context = form.get('context')
      const tag = String(form.get('tag') ?? '').trim()

      if (!(context instanceof File) || context.size === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'context 字段是必填的'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      if (tag.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'tag 不能为空'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const reference = hasTag(tag) ? tag : `${tag}:latest`

      // 构建失败：与真实后端一致——守护进程的 HTTP 是 200，错误只在进度流里，
      // 由后端翻译成 1806。**错误码必须是 1806 而不是 1402**：后者会让用户
      // 以为后端挂了，而真正该修的是 Dockerfile。
      if (tag.startsWith('broken')) {
        return HttpResponse.json(
          fail(ErrorCode.ImageBuildFailed, `构建镜像 ${reference} 失败：COPY failed: no such file`),
          { status: httpStatusOf(ErrorCode.ImageBuildFailed) }
        )
      }

      // 成功：造一个镜像放进会话，让「构建完成后列表里能看到它」成立
      const detail = createFixture(reference, pulledInSession.size)

      pulledInSession.set(detail.id, detail)
      removedInSession.delete(detail.id)

      return HttpResponse.json(
        ok({
          imageId: detail.id,
          reference,
          size: detail.size,
          repoTags: detail.repoTags,
          // 日志保留最后若干行；这里给一段形似真实构建输出的内容，
          // 让界面的「构建日志」面板真的有事可展示
          buildLog: [
            `Step 1/4 : FROM alpine:3.20`,
            `Step 2/4 : COPY payload.txt /payload.txt`,
            `Step 3/4 : LABEL org.opencontainers.image.title=${reference}`,
            `Step 4/4 : CMD ["/bin/sh"]`,
            `Successfully built ${detail.id.replace('sha256:', '').slice(0, 12)}`,
            `Successfully tagged ${reference}`
          ]
        })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/images/push', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const image = String(body.image ?? '').trim()
      const registryId = String(body.registryId ?? '').trim()

      if (image.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'image 不能为空'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 推送必须是「仓库:标签」形态；没标签的引用推不出去
      if (!hasTag(image)) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '镜像没有标签，无法推送'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const hit = findImage(scenario(), image)

      if (!hit) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      const serverAddress = addressOf(image)

      if (serverAddress.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, '无法从引用解析出 Registry 地址'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 显式指定的配置必须存在（1800）；不给则按地址自动匹配
      const registries = registriesFor(scenario())
      const explicit = registryId.length > 0
        ? registries.find((item) => item.id === registryId)
        : undefined

      if (registryId.length > 0 && !explicit) {
        return HttpResponse.json(
          fail(ErrorCode.RegistryNotFound, `未找到 Registry 配置 ${registryId}`),
          { status: httpStatusOf(ErrorCode.RegistryNotFound) }
        )
      }

      const matched =
        explicit ?? registries.find((item) => item.serverAddress.toLowerCase() === serverAddress)

      // 两条失败路径：不可达 1805、凭据被拒 1804（与 registry 的 test 端点同约定）
      if (serverAddress.startsWith('unreachable')) {
        return HttpResponse.json(fail(ErrorCode.RegistryUnreachable, 'dial tcp: connection refused'), {
          status: httpStatusOf(ErrorCode.RegistryUnreachable)
        })
      }

      if (serverAddress.startsWith('denied')) {
        return HttpResponse.json(
          fail(ErrorCode.RegistryAuthFailed, '401 Unauthorized from /v2/'),
          { status: httpStatusOf(ErrorCode.RegistryAuthFailed) }
        )
      }

      const authenticated = matched?.hasCredentials === true

      return HttpResponse.json(
        ok({
          imageId: hit.id,
          reference: image,
          registryServerAddress: serverAddress,
          authenticated,
          pushLog: [
            `The push refers to repository [${serverAddress}/${repositoryOf(image)}]`,
            `${shortOf(hit.id)}: Pushed`,
            `${image.split(':').pop()}: digest: sha256:${'0'.repeat(8)} size: 528`,
            authenticated ? 'authenticated: true' : 'authenticated: false'
          ]
        })
      )
    })
  ]
}

/** 引用里的地址段（与后端 `ImageReference.ExtractServerAddress` 同判据的简化版）。 */
function addressOf(reference: string): string {
  const repository = reference.slice(0, reference.lastIndexOf(':'))
  const slash = repository.indexOf('/')

  if (slash <= 0) return 'registry-1.docker.io'

  const prefix = repository.slice(0, slash)
  const looksLikeHost = prefix.includes('.') || prefix.includes(':') || prefix.toLowerCase() === 'localhost'

  return looksLikeHost ? prefix.toLowerCase() : 'registry-1.docker.io'
}

/** 引用里的仓库段（去掉地址前缀与标签）。 */
function repositoryOf(reference: string): string {
  const withoutTag = reference.slice(0, reference.lastIndexOf(':'))
  const slash = withoutTag.indexOf('/')

  return slash > 0 && (withoutTag.slice(0, slash).includes('.') || withoutTag.slice(0, slash).includes(':'))
    ? withoutTag.slice(slash + 1)
    : withoutTag
}

/** 短 ID（12 位）。 */
function shortOf(id: string): string {
  return id.replace('sha256:', '').slice(0, 12)
}

/**
 * 批量入参非法的**具体**原因（与后端 `BatchRequest.NormalizeIds` 的消息一致）。
 *
 * 复刻具体文案而不是用默认的「参数校验失败」：「101 个 ID 超了上限 100」这个信息
 * 只有说清楚才有用，笼统的报错只会让人反复重试同一个必然失败的操作。
 */
function batchValidationMessage(raw: unknown[], field: string): string {
  if (raw.length === 0) return `${field} 不能为空`

  if (raw.length > 100) return `${field} 最多 100 个，当前 ${raw.length} 个`

  return `${field} 不能全为空值`
}

/**
 * 批量 ID 列表的归一化（与后端 `BatchRequest.NormalizeIds` 同规则）。
 *
 * @returns 去空白、去空项、去重后的 ID；**列表为空 / 超限 / 全为空串时返回 null**（对应后端 1000）。
 */
function normalizeBatchIds(raw: unknown[]): string[] | null {
  if (raw.length === 0) return null

  // ⚠️ 上限必须与后端一致：服务端超过 100 就整批 400，而 mock 不拦的话
  // 前端会以为「1000 条也能批量删」，到真机上才发现整批失败。
  if (raw.length > 100) return null

  const seen = new Set<string>()
  const normalized: string[] = []

  for (const item of raw) {
    const trimmed = typeof item === 'string' ? item.trim() : ''
    if (trimmed.length === 0 || seen.has(trimmed)) continue

    seen.add(trimmed)
    normalized.push(trimmed)
  }

  return normalized.length > 0 ? normalized : null
}

/** 把「只解绑一个标签」的结果记进会话覆盖层（不改夹具本身）。 */
function applyUntag(image: ImageSummary, remaining: string[]): void {
  const pulled = pulledInSession.get(image.id)

  if (pulled) {
    pulledInSession.set(image.id, { ...pulled, repoTags: remaining })
    return
  }

  untaggedInSession.set(image.id, { ...image, repoTags: remaining })
}

/** 仓库名是否已含标签（冒号在最后一个斜杠之后）——与后端判据一致。 */
function hasTag(reference: string): boolean {
  return reference.lastIndexOf(':') > reference.lastIndexOf('/')
}

/** 由拉取请求造出一个镜像详情，让列表与详情在拉取后立刻可见。 */
function createFixture(reference: string, index: number): ImageDetail {
  const hex = (index + 1).toString(16).padStart(2, '0')

  return {
    id: `sha256:${hex}${'a1b2c3d4e5f6'.repeat(5)}`,
    repoTags: [reference],
    repoDigests: [`${reference.split(':')[0]}@sha256:${hex}${'a1b2c3d4e5f6'.repeat(5)}`],
    created: new Date().toISOString(),
    size: 52_428_800,
    architecture: 'amd64',
    os: 'linux',
    osVersion: '',
    author: '',
    comment: '',
    env: ['PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin'],
    cmd: ['/bin/sh'],
    entrypoint: [],
    exposedPorts: [],
    volumes: [],
    labels: {},
    workingDir: '',
    layerCount: 4
  }
}

/** 详情 → 列表项。 */
function toSummary(detail: ImageDetail): ImageSummary {
  return {
    id: detail.id,
    repoTags: detail.repoTags,
    repoDigests: detail.repoDigests,
    created: detail.created,
    size: detail.size,
    sharedSize: 0,
    containerCount: 0
  }
}
