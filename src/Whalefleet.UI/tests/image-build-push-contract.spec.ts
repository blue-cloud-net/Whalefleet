/**
 * @vitest-environment node
 *
 * 与 `mock-contract.spec.ts` 同理由使用 **node 环境**：
 * jsdom 自带的 `AbortSignal` 与 Node 原生 `fetch` 不是同一实例，
 * MSW 的 fetch 拦截器会因此抛
 * `RequestInit: Expected signal ("AbortSignal {}") to be an instance of AbortSignal`。
 */
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import { mockServer } from '../src/mocks/node'
import { setMockScenario } from '../src/mocks/index'
import { ErrorCode } from '../src/mocks/envelope'
import { resetSessionRegistries } from '../src/mocks/handlers/registries'
import { resetSessionImages } from '../src/mocks/handlers/images'

/**
 * 镜像构建 / 推送端点的 mock 契约守卫（0.4.0）。
 *
 * ## 守的是什么
 * 这几条语义不跑起来是看不出来的：
 * 1. **构建失败必须是 `1806` 而不是 `1402`**：后者会让用户以为后端挂了，
 *    而真正该改的是 Dockerfile。守护进程在两种不同阶段失败（请求阶段抛异常 /
 *    进度流里带错误），后端要把它们收敛到同一个码。
 * 2. **构建成功要把镜像放进列表**：否则界面表现成「构建成功但列表里没有」。
 * 3. **构建日志必须回带**：失败时守护进程只给一行 `non-zero code: 1`，
 *    原因在上面几百行里。
 * 4. **推送返回 `authenticated`**：匿名推送也是成功，界面必须能区分
 *    「用了我的凭据」与「这个仓库根本不要凭据」——后者往往意味着地址配错了。
 * 5. **推送的两种失败是两个码**：不可达 `1805` / 凭据被拒 `1804`。
 */
const BASE = 'http://localhost:5173'
const HOST = `${BASE}/api/v1/hosts/prod-01`

beforeAll(() => mockServer.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  mockServer.resetHandlers()
  setMockScenario('default')
  // handler 里的会话态（拉取 / 构建出来的镜像、新增的配置）不会被 resetHandlers 还原
  resetSessionRegistries()
  resetSessionImages()
})
afterAll(() => mockServer.close())

interface Envelope {
  success: boolean
  code: number
  message: string
  data: unknown
}

async function post(path: string, body?: unknown) {
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  })

  return { status: res.status, json: (await res.json()) as Envelope }
}

/**
 * 发一次 multipart 构建请求。
 *
 * ⚠️ 这里**无法**复刻「文件段缺 filename」那种坏请求：Node 的 `FormData.append(name, blob)`
 * 总会替 Blob 补一个默认文件名（`blob`），而浏览器与 `curl -F` 也都会带。
 * 那条语义的守卫因此只能靠 `api/image.ts` 里 `form.append(name, file, file.name)`
 * 的写法与本文件的注释——改代码时别把第三个参数删掉。
 */
async function build(fields: Record<string, string>) {
  const form = new FormData()

  form.append('context', new Blob([new Uint8Array(1024)]), 'context.tar')
  for (const [key, value] of Object.entries(fields)) form.append(key, value)

  const res = await fetch(`${HOST}/images/build`, { method: 'POST', body: form })

  return { status: res.status, json: (await res.json()) as Envelope }
}

