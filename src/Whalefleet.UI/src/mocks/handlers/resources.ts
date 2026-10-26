import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import {
  MOCK_NETWORK_DETAILS,
  MOCK_NETWORKS_DEFAULT,
  MOCK_VOLUME_DETAILS,
  MOCK_VOLUMES_DEFAULT
} from '../data/resources'
import { MOCK_CONTAINERS_DEFAULT } from '../data/containers'
import type {
  BatchOperationResult,
  NetworkDetail,
  NetworkSummary,
  VolumeDetail,
  VolumeSummary
} from '@/types/api'

/**
 * 本次会话里新建的数据卷。
 *
 * 与镜像那边的 `pulledInSession` 同一个理由：创建接口只返回结果的话，
 * 紧接着的列表请求看不到它，界面表现成「创建成功但列表里没有」。
 * mock 必须同样有记忆。
 */
const createdVolumes = new Map<string, VolumeSummary>()

/** 本次会话里新建的网络。 */
const createdNetworks = new Map<string, NetworkDetail>()

/** 本次会话里被**删除**的夹具卷名。 */
const removedVolumes = new Set<string>()

/** 本次会话里被**删除**的夹具网络名。 */
const removedNetworks = new Set<string>()

/**
 * 本次会话里每个网络的容器端点（网络 ID → 端点列表）。
 *
 * 夹具是模块级常量，**刻意不原地改**（与镜像那边的覆盖层同一个理由）：
 * 原地改会让「跑一次走查后夹具变了」，下一轮看到的数据与初始态不一致。
 */
const networkContainers = new Map<string, NetworkDetail['containers']>()

/** Docker 内置网络（不可删除）。与 `api/network.ts` 的常量同源。 */
const BUILT_IN = new Set(['bridge', 'host', 'none', 'null'])

/** 批量删除条目数上限（与后端 `BatchRequest.MaxBatchSize` 一致）。 */
const MAX_BATCH = 100

/** 清空会话内的改动（供测试隔离用）。 */
export function resetSessionResources(): void {
  createdVolumes.clear()
  createdNetworks.clear()
  removedVolumes.clear()
  removedNetworks.clear()
  networkContainers.clear()
}

/**
 * 数据卷与网络端点 mock（0.2.0）。
 *
 * ## 复刻了后端的哪些「容易被忽略的语义」
 * 1. **`size` / `refCount` 为 `null` 才是「不知道」**——守护进程不报用量时后端给 `null`，
 *    而真正的 `0`（空卷 / 没人引用）应如实显示。两者混为一谈会把真实结论谎报成缺失。
 * 2. **`warnings` 恒为数组**：少了它前端模板直接报错。
 * 3. **列表分页**：`pageSize` 默认 20、夹紧到 [1, 100]，`pageIndex` 负数按 0；
 *    回显的 `pageIndex` / `pageSize` 是夹紧后的值。
 * 4. **同名创建卷不报错**（返回已存在的那一个），但**同名创建网络报 1404/409**。
 *    这两条方向相反，是 Docker 本身的差异，照抄任一边都会让另一条测不到。
 * 5. **卷的 `force` 删不掉被引用的卷**：`refCount > 0` 时带不带 force 都 1404；
 *    只有「卷不存在」时 force 才让它变成成功（幂等）。
 * 6. **网络没有 force**：有容器接入 → 1404；内置网络 → 1404（消息点名是哪个）。
 * 7. **网段格式非法 → 1000**（缺前缀长度、前缀越界、地址不合法）。
 * 8. **列表里的网络没有 `containerCount` 字段**：Docker 的列表接口不返回 `Containers`。
 */
