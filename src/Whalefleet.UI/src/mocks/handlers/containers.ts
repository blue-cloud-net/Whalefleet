import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, hostsFor, type MockScenario } from '../scenarios'
import {
  MOCK_CONTAINER_DETAILS,
  MOCK_CONTAINER_LOGS,
  MOCK_CONTAINERS_DEFAULT
} from '../data/containers'
import type { BatchOperationFailure, ContainerDetail, ContainerSummary } from '@/types/api'

/**
 * 本次会话里「创建出来」的容器。
 *
 * ⚠️ 必须有这个东西，否则 mock 的「创建 → 启动」链路是断的：
 * `create` 只能返回一个编造的 ID，而紧接着的 `start` 去查容器列表时找不到它，
 * 于是回 1401——界面表现成「刚创建就启动失败」，让人以为是前端的 bug。
 * 真实守护进程当然不是这样（创建出来的容器立刻就可操作），mock 必须同样有记忆。
 *
 * 只存在内存里，不持久化：刷新页面重置是合理的（Mock 本来就是一次性夹具）。
 */
const createdInSession = new Map<string, CreatedContainer>()

/**
 * 本次会话里被**删除**的夹具容器 id。
 *
 * 与镜像那边的 `removedInSession` 同构：`createdInSession.delete()` 只对会话新建的容器有效，
 * 而夹具容器是模块级常量，删掉它就意味着改了夹具本身（会让下一次走查看到不同的数据）。
 * 因此用「已删除集合」过滤，让列表与删除结果保持自洽——
 * 否则删完刷新它又回来了，界面上看起来像删除没生效。
 */
const removedInSession = new Set<string>()

/**
 * 本次会话里被**重命名**的容器：`原 id → 新名字`。
 *
 * ⚠️ 为什么用「覆盖表」而不是直接改夹具：夹具是模块级常量，
 * 改了它就意味着「跑一次走查后夹具变了」——下一轮看到的状态与初始态不一致，
 * 平行跑测试时还会互相污染（和 `applyStateChange` 同一个理由）。
 *
 * 对追加挂载（重建）同样适用：旧容器被让位改名成 `whalefleet-replaced-*`，
 * 而它的 **id 不变**——这正是「让位而不是删除」在 mock 里的可观察形态。
 */
const renamedInSession = new Map<string, string>()

/**
 * 本次会话里被**热更新**过的容器，只记录本次改了的字段。
 *
 * 未出现在这里的字段表示「没改过」，读取时回落到夹具原值——
 * 这正是后端「先 inspect 再按当前值回填」语义在 mock 里的对应物：
 * 不记的话 mock 会把「只改重启策略」表现成「内存上限也被清空了」，
 * 而真实后端不会那样。
 */
const updatedInSession = new Map<string, ContainerUpdateOverride>()

/** 热更新覆盖项：未列出的字段保持原值。 */
interface ContainerUpdateOverride {
  restartPolicy?: string
  /** `null` = 取消限制（后端把它投影成 null），与「没改」是两种不同的状态 */
  memoryLimitBytes?: number | null
  nanoCpus?: number | null
}

/** 本次会话新建的容器：详情 + 列表项需要的端口映射（`ContainerDetail` 里没有端口字段）。 */
interface CreatedContainer {
  detail: ContainerDetail
  ports: ContainerSummary['ports']
}

/** 清空会话内创建的容器（供测试隔离用）。 */
export function resetSessionContainers(): void {
  createdInSession.clear()
  removedInSession.clear()
  renamedInSession.clear()
  updatedInSession.clear()
}

/** 取容器当前的名字（应用本会话的重命名覆盖）。 */
function currentName(containerId: string, fallback: string): string {
  return renamedInSession.get(containerId) ?? fallback
}

/**
 * 把本会话的重命名覆盖应用到列表项上。
 *
 * 只有名字可改：<see cref="ContainerSummary"/> 不含 hostConfig 字段，
 * 所以热更新的结果只能从详情看到（列表上本来也不显示内存 / CPU 限制）。
 */
