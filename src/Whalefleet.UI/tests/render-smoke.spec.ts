import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import ElementPlus from 'element-plus'
import { i18n } from '../src/locales'
import { useAuthStore } from '../src/stores/auth'
import type { CurrentUser } from '../src/types/api'

/**
 * 渲染冒烟（0.4.0 新增 / 改动组件）。
 *
 * ## 为什么必须有这个文件
 * `typecheck` 与 `lint` **抓不到**一类错误：模板里引用了一个 `<script setup>`
 * 根本没导出的函数。Vue 3 此时只在 console 里告警
 * （`Property "xxx" was accessed during render but is not defined on instance`），
 * 那一段内容**渲染成空白**，页面其余部分照常显示——没有任何异常抛出，
 * 退出码也是 0。AGENTS.md 陷阱 14 记的就是这个：症状是「整块内容空白且
 * v-loading 卡住」，几乎不可能靠读日志定位。
 *
 * 所以守卫的手段是**真的 mount 一次并检查 console 告警**，而不是静态分析。
 *
 * ## 为什么用**真实** i18n 而不是把 `t()` 桩成返回 key
 * 桩掉 `t` 就查不出「key 不存在」——而 vue-i18n 对缺失 key 也**只告警不报错**，
 * 界面上直接显示一串 key 路径（AGENTS.md 前端约定里明确禁止拼 key，同理也不能
 * 拼错）。用真实词条库 + 收集告警，才能把这两类静默失败一起拦住。
 */

// ===== API 层桩：渲染守卫不该发网络请求 =====
vi.mock('../src/api/repository', () => ({
  listRepositories: vi.fn(async () => []),
  addRepository: vi.fn(),
  removeRepository: vi.fn(async () => null),
  testRepository: vi.fn(async () => ({ reachable: true, authenticated: true, message: 'ok' }))
}))

vi.mock('../src/api/image', () => ({
  listImages: vi.fn(async () => ({ items: [], total: 0, pageIndex: 0, pageSize: 20 })),
  batchRemoveImages: vi.fn(async () => ({ requested: 0, succeeded: 0, failures: [] })),
  removeImage: vi.fn(async () => ({ untagged: [], deleted: [] })),
  pullImage: vi.fn(),
  buildImage: vi.fn(),
  pushImage: vi.fn()
}))

vi.mock('../src/api/auth', () => ({
  login: vi.fn(),
  logoutSession: vi.fn(),
  refreshSession: vi.fn()
}))

// ===== 0.5.0：容器日志端点与实时通道 =====
// 日志页会真的去拉日志并建立 SignalR 连接；渲染守卫不该发网络请求，
// 也不该在 jsdom 里连 WebSocket。
vi.mock('../src/api/container', () => ({
  getContainerLogs: vi.fn(async () => ({
    containerId: 'smoke-container',
    lines: [
      { seq: 1, timestamp: '2026-10-20T10:00:00Z', line: 'hello from smoke' },
      { seq: 2, timestamp: '2026-10-20T10:00:01Z', line: 'second line' }
    ],
    truncated: false
  })),
  downloadContainerLogs: vi.fn(async () => ({ blob: new Blob(['x']), fileName: 'smoke.log' })),
  // 容器列表页（0.7.0 改造过模板）用得到这几个
  listContainers: vi.fn(async () => ({ items: [], total: 0, pageIndex: 0, pageSize: 20 })),
  getContainerHealth: vi.fn(async () => ({ items: [] })),
  startContainer: vi.fn(),
  stopContainer: vi.fn(),
  restartContainer: vi.fn(),
  removeContainer: vi.fn(),
  killContainer: vi.fn(),
  pauseContainer: vi.fn(),
  resumeContainer: vi.fn(),
  recreateContainer: vi.fn()
}))

vi.mock('../src/realtime/connection', () => ({
  createHubConnection: vi.fn(() => ({
    on: vi.fn(),
    onreconnected: vi.fn(),
    start: vi.fn(async () => undefined),
    stop: vi.fn(async () => undefined),
    invoke: vi.fn(async () => ({ success: true, code: null, message: null }))
  })),
  hubUrl: vi.fn((path: string) => `/hubs/${path}`),
  hostHubPath: vi.fn((hostId: string, kind: string) => `hosts/${hostId}/${kind}`)
}))