export function resourceHandlers(scenario: () => MockScenario) {
  async function readBody(request: Request): Promise<Record<string, unknown>> {
    try {
      return (await request.json()) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  /**
   * 与后端一致的分页口径（`ContainerService` / `VolumeService` / `NetworkService` 同参数）。
   *
   * ⚠️ 必须在 mock 里真的切片：只把 `total` 填上、`items` 仍返回全量的话，
   * 前端「只渲染当前页」的分支永远测不到。
   */
  function paginate<T>(items: T[], body: Record<string, unknown>) {
    const rawIndex = Number(body.pageIndex ?? 0)
    const rawSize = Number(body.pageSize ?? 20)
    const pageIndex = Math.max(Number.isFinite(rawIndex) ? Math.trunc(rawIndex) : 0, 0)
    const pageSize = Math.min(
      Math.max(Number.isFinite(rawSize) ? Math.trunc(rawSize) : 20, 1),
      100
    )

    return {
      items: items.slice(pageIndex * pageSize, pageIndex * pageSize + pageSize),
      total: items.length,
      pageIndex,
      pageSize
    }
  }

  /** 当前场景下的数据卷（夹具 + 会话内新建 − 会话内删除）。 */
  function volumesFor(current: MockScenario): VolumeSummary[] {
    if (current === 'empty') return []

    return [
      ...MOCK_VOLUMES_DEFAULT.filter((volume) => !removedVolumes.has(volume.name)),
      ...[...createdVolumes.values()]
    ]
  }

  function volumeDetailFor(current: MockScenario, name: string): VolumeDetail | undefined {
    if (current === 'empty') return undefined

    const created = createdVolumes.get(name)
    if (created) {
      return {
        name: created.name,
        driver: created.driver,
        mountpoint: created.mountpoint,
        scope: created.scope,
        created: created.created,
        options: { type: 'none', o: 'bind' },
        labels: created.labels
      }
    }

    if (removedVolumes.has(name)) return undefined

    return MOCK_VOLUME_DETAILS.find((volume) => volume.name === name)
  }

  /** 当前场景下的网络（夹具 + 会话内新建 − 会话内删除）。 */
  function networksFor(current: MockScenario): NetworkSummary[] {
    if (current === 'empty') return []

    return [
      ...MOCK_NETWORKS_DEFAULT.filter((network) => !removedNetworks.has(network.name)),
      ...[...createdNetworks.values()].map(toSummary)
    ]
  }

  /** 按 ID 或名称取网络详情（守护进程两者都接受）。 */
  function networkDetailFor(current: MockScenario, id: string): NetworkDetail | undefined {
    if (current === 'empty') return undefined

    const created = [...createdNetworks.values()].find(
      (network) => network.id === id || network.name === id
    )
    if (created) return created

    if (removedNetworks.has(id)) return undefined

    return MOCK_NETWORK_DETAILS.find((network) => network.id === id || network.name === id)
  }

  /**
   * 某网络当前的容器端点（夹具 + 会话内接入 / 断开）。
   *
   * 首次访问时用夹具播种，之后一直返回**同一个数组**——
   * 接入 / 断开直接改它，inspect 与删除判定读到的就是最新状态。
   */
  function containersOf(detail: NetworkDetail): NetworkDetail['containers'] {
    const existing = networkContainers.get(detail.id)
    if (existing) return existing

    const seeded: NetworkDetail['containers'] = [...detail.containers]
    networkContainers.set(detail.id, seeded)

    return seeded
  }

  return [
    /* ==================== 数据卷 ==================== */

    http.post('*/api/v1/hosts/:hostId/volumes/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)

      return HttpResponse.json(
        ok({
          ...paginate(volumesFor(scenario()), body),
          // 恒为数组：守护进程没给告警时也要有这个键，否则模板遍历 undefined 会崩
          warnings: []
        })
      )
    }),

    http.post('*/api/v1/hosts/:hostId/volumes/inspect', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const detail = name ? volumeDetailFor(scenario(), name) : undefined

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      return HttpResponse.json(ok(detail))
    }),

    http.post('*/api/v1/hosts/:hostId/volumes/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()

      if (!isValidName(name)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, `数据卷名「${name}」不合法`), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // ⚠️ 同名创建**不报错**，返回已存在的那一个（Docker 的既定行为）。
      // 网络那边相反——别把这条的逻辑套过去。
      const existing = volumesFor(scenario()).find((volume) => volume.name === name)

      if (existing) {
        return HttpResponse.json(ok(existing))
      }

      const created: VolumeSummary = {
        name,
        driver: String(body.driver ?? '').trim() || 'local',
        mountpoint: `/var/lib/docker/volumes/${name}/_data`,
        scope: 'local',
        created: new Date().toISOString(),
        // 新建的卷还没有数据、也没有容器挂着——真实守护进程的 df 会给 0/0
        size: 0,
        refCount: 0,
        labels: (body.labels as Record<string, string>) ?? {}
      }

      createdVolumes.set(name, created)

      return HttpResponse.json(ok(created))
    }),

    http.post('*/api/v1/hosts/:hostId/volumes/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const force = body.force === true

      if (!isValidName(name)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, `卷名「${name}」不合法`), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const existing = volumesFor(scenario()).find((volume) => volume.name === name)

      if (!existing) {
        // force 的**唯一**作用：让「不存在」变成成功（幂等）
        if (force) return HttpResponse.json(ok(null))

        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // ⚠️ 被引用时**带 force 也删不掉**（实测如此）：必须先删掉使用它的容器。
      if ((existing.refCount ?? 0) > 0) {
        return HttpResponse.json(
          fail(
            ErrorCode.DockerResourceConflict,
            `数据卷 ${name} 仍被 ${existing.refCount ?? 0} 个容器引用，先删除它们`
          ),
          { status: httpStatusOf(ErrorCode.DockerResourceConflict) }
        )
      }

      removedVolumes.add(name)
      createdVolumes.delete(name)

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/hosts/:hostId/volumes/batch-remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const raw = Array.isArray(body.names) ? (body.names as unknown[]) : []
      const force = body.force === true

      if (raw.length === 0 || raw.length > MAX_BATCH) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'names 不能为空'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 去重 + 去空白（后端 NormalizeIds 的行为）：`requested` 必须是**去重后**的个数
      const names = [...new Set(raw.map((item) => String(item ?? '').trim()).filter(Boolean))]

      if (names.length === 0) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'names 不能全为空值'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      const failures: BatchOperationResult['failures'] = []
      let succeeded = 0

      for (const name of names) {
        const existing = volumesFor(scenario()).find((volume) => volume.name === name)

        if (!existing) {
          if (force) {
            succeeded += 1
          } else {
            failures.push({
              resourceId: name,
              code: ErrorCode.DockerResourceNotFound,
              message: `数据卷 ${name}不存在`
            })
          }

          continue
        }

        if ((existing.refCount ?? 0) > 0) {
          failures.push({
            resourceId: name,
            code: ErrorCode.DockerResourceConflict,
            message: `数据卷 ${name} 仍被 ${existing.refCount ?? 0} 个容器引用`
          })

          continue
        }

        removedVolumes.add(name)
        createdVolumes.delete(name)
        succeeded += 1
      }

      // **部分成功也返回 200**：失败原因逐条在 failures 里
      return HttpResponse.json(ok({ requested: names.length, succeeded, failures }))
    }),

    http.post('*/api/v1/hosts/:hostId/volumes/prune', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const label = String(body.label ?? '').trim()

      if (label.length > MAX_LABEL_LENGTH) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, `label 最长 ${MAX_LABEL_LENGTH} 个字符`),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      if (hasControlCharacter(label)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, 'label 不能包含控制字符'), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 候选口径与守护进程一致：**未被引用** + **本地驱动**（nfs 那些不是本地卷，回收不了空间）
      const candidates = volumesFor(scenario()).filter((volume) => {
        if ((volume.refCount ?? 0) !== 0) return false
        if (volume.driver !== 'local') return false
        if (label.length > 0 && !matchesLabel(volume.labels, label)) return false

        return true
      })

      for (const volume of candidates) {
        removedVolumes.add(volume.name)
        createdVolumes.delete(volume.name)
      }

      return HttpResponse.json(
        ok({
          deleted: candidates.map((volume) => volume.name),
          reclaimedBytes: candidates.reduce((sum, volume) => sum + (volume.size ?? 0), 0)
        })
      )
    }),

    /* ==================== 网络 ==================== */

    http.post('*/api/v1/hosts/:hostId/networks/list', async ({ request }) => {
      await delay(delayFor(scenario()))

      if (scenario() === 'error') {
        return HttpResponse.json(fail(ErrorCode.InternalError), {
          status: httpStatusOf(ErrorCode.InternalError)
        })
      }

      const body = await readBody(request)

      // ⚠️ items 里**没有** containerCount：Docker 的列表接口不返回 Containers，
      // 后端不会补一个恒为 0 的字段。mock 同样不给。
      return HttpResponse.json(ok(paginate(networksFor(scenario()), body)))
    }),

    http.post('*/api/v1/hosts/:hostId/networks/inspect', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const id = String(body.networkId ?? '').trim()
      const detail = id ? networkDetailFor(scenario(), id) : undefined

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      // 容器端点取**会话内**的状态：接入 / 断开之后详情必须立刻反映出来
      return HttpResponse.json(ok({ ...detail, containers: containersOf(detail) }))
    }),

    http.post('*/api/v1/hosts/:hostId/networks/connect', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const containerId = String(body.containerId ?? '').trim()
      const networkId = String(body.networkId ?? '').trim()

      if (containerId.length === 0 || networkId.length === 0) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, 'containerId 与 networkId 都不能为空'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const detail = networkDetailFor(scenario(), networkId)

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      const endpoints = containersOf(detail)
      const resolvedId = resolveContainerId(containerId)

      // ⚠️ **幂等**：已经在网上了直接成功（后端先读实际状态再决定要不要调守护进程，
      // 不同守护进程版本对「重复接入」的行为并不一致）
      if (!endpoints.some((endpoint) => endpoint.id === resolvedId)) {
        endpoints.push(toEndpoint(resolvedId))
      }

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/hosts/:hostId/networks/disconnect', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const containerId = String(body.containerId ?? '').trim()
      const networkId = String(body.networkId ?? '').trim()

      if (containerId.length === 0 || networkId.length === 0) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, 'containerId 与 networkId 都不能为空'),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      const detail = networkDetailFor(scenario(), networkId)

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      const endpoints = containersOf(detail)
      const resolvedId = resolveContainerId(containerId)
      // 同时按 ID 与**名字**匹配：夹具里的端点可能用的是容器名之外的标识，
      // 而用户手里常常只有名字。
      const index = endpoints.findIndex(
        (endpoint) => endpoint.id === resolvedId || endpoint.name === containerId
      )

      // ⚠️ **幂等**：本来就不在网中也算成功（守护进程会回 404，后端自己先读状态绕开）
      if (index >= 0) {
        endpoints.splice(index, 1)
      }

      return HttpResponse.json(ok(null))
    }),

    http.post('*/api/v1/hosts/:hostId/networks/create', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const name = String(body.name ?? '').trim()
      const subnet = String(body.subnet ?? '').trim()

      if (!isValidName(name)) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed, `网络名「${name}」不合法`), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 网段格式校验在**后端**、返回 1000：格式错误若落进 1402/500，
      // 「自己填的网段打错一个字符」会显示成服务端故障。
      if (subnet.length > 0 && !isValidCidr(subnet)) {
        return HttpResponse.json(
          fail(ErrorCode.ValidationFailed, `subnet「${subnet}」不是合法的 CIDR 网段`),
          { status: httpStatusOf(ErrorCode.ValidationFailed) }
        )
      }

      // ⚠️ 同名网络报 1404/409（与卷相反）：守护进程对「同名」回 403。
      if (networksFor(scenario()).some((network) => network.name === name)) {
        return HttpResponse.json(
          fail(ErrorCode.DockerResourceConflict, `无法创建网络「${name}」：网络名称已存在`),
          { status: httpStatusOf(ErrorCode.DockerResourceConflict) }
        )
      }

      const detail: NetworkDetail = {
        id: hexId(name),
        name,
        driver: String(body.driver ?? '').trim() || 'bridge',
        scope: 'local',
        attachable: body.attachable === true,
        internal: false,
        enableIPv4: true,
        // IPv6 网段时后端自动打开 IPv6——否则会建出「有地址池却不通」的网
        enableIPv6: isIpv6(subnet),
        ipam: subnet.length > 0 ? [{ subnet, gateway: firstHost(subnet), ipRange: null }] : [],
        containers: [],
        options: {},
        labels: (body.labels as Record<string, string>) ?? {}
      }

      createdNetworks.set(name, detail)

      return HttpResponse.json(ok(toSummary(detail)))
    }),

    http.post('*/api/v1/hosts/:hostId/networks/remove', async ({ request }) => {
      await delay(delayFor(scenario()))

      const body = await readBody(request)
      const id = String(body.networkId ?? '').trim()
      const detail = id ? networkDetailFor(scenario(), id) : undefined

      if (!detail) {
        return HttpResponse.json(fail(ErrorCode.DockerResourceNotFound), {
          status: httpStatusOf(ErrorCode.DockerResourceNotFound)
        })
      }

      if (BUILT_IN.has(detail.name)) {
        return HttpResponse.json(
          fail(
            ErrorCode.DockerResourceConflict,
            `内置网络「${detail.name}」不可删除（它被主机上的容器默认依赖）`
          ),
          { status: httpStatusOf(ErrorCode.DockerResourceConflict) }
        )
      }

      // 没有 force：Docker 删除网络不提供该参数，有容器接入必然失败
      const attached = containersOf(detail)
      if (attached.length > 0) {
        return HttpResponse.json(
          fail(
            ErrorCode.DockerResourceConflict,
            `网络「${detail.name}」上还有 ${attached.length} 个容器，先断开或删除它们再删除网络`
          ),
          { status: httpStatusOf(ErrorCode.DockerResourceConflict) }
        )
      }

      removedNetworks.add(detail.name)
      createdNetworks.delete(detail.name)

      return HttpResponse.json(ok(null))
    })
  ]
}