function applyOverrides(item: ContainerSummary): ContainerSummary {
  const name = currentName(item.id, item.name)

  return name === item.name ? item : { ...item, name }
}

/**
 * 把本会话的重命名与热更新覆盖应用到详情上。
 *
 * ⚠️ `update` 的回显语义靠这里成立：后端在更新前先 inspect、把未改字段按当前值回填，
 * 因此详情里的 `memoryLimitBytes` 是**更新后**的值，而不是 0。
 */
function applyDetailOverrides(detail: ContainerDetail): ContainerDetail {
  const name = currentName(detail.id, detail.name)
  const override = updatedInSession.get(detail.id)

  if (name === detail.name && !override) return detail

  return {
    ...detail,
    name,
    hostConfig: {
      ...detail.hostConfig,
      restartPolicy: override?.restartPolicy ?? detail.hostConfig.restartPolicy,
      memoryLimitBytes:
        override?.memoryLimitBytes === undefined
          ? detail.hostConfig.memoryLimitBytes
          : override.memoryLimitBytes,
      nanoCpus:
        override?.nanoCpus === undefined ? detail.hostConfig.nanoCpus : override.nanoCpus
    }
  }
}

/**
 * 容器端点 mock（对应后端 0.0.9 已实现的八个端点）。
 *
 * ## 复刻了后端的哪些「容易被忽略的语义」
 * mock 的价值不在于「返回一份假数据」，而在于**让前端的分支能被走查到**。
 * 因此这里刻意复刻：
 *
 * 1. **`force` 与 1404**：不带 force 删除运行中的容器 → 409/1404。
 *    前端据此提示「先停止」。不模拟它，那段提示永远测不到。
 * 2. **`pageSize` 夹紧**：请求 5000 时回 100。前端若用请求值算「还有没有下一页」，
 *    就在这里露馅（真实后端同样会夹紧）。
 * 3. **`logs` 的 `tail` 越界**：> 10000 → 1000/400（不是静默截断）。
 * 4. **容器不存在**：→ 1401/404。
 * 5. **`create` 的 warnings**：创建成功同时回带提示。
 */