// ===== element-plus 的消息组件是副作用调用，桩掉以免污染测试输出 =====
vi.mock('element-plus', async (importOriginal) => {
  const actual = await importOriginal<typeof import('element-plus')>()

  return {
    ...actual,
    ElMessage: Object.assign(vi.fn(), {
      success: vi.fn(),
      error: vi.fn(),
      warning: vi.fn(),
      info: vi.fn()
    }),
    ElMessageBox: Object.assign(vi.fn(), { confirm: vi.fn() })
  }
})

/** 收集渲染期的 console 告警（Vue 的「未定义属性」只走这里）。 */
let warnings: string[] = []
let warnSpy: ReturnType<typeof vi.spyOn>
let errorSpy: ReturnType<typeof vi.spyOn>

/**
 * 把 console 参数安全地转成文本。
 *
 * ⚠️ **不能直接 `String(arg)`**：Vue 有时会把「组件实例 / 代理对象」当参数传出来，
 * 而代理对象的 `toString` 可能不可调用——`String()` 于是抛
 * `Cannot convert object to primitive value`，**守卫测试自己先炸掉**，
 * 结果是「看不见真正要报的告警」。这里逐个 try，失败时退回 `Object.prototype.toString`。
 */
function safeText(value: unknown): string {
  if (typeof value === 'string') return value
  if (value instanceof Error) return `${value.name}: ${value.message}`
  try {
    return String(value)
  } catch {
    return Object.prototype.toString.call(value)
  }
}

beforeEach(() => {
  warnings = []
  // 固定语言：断言的是**渲染出来的文案**而不是 key（真实 i18n 会把 key 翻成文字），
  // 语言的确定性与断言的确定性必须绑定。
  i18n.global.locale.value = 'zh-CN'
  warnSpy = vi.spyOn(console, 'warn').mockImplementation((...args) => {
    warnings.push(args.map(safeText).join(' '))
    if (process.env.DUMP_WARNINGS) console.info('[WARN]', ...args.map(safeText))
  })
  errorSpy = vi.spyOn(console, 'error').mockImplementation((...args) => {
    warnings.push(args.map(safeText).join(' '))
    if (process.env.DUMP_WARNINGS) console.info('[ERROR]', ...args.map(safeText))
  })
})

afterEach(() => {
  warnSpy.mockRestore()
  errorSpy.mockRestore()
  vi.clearAllMocks()
})

/** 与「模板引用了不存在的标识符」有关的告警模式。 */
const TEMPLATE_BUG_PATTERNS = [
  /was accessed during render but is not defined on instance/,
  /is not a function/,
  /Failed to resolve component/,
  /not found in the following locale resource/,
  /Cannot read propert(y|ies) of undefined/,
  /Cannot read propert(y|ies) of null/
]

/** 把命中的渲染期缺陷原样返回，好让断言消息里能看到全文。 */
function templateBugs(): string[] {
  return warnings.filter((line) => TEMPLATE_BUG_PATTERNS.some((p) => p.test(line)))
}

const routes: RouteRecordRaw[] = [
  { path: '/', name: 'overview', component: { template: '<div />' } },
  { path: '/repositories', name: 'repositories', component: { template: '<div />' } },
  {
    path: '/hosts/:hostId/images',
    name: 'host-images',
    component: { template: '<div />' }
  },
  {
    path: '/hosts/:hostId/images/:id/inspect',
    name: 'host-image-inspect',
    component: { template: '<div />' }
  },
  {
    path: '/hosts/:hostId/containers',
    name: 'host-containers',
    component: { template: '<div />' }
  }
]

/**
 * 造一个「什么都能写」的鉴权 store。
 *
 * ⚠️ 走 `setSession` 而不是直接改 state：`CurrentUser` 的字段必须一次给齐，
 * 少一个字段会让权限判定拿到 `undefined`，测试就成了在测一个不存在的用户形态。
 */
function seedAuth(permissions: string[]): void {
  const user: CurrentUser = {
    id: '00000000-0000-0000-0000-000000000001',
    username: 'smoke',
    displayName: null,
    isDefault: false,
    permissions,
    lastLoginAt: null
  }

  useAuthStore().setSession({
    accessToken: 'access-token',
    refreshToken: 'refresh-token',
    user
  } as never)
}

interface MountOptions {
  permissions?: string[]
  props?: Record<string, unknown>
  /** 初始路由；主机作用域的视图靠它拿到 `route.params.hostId` */
  path?: string
}