/** 标签过滤表达式的最大长度（与后端 `PruneFilter.MaxLabelLength` 一致）。 */
const MAX_LABEL_LENGTH = 256

/**
 * 标签表达式匹配（`key` 或 `key=value`）。
 *
 * ⚠️ 语义**原样照搬守护进程**：只写键表示「带这个键即可」，
 * 写 `键=值` 表示「键与值都相等」。mock 不重新解释一遍——
 * 两套规则一旦漂移，症状是「我明明选了它却没被清理」。
 */
function matchesLabel(labels: Record<string, string>, expression: string): boolean {
  const index = expression.indexOf('=')

  if (index < 0) {
    return Object.prototype.hasOwnProperty.call(labels, expression)
  }

  return labels[expression.slice(0, index)] === expression.slice(index + 1)
}

/**
 * 把容器标识（ID 或**名字**）解析成容器 ID。
 *
 * ⚠️ **必须先解析再做幂等判定**：端点上记的是**容器 ID**，
 * 而用户（与夹具）手里常常是容器名。拿名字直接比 id 会比不相等，
 * 于是「重复接入」被当成两次接入——幂等就失效了。
 */
function resolveContainerId(containerId: string): string {
  const container = MOCK_CONTAINERS_DEFAULT.find(
    (item) => item.id === containerId || item.name === containerId
  )

  return container?.id ?? containerId
}