export function containerHandlers(scenario: () => MockScenario) {
  /** 路径形如 `/api/v1/hosts/{hostId}/containers/{action}`，取 hostId 与 action。 */
  function parse(request: Request): { hostId: string; action: string } {
    const match = new URL(request.url).pathname.match(/\/hosts\/([^/]+)\/containers\/([^/]+)$/)

    return {
      hostId: match ? decodeURIComponent(match[1]) : '',
      action: match ? match[2] : ''
    }
  }

  /** 当前场景下的容器列表（含本次会话新建的，已删除的除外）。 */
  function containersFor(current: MockScenario): ContainerSummary[] {
    if (current === 'empty') return []

    return [
      ...MOCK_CONTAINERS_DEFAULT.filter((item) => !removedInSession.has(item.id)),
      ...[...createdInSession.values()].map((item) => toSummary(item))
    ].map(applyOverrides)
  }

  /** 全部可查详情的容器（夹具 + 会话新建，已删除的除外）。 */
  function detailsFor(current: MockScenario): ContainerDetail[] {
    if (current === 'empty') return []

    return [
      ...MOCK_CONTAINER_DETAILS.filter((item) => !removedInSession.has(item.id)),
      ...[...createdInSession.values()].map((item) => item.detail)
    ].map(applyDetailOverrides)
  }

  /**
   * 按名字查容器（复制时要避让重名、重命名要判占用）。
   *
   * ⚠️ 必须比的是**当前**名字而不是夹具里的原名：让位改名之后旧容器的名字已经变了，
   * 用原名比会让「它现在叫什么」与「这个名字有没有被占」对不上。
   */
  function findByName(current: MockScenario, name: string): ContainerSummary | undefined {
    return containersFor(current).find((item) => item.name === name)
  }

  /**
   * 在 `-copy` / `-copy-2` / `-copy-3` … 里找一个没被占用的名字。
   *
   * ⚠️ 必须避让而不是直接失败：复制最常见的用法就是「连点两次」，
   * 第二次拿到 409 会让人以为系统有问题。
   */
  function freeName(current: MockScenario, baseName: string): string {
    if (!findByName(current, baseName)) return baseName

    for (let attempt = 2; attempt <= 10; attempt += 1) {
      const candidate = `${baseName}-${attempt}`
      if (!findByName(current, candidate)) return candidate
    }

    return `${baseName}-${Date.now()}`
  }

  /** 取容器的端口映射（详情里没有端口字段，复制 / 重建都要带上它）。 */
  function portsOf(current: MockScenario, containerId: string): ContainerSummary['ports'] {
    return containersFor(current).find((item) => item.id === containerId)?.ports ?? []
  }

  /** 标记删除：会话新建的从会话态移除，夹具的进已删除集合（两种来源都要真的从列表里消失）。 */
  function markRemoved(containerId: string): void {
    createdInSession.delete(containerId)
    removedInSession.add(containerId)
    renamedInSession.delete(containerId)
    updatedInSession.delete(containerId)
  }

  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  return [
    http.post('*/api/v1/hosts/:hostId/containers/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)
      const pageIndex = Math.max(Number(body.pageIndex ?? 0) || 0, 0)
      const pageSize = Math.min(Math.max(Number(body.pageSize ?? 20) || 20, 1), 100)
      const all = body.all !== false

      // `all: false` 等价 `docker ps`：只列运行中的。
      const visible = containersFor(scenario()).filter((item) => all || item.state === 'running')

      // ⚠️ 排序必须在**分页之前**，否则 total 不变而每页内容错位——
      // 这正是后端 Sort() 的位置，前端排序之所以正确全靠这一点。
      // 规则与后端 ContainerService.Sort 逐条对齐（同值用 id 兜底、未知字段回落 created）。
      const sortBy = String(body.sortBy ?? '').trim()
      const descending = String(body.sortDir ?? '').trim().toLowerCase() !== 'asc'

      let ordered = visible
      if (sortBy.length > 0) {
        const pick = (item: ContainerSummary): string => {
          switch (sortBy) {
            case 'name':
              return item.name
            case 'image':
              return item.image
            case 'state':
              return item.state
            default:
              return item.created
          }
        }

        ordered = [...visible].sort((a, b) => {
          const result = pick(a).localeCompare(pick(b))
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

    http.post('*/api/v1/hosts/:hostId/containers/inspect', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const id = String(body.containerId ?? '')
      const detail = detailsFor(scenario()).find((item) => item.id === id)

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      return HttpResponse.json(ok(detail))
    }),

    http.post('*/api/v1/hosts/:hostId/containers/logs', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const tail = body.tail === undefined ? null : Number(body.tail)

      // 与后端一致：tail 越界是报错而不是静默截断。
      if (tail !== null && (!Number.isInteger(tail) || tail < 0 || tail > 10000)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      return HttpResponse.json(
        ok({
          containerId: String(body.containerId ?? ''),
          content: tail === 0 ? '' : MOCK_CONTAINER_LOGS
        })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/containers/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const image = String(body.image ?? '')

      if (image.trim().length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 镜像不在本地 → 1401（复刻后端：守护进程返回 404 时翻译成「资源不存在」）。
      if (image.startsWith('missing')) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // 记住这个容器：否则接下来的 `start` / `inspect` 会因找不到它而回 1401，
      // 界面表现成「刚创建就启动失败」，而真实守护进程并非如此。
      const containerId = `e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6${createdInSession.size}`

      createdInSession.set(containerId, createFixture(containerId, body))
      return HttpResponse.json(
        ok({
          containerId,
          // 真实守护进程会在这里给提示（例如「端口未映射」）。
          // 前端必须把它当「提示」而不是「失败」渲染。
          warnings: body.ports ? [] : ['该容器没有端口映射，无法从宿主机访问']
        })
      )
    }),

    ...(['start', 'stop', 'restart'] as const).map((action) =>
      http.post(`*/api/v1/hosts/:hostId/containers/${action}`, async ({ request }) => {
        await delay(delayFor(scenario()))

        const { hostId } = parse(request)
        const body = await readBody(request)
        const containerId = String(body.containerId ?? '')

        if (containerId.trim().length === 0) {
          return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
            status: httpStatusOf(ErrorCode.ValidationFailed)
          })
        }

        // 未知容器 → 1401；这三个端点都是**幂等**的，因此不需要模拟 304。
        if (!containersFor(scenario()).some((item) => item.id === containerId)) {
          return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
            status: httpStatusOf(ErrorCode.DockerResourceNotFound)
          })
        }

        // stop 的 waitSeconds 越界 → 1000（与后端一致：0–300）。
        if (action === 'stop' && body.waitSeconds != null) {
          const wait = Number(body.waitSeconds)
          if (!Number.isInteger(wait) || wait < 0 || wait > 300) {
            return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
              status: httpStatusOf(ErrorCode.ValidationFailed)
            })
          }
        }

        void hostId

        // 会话新建的容器要真的跟着变状态：否则界面会显示「启动成功」但行里仍是「已创建」，
        // 走查时看起来像前端没刷新，而真相是 mock 没记事。
        applyStateChange(containerId, action)

        return HttpResponse.json(ok(null))
      })
    ),

    http.post('*/api/v1/hosts/:hostId/containers/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const containerId = String(body.containerId ?? '')
      const force = body.force === true
      const container = containersFor(scenario()).find((item) => item.id === containerId)

      if (!container) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // ⚠️ 这一条最容易漏：不带 force 删运行中的容器，守护进程返回 409。
      // 前端要靠它提示「先停止容器」，因此 mock 必须能产生这个分支。
      if (container.state === 'running' && !force) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceConflict), {
          status: httpStatusOf(ErrorCode.DockerResourceConflict)
        })
      }

      // 删除成功则从列表移除：否则删完再点「刷新」它又回来了。
      markRemoved(containerId)

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/hosts/:hostId/containers/batch-remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const { hostId } = parse(request)
      const body = await readBody(request)
      const force = body.force === true
      const raw = Array.isArray(body.containerIds) ? (body.containerIds as unknown[]) : []

      // ⚠️ 主机不存在是**整批**失败（1300）。真实后端由 CurrentHostMiddleware 在进入端点前
      // 抛出，mock 没有中间件，因此必须在这里补上——否则前端在 mock 下永远走不到
      // 「主机已被删除」这条分支，而真机上刷新一个旧深链恰好会碰到它。
      if (!hostsFor(scenario()).some((host) => host.name === hostId)) {
        return HttpResponse.json(fail(ErrorCode.HostNotFound), {
          status: httpStatusOf(ErrorCode.HostNotFound)
        })
      }

      // ⚠️ 批量端点的入参校验同样是**整批**失败（1000），与逐条失败形态不同：
      // 界面据此显示「操作无效」而不是「部分成功」。
      const requested = normalizeBatchIds(raw)

      if (requested === null) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, batchValidationMessage(raw, 'containerIds')),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const containers = containersFor(scenario())
      const failures: BatchOperationFailure[] = []
      let succeeded = 0

      for (const containerId of requested) {
        const container = containers.find((item) => item.id === containerId)

        if (!container) {
          // 逐条失败：错误码与单条端点**完全一致**，界面才能复用同一套文案
          failures.push({
            resourceId: containerId,
            code: ErrorCode.DockerResourceNotFound,
            message: '容器不存在'
          })
          continue
        }

        if (container.state === 'running' && !force) {
          failures.push({
            resourceId: containerId,
            code: ErrorCode.DockerResourceConflict,
            message: '容器正在运行，需指定 force'
          })
          continue
        }

        createdInSession.delete(containerId)
        removedInSession.add(containerId)
        succeeded += 1
      }

      // 部分成功也是 200：真正的错误响应只有「入参非法」与「主机不可达」两种
      return HttpResponse.json(
        ok({ requested: requested.length, succeeded, failures })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/containers/rename', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const containerId = String(body.containerId ?? '').trim()
      const name = String(body.name ?? '').trim()
      const current = scenario()

      if (containerId.length === 0 || name.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 名称规则与创建/复制一致（后端 RequireContainerName）。
      // ⚠️ 这条必须复刻：非法名称在真机上返回 1000，而「名字随便填」的前端
      // 在 mock 下永远走不到那个分支。
      if (!CONTAINER_NAME_PATTERN.test(name)) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '容器名只能包含字母、数字与 _ . -，且必须以字母或数字开头'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const container = containersFor(current).find((item) => item.id === containerId)

      if (!container) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // 名字已被占用 → 1404/409（不是 1000）：改一下就能成功，属「换个名字」而不是「参数错了」
      if (findByName(current, name)) {
        return HttpResponse.json(
          fail(ErrorCode.DockerResourceConflict, `容器名「${name}」已被占用`),
          { status: httpStatusOf(ErrorCode.DockerResourceConflict) }
        )
      }

      renamedInSession.set(containerId, name)

      return HttpResponse.json(ok({ containerId, name }))
    }),

    http.post('*/api/v1/hosts/:hostId/containers/update', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const containerId = String(body.containerId ?? '').trim()

      if (containerId.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const restartPolicy = body.restartPolicy
      const memoryLimitBytes = body.memoryLimitBytes
      const nanoCpus = body.nanoCpus

      // 全省略等于什么都不改 → 1000（真机也返回 1000）。
      // 不模拟它的话，「三个字段都没填就点保存」在 mock 下会显示成功。
      if (
        restartPolicy === undefined &&
        memoryLimitBytes === undefined &&
        nanoCpus === undefined
      ) {
        return HttpResponse.json(
          fail(
            ErrorCode.ValidationFailed,
            'restartPolicy / memoryLimitBytes / nanoCpus 至少要给一个（全省略等同于什么都不改）'
          ),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const valid = validateUpdate({ restartPolicy, memoryLimitBytes, nanoCpus })

      if (valid !== null) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, valid), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const detail = detailsFor(scenario()).find((item) => item.id === containerId)

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      const current = updatedInSession.get(containerId) ?? {}

      updatedInSession.set(containerId, {
        restartPolicy: (restartPolicy as string) ?? current.restartPolicy,
        // ⚠️ 只有**显式传了 0** 才写 null（取消限制）；没传就保持原值。
        // 这正是后端「先 inspect 再按当前值回填」的语义在 mock 里的对应物。
        memoryLimitBytes:
          memoryLimitBytes === undefined
            ? current.memoryLimitBytes
            : memoryLimitBytes === 0
              ? null
              : (memoryLimitBytes as number),
        nanoCpus:
          nanoCpus === undefined
            ? current.nanoCpus
            : nanoCpus === 0
              ? null
              : (nanoCpus as number)
      })

      // 回带**更新后实际生效**的值（未传的字段保持当前值），与后端一致
      const applied = applyDetailOverrides(detail)

      return HttpResponse.json(
        ok({
          containerId,
          restartPolicy: applied.hostConfig.restartPolicy,
          memoryLimitBytes: applied.hostConfig.memoryLimitBytes,
          nanoCpus: applied.hostConfig.nanoCpus
        })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/containers/duplicate', async ({ request }) => {
      await delay(delayFor(scenario()))

      const current = scenario()
      const body = await readBody(request)
      const containerId = String(body.containerId ?? '').trim()

      const source = detailsFor(current).find((item) => item.id === containerId)

      if (!source) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      const requested = String(body.name ?? '').trim()
      const baseName = requested.length > 0 ? requested : `${source.name}-copy`

      if (!CONTAINER_NAME_PATTERN.test(baseName)) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, '容器名只能包含字母、数字与 _ . -，且必须以字母或数字开头'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      // 连点两次必须都能成功：默认名被占用时自动递增 -2 / -3
      const name = freeName(current, baseName)
      const copyId = `d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3${createdInSession.size}`

      createdInSession.set(copyId, {
        detail: { ...structuredClone(source), id: copyId, name },
        ports: portsOf(current, containerId)
      })

      if (body.start === true) {
        applyStateChange(copyId, 'start')
      }

      // ⚠️ recreated=false：复制是「多了一个容器」，**原容器毫发无损**。
      // 前端据此把提示写成「已复制」，而不是「已替换」。
      return HttpResponse.json(
        ok({ containerId: copyId, name, replacedContainerId: null, running: body.start === true, warnings: [], recreated: false })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/containers/add-volumes', async ({ request }) => {
      await delay(delayFor(scenario()))

      const current = scenario()
      const body = await readBody(request)
      const containerId = String(body.containerId ?? '').trim()
      const volumes = Array.isArray(body.volumes) ? body.volumes : []

      if (containerId.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 空列表意味着「重建了容器却什么也没加」——一次没有任何收益的重建
      if (volumes.length === 0) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, 'volumes 不能为空（要追加的挂载不能为空列表）'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const source = detailsFor(current).find((item) => item.id === containerId)

      if (!source) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      const added: Array<{ source: string; destination: string; readOnly?: boolean }> = []

      for (const item of volumes) {
        const entry = item as { source?: unknown; destination?: unknown; readOnly?: unknown }
        const from = String(entry.source ?? '').trim()
        const to = String(entry.destination ?? '').trim()

        if (from.length === 0 || to.length === 0) {
          return HttpResponse.json(
            fail(ErrorCode.ValidationFailed, 'volumes[].source 与 volumes[].destination 都不能为空'),
            { status: httpStatusOf(ErrorCode.ValidationFailed) }
          )
        }

        // 同一目标路径挂两次 → 1000。这种请求几乎总是配置错了（后端同样提前拒掉），
        // 而它的表现是「重建失败」，界面上看不出是哪里重了。
        if (source.mounts.some((mount) => mount.destination === to)) {
          return HttpResponse.json(
            fail(
              ErrorCode.ValidationFailed,
              `容器 ${source.name} 已经挂载了 ${to}；追加挂载不允许重复同一目标路径（要替换请先删掉原挂载）`
            ),
            { status: httpStatusOf(ErrorCode.ValidationFailed) }
          )
        }

        added.push({ source: from, destination: to, readOnly: entry.readOnly === true })
      }

      // ===== 重建：旧容器让位（改名保留）→ 新容器用原名建出来 =====
      // ⚠️ 顺序不能反：先让位才能用回原名，反过来会撞「名字被占用」。
      // 让位名带旧 ID 的前 12 位 —— 连续重建同一容器时，用户要能分辨「哪一代被换掉了」。
      const parkedName = `whalefleet-replaced-${containerId.slice(0, 12)}`

      renamedInSession.set(containerId, parkedName)

      const newId = `f0e1d2c3b4a5968778695a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1${createdInSession.size}`

      createdInSession.set(newId, {
        detail: {
          ...structuredClone(source),
          id: newId,
          name: source.name,
          mounts: [
            ...source.mounts,
            ...added.map((volume) => ({
              type: 'bind',
              source: volume.source,
              destination: volume.destination,
              readWrite: volume.readOnly !== true
            }))
          ]
        },
        ports: portsOf(current, containerId)
      })

      // start 省略时**沿用原容器的运行状态**——不擅自启动一个本来没运行的容器
      const start = typeof body.start === 'boolean' ? body.start : source.state.running

      if (start) {
        applyStateChange(newId, 'start')
      }

      // recreated=true 且 replacedContainerId 指向旧容器：
      // 界面上那个「来历不明的容器」就是它，必须显式提示用户可以删。
      return HttpResponse.json(
        ok({ containerId: newId, name: source.name, replacedContainerId: containerId, running: start, warnings: [], recreated: true })
      )
    })
  ]
}

/** 容器名是否合法（与后端 `RequireContainerName` 同规则）。 */
const CONTAINER_NAME_PATTERN = /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/

/** 重启策略的合法取值（与后端 `BuildRestartPolicy` 一致）。 */
const RESTART_POLICIES = new Set(['no', 'always', 'on-failure', 'unless-stopped'])

/** 内存下限：6 MiB（守护进程硬性下限，实测 6291455 被拒）。 */
const MIN_MEMORY_LIMIT_BYTES = 6 * 1024 * 1024

/** CPU 配额上限：1024 核 × 10⁹。 */
const MAX_NANO_CPUS = 1024 * 1_000_000_000

/**
 * 校验热更新请求体，返回**具体**的失败原因（`null` 表示通过）。
 *
 * ⚠️ 三条最容易漏的规则都在这里，各对应一个真机上「返回成功但没生效」或「报错指错地方」：
 * 1. `0` 被拒——Docker 的 update 对 0 是「返回成功但不变」；
 * 2. 内存下限 6 MiB——低于它守护进程在创建时就回 400；
 * 3. CPU 上限——「把毫核当十亿分之一核」传 2e9 是常见错值。
 */
function validateUpdate(body: {
  restartPolicy?: unknown
  memoryLimitBytes?: unknown
  nanoCpus?: unknown
}): string | null {
  if (body.restartPolicy !== undefined) {
    const policy = String(body.restartPolicy)

    if (!RESTART_POLICIES.has(policy)) {
      return `restartPolicy「${policy}」非法，只能是 no / always / on-failure / unless-stopped`
    }
  }

  if (body.memoryLimitBytes !== undefined) {
    const memory = Number(body.memoryLimitBytes)

    if (!Number.isInteger(memory) || memory < 0) {
      return 'memoryLimitBytes 必须是非负整数'
    }

    if (memory === 0) {
      return 'memoryLimitBytes 不能为 0：Docker 的热更新把 0 当作「未指定」（会返回成功但不生效）。要取消内存上限必须重建容器'
    }

    if (memory < MIN_MEMORY_LIMIT_BYTES) {
      return `memoryLimitBytes 至少为 ${MIN_MEMORY_LIMIT_BYTES} 字节（6 MiB，Docker 守护进程的硬性下限），当前 ${memory}`
    }
  }

  if (body.nanoCpus !== undefined) {
    const cpus = Number(body.nanoCpus)

    if (!Number.isInteger(cpus) || cpus < 0) {
      return 'nanoCpus 必须是非负整数'
    }

    if (cpus === 0) {
      return 'nanoCpus 不能为 0：Docker 的热更新把 0 当作「未指定」（会返回成功但不生效）。要取消 CPU 限制必须重建容器'
    }

    if (cpus > MAX_NANO_CPUS) {
      return `nanoCpus 最多 ${MAX_NANO_CPUS}（1024 核 × 10⁹），当前 ${cpus}`
    }
  }

  return null
}

/**
 * 批量入参非法的**具体**原因（与后端 `BatchRequest.NormalizeIds` 的消息一致）。
 *
 * 为什么不直接用 `fail(code)` 的默认文案：默认文案是「参数校验失败」，
 * 而「101 个 ID 超了上限 100」这个信息只有说清楚才有用——用户看到笼统的报错
 * 只会反复重试，而重试还是同一个错误。
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

/**
 * 把状态变更应用到会话新建的容器上。
 *
 * 夹具（`MOCK_CONTAINER_DETAILS`）是模块级常量，刻意不改它们：
 * 改了就变成「跑一次走查后夹具变了」，下一轮看到的状态与初始态不一致
 * （而且并行跑测试时互相污染）。只有本轮会话新建的容器是可变的。
 */
function applyStateChange(containerId: string, action: 'start' | 'stop' | 'restart'): void {
  const created = createdInSession.get(containerId)

  if (!created) return

  const { state } = created.detail

  if (action === 'stop') {
    state.status = 'exited'
    state.running = false
    state.finishedAt = new Date().toISOString()
    // 优雅停止成功，Mock 固定给 0（真实场景可能是 137，那取决于进程是否响应 SIGTERM）。
    state.exitCode = 0
    return
  }

  state.status = 'running'
  state.running = true
  state.startedAt = state.startedAt ?? new Date().toISOString()
  state.finishedAt = null
  state.exitCode = null
}

/**
 * 由创建请求造出一个「已创建但未启动」的容器详情。
 *
 * 状态必须是 `created` 而**不是** `running`——「创建不自动启动」是后端的明确语义
 * （见 api-spec 的变更端点说明）。mock 若在这里返回 running，界面会立刻显示成运行中，
 * 用户点「启动」时反而看到「已启动」的空操作，把真实语义掩盖掉。
 */
function createFixture(id: string, body: Record<string, unknown>): CreatedContainer {
  const cmd = Array.isArray(body.cmd) ? (body.cmd as string[]) : []

  // 端口映射要一并推出来：新建的容器如果永远显示「无端口」，
  // 用户会以为端口映射没生效（而它其实生效了）。
  const ports: ContainerSummary['ports'] = Array.isArray(body.ports)
    ? (
        body.ports as Array<{ containerPort: number; hostPort?: number; type?: string; hostIp?: string }>
      ).map((port) => ({
        privatePort: port.containerPort,
        publicPort: port.hostPort ?? null,
        type: port.type ?? 'tcp',
        ip: port.hostIp ?? null
      }))
    : []

  const detail: ContainerDetail = {
    id,
    name: String(body.name ?? 'generated-name'),
    created: new Date().toISOString(),
    image: String(body.image ?? ''),
    state: {
      status: 'created',
      running: false,
      paused: false,
      restarting: false,
      startedAt: null,
      finishedAt: null,
      exitCode: null,
      oomKilled: false,
      healthStatus: null
    },
    config: {
      image: String(body.image ?? ''),
      cmd,
      entrypoint: Array.isArray(body.entrypoint) ? (body.entrypoint as string[]) : [],
      env: Array.isArray(body.env) ? (body.env as string[]) : [],
      labels: (body.labels as Record<string, string>) ?? {},
      workingDir: String(body.workingDir ?? ''),
      hostname: String(body.hostname ?? id.slice(0, 12)),
      user: '',
      tty: body.tty === true
    },
    hostConfig: {
      restartPolicy: String(body.restartPolicy ?? 'no'),
      autoRemove: body.autoRemove === true,
      networkMode: String(body.network ?? 'bridge'),
      privileged: false,
      memoryLimitBytes: null,
      nanoCpus: null
    },
    mounts: Array.isArray(body.volumes)
      ? (body.volumes as Array<{ source: string; destination: string; readOnly?: boolean }>).map(
          (volume) => ({
            type: 'bind',
            source: volume.source,
            destination: volume.destination,
            readWrite: volume.readOnly !== true
          })
        )
      : [],
    networks: []
  }

  return { detail, ports }
}

/**
 * 由详情派生列表项，端口由创建请求里的映射推出。
 *
 * 端口必须一起推出来：新建的容器如果永远显示「无端口」，用户会以为端口映射没生效。
 */
function toSummary(item: CreatedContainer): ContainerSummary {
  const { detail, ports } = item

  return {
    id: detail.id,
    name: detail.name,
    image: detail.image,
    imageId: `sha256:${detail.id.slice(0, 32)}`,
    command: detail.config.cmd.join(' '),
    created: detail.created,
    state: detail.state.status,
    status: detail.state.running ? 'Up 0 seconds' : 'Created',
    ports
  }
}