describe('镜像构建端点（0.4.0，multipart）', () => {
  it('tag 为空 → 1000 / 400', async () => {
    const { status, json } = await build({ tag: '  ' })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('成功时返回镜像 ID / 引用 / 日志，且新镜像立刻出现在列表里', async () => {
    const { status, json } = await build({ tag: 'demo:v1' })

    expect(status).toBe(200)

    const result = json.data as {
      imageId: string
      reference: string
      buildLog: string[]
      repoTags: string[]
    }

    // 后端要求 Tags 是**完整引用**：只给仓库名会打出 <none> 标签，
    // 于是「构建成功」但按 仓库:标签 查不到
    expect(result.reference).toBe('demo:v1')
    expect(result.repoTags).toContain('demo:v1')
    // 日志必须回带：失败时守护进程只给一行 non-zero code，原因在上面几百行里
    expect(result.buildLog.length).toBeGreaterThan(0)

    const list = await post(`${HOST}/images/list`, { search: 'demo:v1', pageSize: 100 })
    expect(
      (list.json.data as { items: { repoTags: string[] }[] }).items.some((item) =>
        item.repoTags.includes('demo:v1')
      )
    ).toBe(true)
  })

  it('未带标签的 tag 补 latest（与 Docker 一致）', async () => {
    const { json } = await build({ tag: 'demo-no-tag' })

    expect((json.data as { reference: string }).reference).toBe('demo-no-tag:latest')
  })

  it('构建失败 → 1806 / 500（不是 1402：该改的是 Dockerfile，不是后端）', async () => {
    const { status, json } = await build({ tag: 'broken:v1' })

    expect(status).toBe(500)
    expect(json.code).toBe(ErrorCode.ImageBuildFailed)
    expect(json.message).toContain('COPY failed')
  })
})

describe('镜像推送端点（0.4.0）', () => {
  it('引用没有标签 → 1000 / 400（推送必须是 仓库:标签 形态）', async () => {
    const { status, json } = await post(`${HOST}/images/push`, { image: 'harbor.local:5000/app' })

    expect(status).toBe(400)
    expect(json.code).toBe(ErrorCode.ValidationFailed)
  })

  it('镜像不存在 → 1401 / 404', async () => {
    const { status, json } = await post(`${HOST}/images/push`, {
      image: 'harbor.local:5000/nope:v1'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.DockerResourceNotFound)
  })

  it('指定的配置不存在 → 1800 / 404', async () => {
    const { status, json } = await post(`${HOST}/images/push`, {
      image: 'nginx:latest',
      registryId: '00000000-0000-0000-0000-0000000000ff'
    })

    expect(status).toBe(404)
    expect(json.code).toBe(ErrorCode.RegistryNotFound)
  })

  it('按地址自动匹配到匿名配置 → 成功但 authenticated=false', async () => {
    const pushed = await post(`${HOST}/images/push`, {
      image: 'registry.example.com/nginx:latest'
    })

    // `registry.example.com/nginx` 不在夹具镜像里，先造一个
    if (pushed.status === 404) {
      await build({ tag: 'registry.example.com/nginx:latest' })
    }

    const { status, json } = await post(`${HOST}/images/push`, {
      image: 'registry.example.com/nginx:latest'
    })

    expect(status).toBe(200)
    expect(json.data).toMatchObject({
      registryServerAddress: 'registry.example.com',
      authenticated: false
    })
  })

  it('自动匹配到带凭据的配置 → authenticated=true', async () => {
    await build({ tag: 'harbor.local:5000/nginx:latest' })

    const { status, json } = await post(`${HOST}/images/push`, {
      image: 'harbor.local:5000/nginx:latest'
    })

    expect(status).toBe(200)
    // 这两个分支必须能区分：anonymous 推送成功往往意味着**地址配错了**
    // （推到了一个公开仓库），而「已认证」才说明凭据生效
    expect(json.data).toMatchObject({ authenticated: true })
    expect((json.data as { pushLog: string[] }).pushLog.length).toBeGreaterThan(0)
  })

  it('地址不可达 → 1805 / 502，凭据被拒 → 1804 / 400', async () => {
    const unreachableRef = 'unreachable.local:5000/app:v1'
    await build({ tag: unreachableRef })

    const unreachable = await post(`${HOST}/images/push`, { image: unreachableRef })
    expect(unreachable.status).toBe(502)
    expect(unreachable.json.code).toBe(ErrorCode.RegistryUnreachable)

    const deniedRef = 'denied.local:5000/app:v1'
    await build({ tag: deniedRef })

    const denied = await post(`${HOST}/images/push`, { image: deniedRef })
    expect(denied.status).toBe(400)
    expect(denied.json.code).toBe(ErrorCode.RegistryAuthFailed)
  })
})