/** 把容器标识变成网络端点（接入网络时用）。 */
function toEndpoint(containerId: string): NetworkDetail['containers'][number] {
  const container = MOCK_CONTAINERS_DEFAULT.find(
    (item) => item.id === containerId || item.name === containerId
  )

  return {
    id: container?.id ?? containerId,
    name: container?.name ?? containerId,
    macAddress: '02:42:ac:14:00:99',
    ipv4Address: '172.20.0.99/16',
    ipv6Address: null
  }
}

/**
 * 标签表达式里是否含控制字符。
 *
 * ⚠️ 刻意**不用正则**：控制字符类在正则里会被 ESLint 的 `no-control-regex` 拦下，
 * 而这里要的就是「逐字符判定」，用 `charCodeAt` 更直白。
 */
function hasControlCharacter(value: string): boolean {
  return Array.from(value).some((character) => {
    const code = character.charCodeAt(0)
    return code < 32 || code === 127
  })
}

/** 网络详情 → 列表项（列表没有容器数与 IPAM 之外的额外信息）。 */
function toSummary(detail: NetworkDetail): NetworkSummary {
  return {
    id: detail.id,
    name: detail.name,
    driver: detail.driver,
    scope: detail.scope,
    attachable: detail.attachable,
    internal: detail.internal,
    subnets: detail.ipam.map((entry) => entry.subnet ?? '').filter(Boolean),
    gateways: detail.ipam.map((entry) => entry.gateway ?? '').filter(Boolean),
    labels: detail.labels
  }
}