/**
 * 挂载并等路由就绪。
 *
 * ⚠️ **`ElementPlus` 必须作为插件注册**：否则 `el-table` / `el-dialog` 全部解析失败，
 * Vue 会把它们当**原生自定义元素**直接渲染子树 —— 于是 `el-table-column` 的
 * `#default="{ row }"` 拿到 `undefined` 而抛
 * `Cannot destructure property 'row' of undefined`。那个错看起来像业务代码坏了，
 * 实际是测试环境没装插件，与被测组件无关。
 */
async function mountSmoke(component: unknown, options: MountOptions = {}): Promise<VueWrapper> {
  const pinia = createPinia()

  setActivePinia(pinia)
  seedAuth(options.permissions ?? ['repository.read', 'repository.write', 'image.write'])

  const router = createRouter({ history: createWebHistory(), routes })

  // ⚠️ 必须**先 push 再 isReady**：`isReady()` 等的是「首次导航完成」，
  // 而首次导航是由 app.use(router) 触发的——此时 app 还不存在，于是永远不 resolve，
  // 测试直接超时。先 push 一次就把导航启动了。
  await router.push(options.path ?? '/')
  await router.isReady()

  return mount(component as never, {
    global: { plugins: [pinia, router, i18n, ElementPlus] },
    props: options.props
  })
}

describe('仓库配置页渲染冒烟（0.4.0）', () => {
  it('有权时正常渲染，且不是空白页', async () => {
    const RepositoriesView = (await import('../src/views/RepositoriesView.vue')).default
    const wrapper = await mountSmoke(RepositoriesView)

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // 空白页也是「无告警」的失败形态，所以必须单独断言内容非空
    expect(wrapper.text()).toContain('新增配置')
    wrapper.unmount()
  })

  it('只有 repository.read 时不渲染写操作（新增 / 测试按钮消失）', async () => {
    const RepositoriesView = (await import('../src/views/RepositoriesView.vue')).default
    const wrapper = await mountSmoke(RepositoriesView, { permissions: ['repository.read'] })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // 无写权限时不该出现新增按钮与测试按钮
    expect(wrapper.text()).not.toContain('新增配置')
    expect(wrapper.text()).not.toContain('测试连接')
    wrapper.unmount()
  })
})

describe('三个对话框渲染冒烟（0.4.0）', () => {
  it('Registry 表单对话框', async () => {
    const Dialog = (await import('../src/components/business/RepositoryFormDialog.vue')).default
    const wrapper = await mountSmoke(Dialog, { props: { modelValue: true } })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    expect(wrapper.text()).toContain('新增仓库配置')
    wrapper.unmount()
  })

  it('构建对话框', async () => {
    const Dialog = (await import('../src/components/business/BuildImageDialog.vue')).default
    const wrapper = await mountSmoke(Dialog, { props: { modelValue: true, hostId: 'prod-01' } })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // 上下文选择器与「上传 tar」提示必须真的渲染出来
    expect(wrapper.text()).toContain('选择 tar 文件')
    expect(wrapper.text()).toContain('tar -cf context.tar .')
    wrapper.unmount()
  })

  it('推送对话框（有标签的镜像）', async () => {
    const Dialog = (await import('../src/components/business/PushImageDialog.vue')).default
    const wrapper = await mountSmoke(Dialog, {
      props: {
        modelValue: true,
        hostId: 'prod-01',
        image: {
          id: 'sha256:abc',
          repoTags: ['harbor.local:5000/app:v1', 'harbor.local:5000/app:latest'],
          repoDigests: [],
          created: '2026-10-01T00:00:00Z',
          size: 1,
          sharedSize: 0,
          containerCount: 0
        }
      }
    })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    expect(wrapper.text()).toContain('harbor.local:5000/app:v1')
    // 多标签必须提示「只推第一个」，否则用户以为两个都会推上去
    expect(wrapper.text()).toContain('推送只推第一个标签')
    wrapper.unmount()
  })

  it('推送对话框（悬挂镜像：不出标签，也不该崩）', async () => {
    const Dialog = (await import('../src/components/business/PushImageDialog.vue')).default
    const wrapper = await mountSmoke(Dialog, {
      props: {
        modelValue: true,
        hostId: 'prod-01',
        image: {
          id: 'sha256:deadbeef',
          repoTags: [],
          repoDigests: [],
          created: '2026-10-01T00:00:00Z',
          size: 1,
          sharedSize: 0,
          containerCount: 0
        }
      }
    })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    expect(wrapper.text()).toContain('悬挂镜像没有标签')
    wrapper.unmount()
  })

  it('推送对话框（image 为 null：少一个 ?. 就会在这里炸）', async () => {
    const Dialog = (await import('../src/components/business/PushImageDialog.vue')).default
    const wrapper = await mountSmoke(Dialog, {
      props: { modelValue: true, hostId: 'prod-01', image: null }
    })

    await wrapper.vm.$nextTick()

    // ⚠️ `stackedTagCount(image?.repoTags)` 这类可选链是这里最容易写错的地方
    expect(templateBugs(), '渲染期告警').toEqual([])
    expect(wrapper.text()).toContain('该镜像没有标签')
    wrapper.unmount()
  })
})