/** 与后端 `DockerResourceName` 同一条正则。 */
function isValidName(name: string): boolean {
  return /^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(name)
}

/**
 * 与后端 `NormalizeIpParameter` 同口径：必须是带前缀长度的 CIDR。
 *
 * ⚠️ **IPv6 必须单独走一条分支**：若沿用「拆四段十进制」的 IPv4 逻辑，
 * `fd00::/64` 会被算成「只有一段」而误拒——那正好是 0.2.0 特意支持的一种输入。
 */
function isValidCidr(subnet: string): boolean {
  const slash = subnet.indexOf('/')
  if (slash < 0) return false

  const address = subnet.slice(0, slash)
  const prefix = subnet.slice(slash + 1)

  if (!/^\d+$/.test(prefix)) return false

  // 前缀长度上限随地址族变化：IPv4 是 32，IPv6 是 128
  const isIpv6 = address.includes(':')
  const value = Number(prefix)
  if (value < 0 || value > (isIpv6 ? 128 : 32)) return false

  if (isIpv6) return /^[0-9a-fA-F:]+$/.test(address)

  const octets = address.split('.')

  // ⚠️ 八位组范围必须与后端的 IPAddress.TryParse 一致（拒绝 999.1.1.1）
  return (
    octets.length === 4 && octets.every((octet) => /^\d{1,3}$/.test(octet) && Number(octet) <= 255)
  )
}

function isIpv6(subnet: string): boolean {
  return subnet.includes(':')
}

/** 由种子生成稳定的 64 位十六进制 ID（网络 ID **不带** `sha256:` 前缀）。 */
function hexId(seed: string): string {
  let hash = 0x811c9dc5

  for (let index = 0; index < seed.length; index += 1) {
    hash ^= seed.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193) >>> 0
  }

  return hash.toString(16).padStart(8, '0').repeat(8)
}

/** 取网段的第一个可用地址作为网关（与 Docker 的默认行为一致）。 */
function firstHost(subnet: string): string {
  const address = subnet.split('/')[0]
  const parts = address.split('.')

  if (parts.length !== 4) return address

  parts[3] = '1'

  return parts.join('.')
}