describe('镜像列表页渲染冒烟（0.4.0 改过模板）', () => {
  it('空列表时正常渲染，且「构建镜像」入口存在', async () => {
    const ImagesView = (await import('../src/views/ImagesView.vue')).default
    const wrapper = await mountSmoke(ImagesView, { path: '/hosts/prod-01/images' })

    await wrapper.vm.$nextTick()
    // 等一次微任务让 listImages 的 promise 落定（onMounted 里 load 了一次）
    await Promise.resolve()
    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // 这次给 ImagesView 加了按钮与对话框 import，改过的模板行必须能真的渲染
    expect(wrapper.text()).toContain('构建镜像')
    expect(wrapper.text()).toContain('拉取镜像')
    wrapper.unmount()
  })
})

describe('容器列表页渲染冒烟（0.7.0 改过模板）', () => {
  it('空列表时正常渲染，搜索框与状态筛选都在', async () => {
    const ContainersView = (await import('../src/views/ContainersView.vue')).default
    const wrapper = await mountSmoke(ContainersView, { path: '/hosts/prod-01/containers' })

    await wrapper.vm.$nextTick()
    // 等 listContainers 的 promise 落定（watch 里 immediate 调了一次 load）
    await Promise.resolve()
    await Promise.resolve()
    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // ⚠️ **必须断言表格真的渲染出来了**：模板里的 `v-model="search.keyword"` 若忘了解包，
    // 脚本里的 `search.keyword.trim()` 会抛 `undefined (reading 'trim')`，
    // 而那个错**不是渲染期告警**——它被 catch 进错误态，整页变成「重试」按钮。
    // 只查 console 告警会漏掉它，必须查「表格在不在」。
    expect(wrapper.find('.el-table').exists(), '容器表格应已渲染（没进错误态）').toBe(true)
    // 工具条上的搜索框与状态下拉必须在（模板改过最容易在这里炸）。
    // ⚠️ 搜索框的占位符**不是文本内容**（它在 placeholder 属性里），
    // 因此只能查元素而不是查文字。
    expect(wrapper.find('input[placeholder="搜索名称 / 镜像 / ID"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('全部')
    // 批量按钮**常驻**：没勾选任何行时它们也在（只是禁用）
    expect(wrapper.find('[aria-label="启动"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="停止"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="重启"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="删除"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('容器详情页渲染冒烟（0.7.0 改过模板）', async () => {
    const InspectView = (await import('../src/views/ContainerInspectView.vue')).default
    const wrapper = await mountSmoke(InspectView, {
      path: '/hosts/prod-01/containers/demo/inspect',
      props: { containerId: 'demo' }
    })

    await wrapper.vm.$nextTick()
    await Promise.resolve()
    await Promise.resolve()
    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    wrapper.unmount()
  })

  it('批量动作条渲染冒烟（0.7.0 新组件）', async () => {
    const BatchActions = (await import('../src/components/business/ContainerBatchActions.vue')).default
    const wrapper = await mountSmoke(BatchActions, {
      props: { states: ['running', 'exited'] }
    })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // 四个高频动作常驻（图标 + 文字）
    expect(wrapper.text()).toContain('启动')
    expect(wrapper.text()).toContain('停止')
    expect(wrapper.text()).toContain('重启')
    expect(wrapper.text()).toContain('删除')
    wrapper.unmount()
  })

  it('终止对话框渲染冒烟（0.7.0 新组件）', async () => {
    const KillDialog = (await import('../src/components/business/ContainerKillDialog.vue')).default
    const wrapper = await mountSmoke(KillDialog, {
      props: {
        modelValue: true,
        hostId: 'prod-01',
        targets: [{ id: 'abc', name: 'web-frontend' }]
      }
    })

    await wrapper.vm.$nextTick()
    await Promise.resolve()
    await Promise.resolve()
    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // ⚠️ `el-dialog` **默认不传送到 body**（`append-to-body` 默认 false），
    // 因此对话框内容在 `wrapper.text()` 里——只有下拉选项会被传送到 body。
    expect(wrapper.text()).toContain('web-frontend')
    // 信号下拉的选项在 body 里（EP 的 popper 默认 teleport）
    expect(document.body.textContent ?? '').toContain('SIGKILL')
    wrapper.unmount()
  })

  it('编辑并重建对话框渲染冒烟（0.7.0 新组件）', async () => {
    const EditDialog = (await import('../src/components/business/ContainerEditDialog.vue')).default
    const wrapper = await mountSmoke(EditDialog, {
      props: { modelValue: true, hostId: 'prod-01', containerId: 'abc' }
    })

    await wrapper.vm.$nextTick()
    await Promise.resolve()
    await Promise.resolve()
    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    wrapper.unmount()
  })

  it('新建容器对话框：三个编辑器与镜像下拉都能渲染', async () => {
    const Dialog = (await import('../src/components/business/CreateContainerDialog.vue')).default
    const wrapper = await mountSmoke(Dialog, { props: { modelValue: true, hostId: 'prod-01' } })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // ⚠️ 对话框带 `append-to-body`：内容被传送到 body，
    // `wrapper.text()` 里**看不到**它——必须查 document.body。
    const body = document.body.textContent ?? ''
    // 三个编辑器的「表格 / 批量粘贴」切换必须都在
    expect(body).toContain('批量粘贴')
    expect(body).toContain('端口映射')
    expect(body).toContain('环境变量')
    expect(body).toContain('卷挂载')
    wrapper.unmount()
  })
})

describe('0.5.0 实时通道组件渲染冒烟', () => {
  it('日志视口（虚拟滚动）渲染出序号与正文', async () => {
    const LogViewport = (await import('../src/components/business/LogViewport.vue')).default

    const wrapper = await mountSmoke(LogViewport, {
      props: {
        lines: [
          { seq: 1, timestamp: '2026-10-20T10:00:00Z', line: 'hello from smoke' },
          { seq: 2, timestamp: '2026-10-20T10:00:01Z', line: 'second line' }
        ],
        mutation: { version: 1, kind: 'reset', count: 2 },
        follow: true,
        paused: false,
        hasMore: false,
        loadingOlder: false,
        wrap: true,
        showTimestamp: true,
        dropped: 0
      }
    })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    expect(wrapper.text()).toContain('hello from smoke')
    expect(wrapper.text()).toContain('second line')
    wrapper.unmount()
  })

  it('日志视口：丢了行时要提示', async () => {
    const LogViewport = (await import('../src/components/business/LogViewport.vue')).default

    const wrapper = await mountSmoke(LogViewport, {
      props: {
        lines: [{ seq: 1, timestamp: null, line: 'only one' }],
        mutation: { version: 1, kind: 'reset', count: 1 },
        follow: true,
        paused: false,
        hasMore: false,
        loadingOlder: false,
        wrap: true,
        showTimestamp: false,
        dropped: 7
      }
    })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    expect(wrapper.text()).toContain('已丢弃 7 行')
    wrapper.unmount()
  })

  it('通知铃铛渲染出未读标记', async () => {
    const NotificationBell = (await import('../src/components/common/NotificationBell.vue')).default

    const wrapper = await mountSmoke(NotificationBell)

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // 无未读时不该有红点；有通知时要有。
    expect(wrapper.find('.bg-danger').exists()).toBe(false)

    const { useNotificationStore } = await import('../src/stores/notifications')
    useNotificationStore().push({
      hostId: 'local',
      containerId: 'abc123',
      containerName: 'web',
      exitCode: 137,
      outOfMemory: true,
      occurredAt: '2026-10-20T10:00:00Z'
    })

    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    expect(wrapper.find('.bg-danger').exists()).toBe(true)
    wrapper.unmount()
  })

  it('容器日志页渲染出工具条与日志内容', async () => {
    const ContainerLogsView = (await import('../src/views/ContainerLogsView.vue')).default

    const wrapper = await mountSmoke(ContainerLogsView, {
      path: '/hosts/prod-01/containers/abc123/logs'
    })

    await wrapper.vm.$nextTick()
    // 等日志请求的 promise 落定
    await Promise.resolve()
    await Promise.resolve()
    await wrapper.vm.$nextTick()

    expect(templateBugs(), '渲染期告警').toEqual([])
    // 工具条上的开关必须在（模板改过最容易在这里炸）
    expect(wrapper.text()).toContain('实时跟随')
    expect(wrapper.text()).toContain('自动换行')
    expect(wrapper.text()).toContain('下载日志')
    wrapper.unmount()
  })
})
