<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import EnvVarEditor from '@/components/business/EnvVarEditor.vue'
import PortMappingEditor from '@/components/business/PortMappingEditor.vue'
import VolumeMountEditor from '@/components/business/VolumeMountEditor.vue'
import { listImages } from '@/api/image'
import { listNetworks } from '@/api/network'
import type { CreateContainerPayload } from '@/api/container'
import type { NetworkSummary } from '@/types/api'
import {
  formatEnvEntry,
  toPortPayload,
  toVolumePayload,
  type EnvEntry,
  type PortMapping,
  type VolumeMount
} from '@/utils/container-input'
import {
  buildHealthCheck,
  hasCapabilityConflict,
  parseCapabilities,
  parseCpuCores,
  parseDevices,
  parseLogOptions,
  parseMemoryMiB,
  parseSysctls,
  parseUlimits,
  type LineParse
} from '@/utils/advanced-create'

/**
 * 容器配置表单（0.0.14 重排输入区，0.7.0 抽出为独立组件）。
 *
 * ## 为什么抽出来
 *
 * 原本这份表单长在 `CreateContainerDialog` 里，只有「新建容器」用得上。
 * 而 0.7.0 的「编辑并重建」需要**同一份表单**——只是初值不同、提交目标不同。
 * 两处各写一份的话，改一处忘了另一处，症状是「新建表单多了个字段、编辑表单没有」，
 * 而用户看到的只是「编辑保存后那个字段没生效」。
 *
 * ## 「只提交改过的字段」（`initial` 存在时）
 *
 * ⚠️ **这是本组件最要紧的一条**。
 *
 * 后端的重建接口是「**请求里出现的字段才覆盖**」的语义——
 * 没给的字段沿用原容器。这样才能保住那些**界面上根本没展示**的字段
 * （capabilities、sysctls、ulimits、devices、日志驱动、健康检查……）：
 * 否则用户「只是改了个环境变量」，却把容器的能力集清空了，而界面上根本没有提示。
 *
 * 因此本组件在有 `initial` 时**只发与初值不同的字段**。若改成「全量发」，
 * 那些没展示的字段就会以「空」的形式被发出去 → 等于清空。
 *
 * 判定必须在**表单内部状态**这一层做，而不是拿组装好的 payload 去比：
 * `cmd` 在表单里是**一行文本**（`splitArgs` 按空白切），
 * `["/bin/sh","-c","echo hi"]` 预填成文本再切回来会变成三个元素——
 * 用 payload 比会误判成「改过了」，于是把一个被破坏的命令发出去。
 * 比文本则能看出「用户没动过」。
 */
const { t } = useI18n()

const props = withDefaults(
  defineProps<{
    /** 主机标识（主机名） */
    hostId: string
    /**
     * 初值（详情投影转成的配置）。
     *
     * `null` / 省略 = 新建：表单重置为默认值，提交时**全量**发字段。
     * 给定值 = 编辑：按初值预填，提交时**只发改过的字段**。
     */
    initial?: CreateContainerPayload | null
    /**
     * 是否显示「容器名」字段。
     *
     * 重建时为 `false`：重建沿用原容器名（后端会忽略 `config.name`），
     * 给一个能改却改不动的输入框比不给更糟。
     */
    showName?: boolean
  }>(),
  { initial: null, showName: true }
)

const emit = defineEmits<{
  /** 提交（已通过校验） */
  submit: [payload: CreateContainerPayload]
  /** 可提交状态变化（供父组件禁用提交按钮） */
  'validity-change': [valid: boolean]
}>()

const image = ref('')
const name = ref('')
const cmd = ref('')
const entrypoint = ref('')

/** 三个编辑器的结构化值；它们的文本草稿与行号校验都封装在组件内部。 */
const envEntries = ref<EnvEntry[]>([])
const portEntries = ref<PortMapping[]>([])
const volumeEntries = ref<VolumeMount[]>([])

/**
 * 三个编辑器回传的出错行号（`null` = 全部合法）。
 *
 * 必须据此**拦下提交**：解析失败时编辑器给出的结构化数组只含出错行**之前**的合法部分，
 * 若照样提交，就会建出一个「少了环境变量 / 少了一条端口」却提示成功的容器。
 * 错误文案由各编辑器自己显示（带行号），这里只做拦截。
 */
const envError = ref<number | null>(null)
const portError = ref<number | null>(null)
const volumeError = ref<number | null>(null)

const restartPolicy = ref('no')
const autoRemove = ref(false)
const network = ref('')
const hostname = ref('')
const workingDir = ref('')
const tty = ref(false)
const stdinOpen = ref(false)

// ===== 0.3.0 高级创建参数 =====
// 全部**默认空**：空 = 不传 = 走镜像/守护进程的默认值。给它们预设初值（如 logDriver 默认
// json-file）看起来无害，但会让「我没改过」也变成一个显式设置，而两种情况的区别在
// 「重建容器」时才会显现（那就是另一个话题了）。
const user = ref('')
const capAdd = ref('')
const capDrop = ref('')
const memoryInput = ref('')
const cpusInput = ref('')
const healthTest = ref('')
const healthEnabled = ref(false)
const healthInterval = ref('')
const healthTimeout = ref('')
const healthStartPeriod = ref('')
const healthRetries = ref('')
const sysctls = ref('')
const ulimits = ref('')
const devices = ref('')
const logDriverType = ref('')
const logDriverOptions = ref('')

/** 是否显式**关闭**镜像自带的健康检查（0.3.0）。 */
const healthDisabled = ref(false)

/** 高级参数的字段级错误：key 是字段名，value 是错误 i18n key。 */
const advancedErrors = ref<Record<string, string>>({})

/**
 * 折叠面板的展开项。
 *
 * ⚠️ **默认展开「基本信息」**：它装的是主机名 / 工作目录 / 运行选项，
 * 偶尔会用到；而「高级参数」才是真正用不到的。
 * 早先两个都默认收起，用户得先点开才知道里面有什么。
 */
const activePanels = ref<string[]>(['basic'])

/**
 * 主机上现有的网络（0.2.0），用于把「网络名」从手打变成选。
 *
 * ⚠️ **加载失败不能阻断创建**：拉列表只是一个便利功能，
 * 用户仍然可以手打网络名（包括本项目没列出来的模式名，如 `container:xxx`）。
 * 因此这里静默失败并把下拉留空——而不是弹一个错误让整个对话框用不了。
 */
const networks = ref<NetworkSummary[]>([])
const networksLoading = ref(false)

/**
 * 本机镜像列表（用于「镜像」下拉）。
 *
 * ⚠️ **拉取失败不阻断创建**：用户必须能填一个本机没镜像的名字
 * （比如即将 `docker pull` 的镜像，或另一个仓库的镜像）。
 * 因此下拉是可空的、允许手输（`allow-create`），拉取失败时静默降级成「只能手打」。
 */
const imageOptions = ref<ImageOption[]>([])
const imagesLoading = ref(false)

/** 镜像下拉的候选项。 */
interface ImageOption {
  /** 传给 Docker 的镜像引用（标签优先，悬挂镜像用 ID） */
  reference: string
  /** 展示文本 */
  label: string
  /** 副标题（悬挂镜像没有标签，用短 ID 提示） */
  hint: string
}

/**
 * 拉取本机镜像，供「镜像」下拉选择。
 *
 * ⚠️ **必须显式要最大页**：列表接口默认每页 20 条，
 * 镜像多的主机上候选会悄悄只剩前 20 个——用户会以为某个镜像「不存在」。
 */
async function loadImages(): Promise<void> {
  if (props.hostId.length === 0) return

  imagesLoading.value = true
  try {
    const result = await listImages(props.hostId, { pageSize: 100 })

    imageOptions.value = result.items.flatMap((item) => {
      const tags = item.repoTags.filter((tag) => tag.length > 0 && tag !== '<none>:<none>')

      // 悬挂镜像（没有任何标签）只能用 ID 启动，展示成短 ID + 完整 ID 的提示
      if (tags.length === 0) {
        const short = item.id.replace(/^sha256:/, '').slice(0, 12)

        return [{ reference: item.id, label: short, hint: item.id }]
      }

      return tags.map((tag) => ({ reference: tag, label: tag, hint: '' }))
    })
  } catch {
    imageOptions.value = []
  } finally {
    imagesLoading.value = false
  }
}

/** 本组件自己校验的字段级错误：key 是字段名，value 是错误 key（交由 t() 取文案）。 */
const fieldErrors = ref<Record<string, string>>({})

/** 输入区有未解析完的行时直接禁用提交，而不是提交后再弹错误。 */
const hasInputError = computed(
  () => envError.value !== null || portError.value !== null || volumeError.value !== null
)

const canSubmit = computed(() => image.value.trim().length > 0 && !hasInputError.value)

/**
 * 表单状态的一份**纯数据快照**（用于「哪些字段被改过」的判定）。
 *
 * ⚠️ 必须是**深拷贝**：编辑器的数组是响应式的，直接存引用的话，
 * 用户改完之后快照也跟着变了 → 永远判定成「没改过」→ 改动发不出去。
 */
interface FormState {
  image: string
  name: string
  cmd: string
  entrypoint: string
  env: string
  ports: string
  volumes: string
  restartPolicy: string
  autoRemove: boolean
  network: string
  hostname: string
  workingDir: string
  tty: boolean
  stdinOpen: boolean
  user: string
  capAdd: string
  capDrop: string
  memory: string
  cpus: string
  healthTest: string
  healthEnabled: boolean
  healthDisabled: boolean
  healthInterval: string
  healthTimeout: string
  healthStartPeriod: string
  healthRetries: string
  sysctls: string
  ulimits: string
  devices: string
  logDriverType: string
  logDriverOptions: string
}

/** 取当前表单状态（纯数据）。 */
function snapshot(): FormState {
  return {
    image: image.value,
    name: name.value,
    cmd: cmd.value,
    entrypoint: entrypoint.value,
    env: JSON.stringify(envEntries.value),
    ports: JSON.stringify(portEntries.value),
    volumes: JSON.stringify(volumeEntries.value),
    restartPolicy: restartPolicy.value,
    autoRemove: autoRemove.value,
    network: network.value,
    hostname: hostname.value,
    workingDir: workingDir.value,
    tty: tty.value,
    stdinOpen: stdinOpen.value,
    user: user.value,
    capAdd: capAdd.value,
    capDrop: capDrop.value,
    memory: memoryInput.value,
    cpus: cpusInput.value,
    healthTest: healthTest.value,
    healthEnabled: healthEnabled.value,
    healthDisabled: healthDisabled.value,
    healthInterval: healthInterval.value,
    healthTimeout: healthTimeout.value,
    healthStartPeriod: healthStartPeriod.value,
    healthRetries: healthRetries.value,
    sysctls: sysctls.value,
    ulimits: ulimits.value,
    devices: devices.value,
    logDriverType: logDriverType.value,
    logDriverOptions: logDriverOptions.value
  }
}

/** 初值快照；`null` = 新建（提交时全量发字段）。 */
let initialState: FormState | null = null

/**
 * 把初值填进表单。
 *
 * ⚠️ **只填「能一一对应」的字段**：能力集 / sysctls / ulimits / 设备 / 日志驱动 /
 * 健康检查在详情投影里**没有**（后端没暴露），因此它们保持空——
 * 而正因为空且未改，提交时不会带上 → 后端沿用原容器的值。
 */
function applyInitial(payload: CreateContainerPayload): void {
  image.value = payload.image ?? ''
  name.value = payload.name ?? ''
  cmd.value = (payload.cmd ?? []).join(' ')
  entrypoint.value = (payload.entrypoint ?? []).join(' ')
  envEntries.value = (payload.env ?? []).map((entry) => {
    const separator = entry.indexOf('=')

    return separator < 0
      ? { key: entry, value: '' }
      : { key: entry.slice(0, separator), value: entry.slice(separator + 1) }
  })
  portEntries.value = (payload.ports ?? []).map((port) => ({
    containerPort: port.containerPort,
    hostPort: port.hostPort ?? null,
    hostIp: port.hostIp ?? null,
    protocol: port.type === 'udp' ? 'udp' : 'tcp'
  }))
  volumeEntries.value = (payload.volumes ?? []).map((volume) => ({
    source: volume.source,
    destination: volume.destination,
    readOnly: volume.readOnly ?? false
  }))
  restartPolicy.value = payload.restartPolicy ?? 'no'
  autoRemove.value = payload.autoRemove ?? false
  network.value = payload.network ?? ''
  hostname.value = payload.hostname ?? ''
  workingDir.value = payload.workingDir ?? ''
  tty.value = payload.tty ?? false
  stdinOpen.value = payload.stdinOpen ?? false
  user.value = payload.user ?? ''
  memoryInput.value = payload.memoryLimitBytes ? String(Math.round(payload.memoryLimitBytes / 1024 / 1024)) : ''
  cpusInput.value = payload.nanoCpus ? String(payload.nanoCpus / 1_000_000_000) : ''
  healthEnabled.value = false
  healthDisabled.value = false
  healthTest.value = ''
  healthInterval.value = ''
  healthTimeout.value = ''
  healthStartPeriod.value = ''
  healthRetries.value = ''

  initialState = snapshot()
}

/** 重置为「新建」的默认值。 */
function reset(): void {
  image.value = ''
  name.value = ''
  cmd.value = ''
  entrypoint.value = ''
  envEntries.value = []
  portEntries.value = []
  volumeEntries.value = []
  envError.value = null
  portError.value = null
  volumeError.value = null
  restartPolicy.value = 'no'
  autoRemove.value = false
  network.value = ''
  hostname.value = ''
  workingDir.value = ''
  tty.value = false
  stdinOpen.value = false
  user.value = ''
  capAdd.value = ''
  capDrop.value = ''
  memoryInput.value = ''
  cpusInput.value = ''
  healthTest.value = ''
  healthEnabled.value = false
  healthInterval.value = ''
  healthTimeout.value = ''
  healthStartPeriod.value = ''
  healthRetries.value = ''
  sysctls.value = ''
  ulimits.value = ''
  devices.value = ''
  logDriverType.value = ''
  logDriverOptions.value = ''
  healthDisabled.value = false
  advancedErrors.value = {}
  fieldErrors.value = {}
  activePanels.value = ['basic']

  initialState = null
}

// 初值变了就重新填表（编辑对话框是异步拿到详情后才填的）
watch(
  () => props.initial,
  (payload) => {
    if (payload) {
      applyInitial(payload)
    } else {
      reset()
    }

    void loadNetworks()
    void loadImages()
  },
  { immediate: true }
)

/**
 * 拉取主机上的网络，供「网络」下拉选择。
 *
 * ⚠️ **失败一律静默**：拉列表只是便利，用户仍可手打网络名
 * （`host` / `none` / `container:xxx` 等本项目未必列得全）。
 * 弹一个错误会让整个创建对话框用不了——为一个可选项不值得。
 *
 * ⚠️ **必须显式要最大页**：列表接口默认每页 20 条，网络多的主机上
 * 下拉框会悄悄只剩前 20 个，用户会以为某个网络「不存在」。
 */
async function loadNetworks(): Promise<void> {
  if (props.hostId.length === 0) return

  networksLoading.value = true
  try {
    networks.value = (await listNetworks(props.hostId, { pageSize: 100 })).items
  } catch {
    networks.value = []
  } finally {
    networksLoading.value = false
  }
}

/** 容器名的合法性规则与后端一致（见其 `RequireContainerName`）。 */
function validateName(value: string): boolean {
  if (value.trim().length === 0) return true

  const trimmed = value.trim()

  return /^[A-Za-z0-9][A-Za-z0-9_.-]*$/.test(trimmed)
}

/**
 * 把一个 `LineParse` 的结果并进 payload 或 errors。
 *
 * ⚠️ `skip` **不写字段**：空输入发空数组/空串会被守护进程当成「显式清空」，
 * 与「我没填」的语义完全不同。
 */
function merge<T>(
  parsed: LineParse<T>,
  key: string,
  target: CreateContainerPayload,
  errors: Record<string, string>,
  errorKey: string
): void {
  if (parsed.kind === 'skip') return

  if (parsed.kind === 'error') {
    errors[key] = t(errorKey)
    return
  }

  ;(target as unknown as Record<string, unknown>)[key] = parsed.value
}

/**
 * 把 0.3.0 的高级参数组装进 payload，并把解析失败记到 `errors`。
 *
 * @returns 组装好的 payload；任一字段解析失败时返回 <see langword="null"/>（错误已记进 `errors`）。
 */
function withAdvancedOptions(
  payload: CreateContainerPayload,
  errors: Record<string, string>
): CreateContainerPayload | null {
  merge(parseCapabilities(capAdd.value), 'capAdd', payload, errors, 'container.advanced.capabilityInvalid')
  merge(parseCapabilities(capDrop.value), 'capDrop', payload, errors, 'container.advanced.capabilityInvalid')
  merge(parseMemoryMiB(memoryInput.value), 'memoryLimitBytes', payload, errors, 'container.advanced.memoryInvalid')
  merge(parseCpuCores(cpusInput.value), 'nanoCpus', payload, errors, 'container.advanced.cpusInvalid')
  merge(parseSysctls(sysctls.value), 'sysctls', payload, errors, 'container.advanced.sysctlInvalid')
  merge(parseUlimits(ulimits.value), 'ulimits', payload, errors, 'container.advanced.ulimitInvalid')
  merge(parseDevices(devices.value), 'devices', payload, errors, 'container.advanced.deviceInvalid')

  const health = healthDisabled.value
    ? ({ kind: 'ok' as const, value: { kind: 'none' as const } })
    : buildHealthCheck(healthEnabled.value, healthTest.value, {
        interval: healthInterval.value,
        timeout: healthTimeout.value,
        startPeriod: healthStartPeriod.value,
        retries: healthRetries.value
      })

  if (health.kind === 'error') {
    errors.healthCheck = t('container.advanced.healthInvalid')
  } else if (health.kind === 'ok') {
    // ⚠️ `kind: 'none'` → `test: ['NONE']`：Docker 见到它就关闭检查，
    // 与「不传」（继承镜像里的 HEALTHCHECK）语义完全不同，必须显式区分。
    payload.healthCheck =
      health.value.kind === 'none'
        ? { test: ['NONE'] }
        : (health.value as unknown as CreateContainerPayload['healthCheck'])
  }

  if (logDriverType.value.trim().length > 0) {
    const options = parseLogOptions(logDriverOptions.value)

    if (options.kind === 'error') {
      errors.logDriver = t('container.advanced.logDriverInvalid')
    } else {
      payload.logDriver = {
        type: logDriverType.value.trim(),
        ...(options.kind === 'ok' ? { options: options.value } : {})
      }
    }
  }

  if (user.value.trim().length > 0) {
    payload.user = user.value.trim()
  }

  return payload
}

/**
 * 按空白切分命令行式输入。
 *
 * 刻意**不支持引号分组**：`docker run --name "my app"` 这类带引号的参数，
 * 切开后引号会留在元素里，传给 Docker 就是个错的名字。这里不做一个半吊子的 shell 词法解析——
 * 需要复杂参数的用户可以直接用后端的原始字段（本表单只覆盖常用场景）。
 */
function splitArgs(text: string): string[] {
  return text.trim().split(/\s+/).filter((part) => part.length > 0)
}

/**
 * 组装出**完整**的 payload（不做「只发改动字段」的裁剪）。
 *
 * @returns 完整 payload；高级参数解析失败时返回 <see langword="null"/>。
 */
function buildFullPayload(): CreateContainerPayload | null {
  /** 丢掉完全空白的行（用户点了「添加一行」却没填）。 */
  const filledEnv = envEntries.value.filter((entry) => entry.key.trim().length > 0)

  const payload: CreateContainerPayload = {
    image: image.value.trim(),
    ...(name.value.trim() ? { name: name.value.trim() } : {}),
    ...(cmd.value.trim() ? { cmd: splitArgs(cmd.value) } : {}),
    ...(entrypoint.value.trim() ? { entrypoint: splitArgs(entrypoint.value) } : {}),
    // ⚠️ **丢掉完全空白的行**：用户在表格里点了「添加一行」却没填，
    // 那一行会以 `KEY=` 的形态发出去，而守护进程会把它当成「显式置空」——
    // 与「我没填」的语义完全不同。
    ...(filledEnv.length > 0 ? { env: filledEnv.map(formatEnvEntry) } : {}),
    ...(portEntries.value.length > 0 ? { ports: portEntries.value.map(toPortPayload) } : {}),
    ...(volumeEntries.value.length > 0 ? { volumes: volumeEntries.value.map(toVolumePayload) } : {}),
    restartPolicy: restartPolicy.value,
    autoRemove: autoRemove.value,
    ...(network.value.trim() ? { network: network.value.trim() } : {}),
    ...(hostname.value.trim() ? { hostname: hostname.value.trim() } : {}),
    ...(workingDir.value.trim() ? { workingDir: workingDir.value.trim() } : {})
  }

  if (tty.value) payload.tty = true
  if (stdinOpen.value) payload.stdinOpen = true

  // ⚠️ **不要叫 `advancedErrors`**：外层已经有一个同名的 `ref`，
  // 同名局部变量会把它**遮住**——而 `advancedErrors.value = advancedErrors`
  // 在遮蔽下变成了「给局部对象的 `value` 属性赋值」，于是**错误永远显示不出来**。
  const parseErrors: Record<string, string> = {}
  const advanced = withAdvancedOptions(payload, parseErrors)

  if (advanced === null) {
    // 高级参数解析失败：错误已记进 `advancedErrors`，表单自己会显示。
    return null
  }

  // 能力既增又删同一项是跨字段的错误（两个独立解析都合法），因此单独拦一道：
  // Docker 不报错（两个列表都会发过去），生效哪一个取决于守护进程内部的合并顺序。
  const capAddParsed = parseCapabilities(capAdd.value)
  const capDropParsed = parseCapabilities(capDrop.value)

  if (
    capAddParsed.kind === 'ok' &&
    capDropParsed.kind === 'ok' &&
    hasCapabilityConflict(capAddParsed.value, capDropParsed.value)
  ) {
    parseErrors.capAdd = t('container.advanced.capabilityConflict')
  }

  if (Object.keys(parseErrors).length > 0) {
    advancedErrors.value = parseErrors
    // ⚠️ 顺带**清掉旧错误**：用户改完一行后提交，提示必须跟着变。
    // 而不清的话会一直红着，而那一行现在已经合法了。
    return null
  }

  advancedErrors.value = {}

  return advanced
}

/**
 * 状态字段 → payload 字段的映射表，外加「**清空时**要发的值」。
 *
 * ⚠️ `cleared` 不能省：表单里「清空」的表现是**字段变空**，而组装出来的 payload
 * 会把这个字段**整个省略**（省略 = 「没填」）。于是「用户清空了它」与「用户没碰它」
 * 变成了同一件事——而这两件事的后果完全相反：
 * 前者要把原值清掉，后者要保住原值。
 * <br/>
 * 因此每个可清空的字段都要显式给出「清空 = 发什么」：
 * 列表发 `[]`、字符串发 `''`、布尔发 `false`、数值发 `0`（= 不限制）。
 */
const FIELD_MAP: Array<{ state: keyof FormState; payload: string; cleared?: unknown }> = [
  { state: 'image', payload: 'image' },
  { state: 'cmd', payload: 'cmd', cleared: [] },
  { state: 'entrypoint', payload: 'entrypoint', cleared: [] },
  { state: 'env', payload: 'env', cleared: [] },
  { state: 'ports', payload: 'ports', cleared: [] },
  { state: 'volumes', payload: 'volumes', cleared: [] },
  { state: 'restartPolicy', payload: 'restartPolicy' },
  { state: 'autoRemove', payload: 'autoRemove' },
  { state: 'network', payload: 'network', cleared: '' },
  { state: 'hostname', payload: 'hostname', cleared: '' },
  { state: 'workingDir', payload: 'workingDir', cleared: '' },
  { state: 'tty', payload: 'tty', cleared: false },
  { state: 'stdinOpen', payload: 'stdinOpen', cleared: false },
  { state: 'user', payload: 'user', cleared: '' },
  { state: 'capAdd', payload: 'capAdd', cleared: [] },
  { state: 'capDrop', payload: 'capDrop', cleared: [] },
  { state: 'memory', payload: 'memoryLimitBytes', cleared: 0 },
  { state: 'cpus', payload: 'nanoCpus', cleared: 0 },
  { state: 'sysctls', payload: 'sysctls', cleared: {} },
  { state: 'ulimits', payload: 'ulimits', cleared: [] },
  { state: 'devices', payload: 'devices', cleared: [] },
  { state: 'logDriverType', payload: 'logDriver' }
]

/**
 * 裁剪出**只含改过字段**的 payload（编辑场景）。
 *
 * @param full 完整 payload。
 * @param before 初值快照。
 * @returns 只含改动字段的 payload。
 */
function pickChanged(full: CreateContainerPayload, before: FormState): CreateContainerPayload {
  const now = snapshot()
  const out: Record<string, unknown> = {}
  const source = full as unknown as Record<string, unknown>

  for (const field of FIELD_MAP) {
    if (now[field.state] === before[field.state]) continue

    const value = source[field.payload]

    // payload 里没有这个字段 = 「清空」：发 `cleared` 指定的空值。
    out[field.payload] = value === undefined ? field.cleared : value
  }

  // 健康检查是一个整体：用户动了其中任何一项就整份发出去。
  if (
    now.healthTest !== before.healthTest ||
    now.healthEnabled !== before.healthEnabled ||
    now.healthDisabled !== before.healthDisabled ||
    now.healthInterval !== before.healthInterval ||
    now.healthTimeout !== before.healthTimeout ||
    now.healthStartPeriod !== before.healthStartPeriod ||
    now.healthRetries !== before.healthRetries
  ) {
    out.healthCheck = full.healthCheck
  }

  return out as unknown as CreateContainerPayload
}

/**
 * 校验并提交。
 *
 * 校验失败时**不发**事件（父组件因此不会去调后端），错误显示在表单里。
 */
function submit(): void {
  const errors: Record<string, string> = {}

  if (props.showName && !validateName(name.value)) errors.name = t('container.create.nameInvalid')

  fieldErrors.value = errors

  // 提交按钮已随 hasInputError 禁用；这里再兜一道，防止键盘直接触发提交。
  if (Object.keys(errors).length > 0 || hasInputError.value) return

  const full = buildFullPayload()

  // 高级参数解析失败时 `buildFullPayload` 返回 null——此时不该提交。
  if (full === null) return

  const payload = initialState ? pickChanged(full, initialState) : full

  emit('submit', payload)
}

watch(canSubmit, (valid) => emit('validity-change', valid), { immediate: true })

defineExpose({ submit, reset })
</script>

<template>
  <el-form label-position="top" @submit.prevent>
    <!--
      镜像 + 容器名（两列）：整个表单里**最高频**的两个字段，
      放在最上面且并排，占据最少的高度。

      镜像用下拉而不是纯输入框：绝大多数时候用户要的就是本机已有的镜像，
      从列表里选比手打省事（少打一遍、也不会打错）。
      ⚠️ 但**必须允许手输**（`allow-create`）：用户可能要填一个本机还没有、
      准备 `docker pull` 的镜像，或另一个仓库的镜像。
    -->
    <div class="grid gap-x-4 sm:grid-cols-2">
      <el-form-item
        :label="t('container.create.image')"
        :error="image.trim().length === 0 ? t('container.create.imageRequired') : undefined"
      >
        <el-select
          v-model="image"
          filterable
          allow-create
          default-first-option
          clearable
          :loading="imagesLoading"
          class="w-full"
          :placeholder="t('container.create.imagePlaceholder')"
        >
          <el-option
            v-for="option in imageOptions"
            :key="option.reference"
            :value="option.reference"
            :label="option.label"
          >
            <span class="font-mono text-xs">{{ option.label }}</span>
            <span v-if="option.hint" class="ml-2 text-xs text-content-faint">{{ option.hint }}</span>
          </el-option>
        </el-select>
      </el-form-item>

      <el-form-item
        v-if="showName"
        :label="t('container.create.name')"
        :error="fieldErrors.name"
      >
        <el-input v-model="name" :placeholder="t('container.create.namePlaceholder')" clearable />
      </el-form-item>
    </div>

    <!--
      端口 / 环境变量 / 挂载：三个**结构化编辑器**。
      它们放在命令与入口点之前，因为「跑起来要能访问」比「跑什么命令」更常用。
    -->
    <el-form-item :label="t('container.create.ports')">
      <PortMappingEditor v-model="portEntries" v-model:error-line="portError" />
    </el-form-item>

    <el-form-item :label="t('container.create.env')">
      <EnvVarEditor v-model="envEntries" v-model:error-line="envError" />
    </el-form-item>

    <el-form-item :label="t('container.create.volumes')">
      <VolumeMountEditor
        v-model="volumeEntries"
        v-model:error-line="volumeError"
        :host-id="hostId"
      />
    </el-form-item>

    <!-- 启动命令 + 入口点（两列） -->
    <div class="grid gap-x-4 sm:grid-cols-2">
      <el-form-item :label="t('container.create.cmd')">
        <el-input v-model="cmd" :placeholder="t('container.create.cmdHint')" />
      </el-form-item>

      <el-form-item :label="t('container.create.entrypoint')">
        <el-input v-model="entrypoint" :placeholder="t('container.create.entrypointHint')" />
      </el-form-item>
    </div>

    <!-- 重启策略 + 网络（两列） -->
    <div class="grid gap-x-4 sm:grid-cols-2">
      <el-form-item :label="t('container.create.restartPolicy')">
        <el-select v-model="restartPolicy" class="w-full">
          <el-option value="no" :label="t('container.restartPolicy.no')" />
          <el-option value="always" :label="t('container.restartPolicy.always')" />
          <el-option value="on-failure" :label="t('container.restartPolicy.onFailure')" />
          <el-option value="unless-stopped" :label="t('container.restartPolicy.unlessStopped')" />
        </el-select>
      </el-form-item>

      <el-form-item :label="t('container.create.network')">
        <!--
          `el-select` + `allow-create`：既有下拉可选，也允许手打模式名
          （`container:xxx`、`host`、`none` 等本项目不一定列得全）。
          只给下拉会挡住合法输入，只给输入框则要用户去别处查名字。
        -->
        <el-select
          v-model="network"
          filterable
          allow-create
          default-first-option
          clearable
          :loading="networksLoading"
          class="w-full"
          :placeholder="t('container.create.networkPlaceholder')"
        >
          <el-option value="" :label="t('container.create.networkDefault')" />
          <el-option
            v-for="item in networks"
            :key="item.id"
            :value="item.name"
            :label="item.name"
          >
            <span class="font-mono text-xs">{{ item.name }}</span>
            <span class="ml-2 text-xs text-content-faint">{{ item.driver }}</span>
          </el-option>
        </el-select>
      </el-form-item>
    </div>

    <!--
      ⚠️ **两个折叠块**：「基本信息」默认展开，「高级」默认收起。
      折叠是「常用 / 高级」两级结构的本意，而高级字段（能力 / 资源限制 / 健康检查…）
      多数容器一个都不用。
    -->
    <el-collapse v-model="activePanels">
      <el-collapse-item :title="t('container.create.basic')" name="basic">
        <div class="grid gap-x-4 sm:grid-cols-2">
          <el-form-item :label="t('container.create.hostname')">
            <el-input v-model="hostname" clearable />
          </el-form-item>
          <el-form-item :label="t('container.create.workingDir')">
            <el-input v-model="workingDir" clearable />
          </el-form-item>
        </div>

        <!--
          运行选项：三个**布尔开关**。
          ⚠️ 旧版这一组的标题误用了「新建容器」（`container.action.create`）——
          一个和内容完全无关的词，界面上读起来像放错了地方。
        -->
        <el-form-item :label="t('container.create.runOptions')">
          <div class="flex items-center gap-5 pt-1.5">
            <el-checkbox v-model="autoRemove">{{ t('container.create.autoRemove') }}</el-checkbox>
            <el-checkbox v-model="tty">{{ t('container.create.tty') }}</el-checkbox>
            <el-checkbox v-model="stdinOpen">{{ t('container.create.stdinOpen') }}</el-checkbox>
          </div>
        </el-form-item>
      </el-collapse-item>

      <!--
        0.3.0 高级参数：第二个折叠块，与上面的「基本」同级。

        ⚠️ **不要把高级字段平铺到主表单**：它们是低频的（多数容器一个都不用），
        平铺后会把镜像 / 名称 / 端口这些高频字段挤到屏幕外，
        而「创建容器」的最短路径因此变长。折叠是「常用 / 高级」两级结构的本意。

        ⚠️ 行式输入（每行一条）与 EnvVarEditor / PortMappingEditor / VolumeMountEditor
        保持一致：真实 `docker run` 就是这种命令行输入，而嵌套表单控件在对话框宽度下
        塞不下「路径 + 权限 + 删除按钮」三列，且可读性更差。
      -->
      <el-collapse-item :title="t('container.create.advanced')" name="advanced">
        <div class="flex flex-col gap-x-4">
          <p class="mb-1 text-xs text-content-muted">
            {{ t('container.create.advancedHint') }}
          </p>

          <div class="grid gap-x-4 sm:grid-cols-2">
            <el-form-item :label="t('container.advanced.user')">
              <el-input v-model="user" clearable placeholder="1000:1000" />
            </el-form-item>
            <el-form-item :label="t('container.advanced.memory')" :error="advancedErrors.memoryLimitBytes ? t('container.advanced.memoryInvalid') : undefined">
              <el-input v-model="memoryInput" clearable placeholder="512" />
            </el-form-item>
            <el-form-item :label="t('container.advanced.cpus')" :error="advancedErrors.nanoCpus ? t('container.advanced.cpusInvalid') : undefined">
              <el-input v-model="cpusInput" clearable placeholder="1.5" />
            </el-form-item>
            <el-form-item :label="t('container.advanced.logDriver')" :error="advancedErrors.logDriver ? t('container.advanced.logDriverInvalid') : undefined">
              <el-input v-model="logDriverType" clearable placeholder="json-file" />
            </el-form-item>
          </div>

          <el-form-item :label="t('container.advanced.logDriverOptions')">
            <el-input
              v-model="logDriverOptions"
              type="textarea"
              :rows="2"
              placeholder="max-size=10m"
            />
          </el-form-item>

          <div class="grid gap-x-4 sm:grid-cols-2">
            <el-form-item :label="t('container.advanced.capAdd')" :error="advancedErrors.capAdd ? t(advancedErrors.capAdd) : undefined">
              <el-input
                v-model="capAdd"
                type="textarea"
                :rows="2"
                placeholder="NET_ADMIN&#10;SYS_PTRACE"
              />
            </el-form-item>
            <el-form-item :label="t('container.advanced.capDrop')" :error="advancedErrors.capDrop ? t(advancedErrors.capDrop) : undefined">
              <el-input v-model="capDrop" type="textarea" :rows="2" placeholder="ALL" />
            </el-form-item>
          </div>

          <el-form-item :label="t('container.advanced.sysctls')" :error="advancedErrors.sysctls ? t('container.advanced.sysctlInvalid') : undefined">
            <el-input
              v-model="sysctls"
              type="textarea"
              :rows="2"
              placeholder="net.core.somaxconn=1024"
            />
          </el-form-item>

          <el-form-item :label="t('container.advanced.ulimits')" :error="advancedErrors.ulimits ? t('container.advanced.ulimitInvalid') : undefined">
            <el-input
              v-model="ulimits"
              type="textarea"
              :rows="2"
              placeholder="nofile=1024:4096"
            />
          </el-form-item>

          <el-form-item :label="t('container.advanced.devices')" :error="advancedErrors.devices ? t('container.advanced.deviceInvalid') : undefined">
            <el-input v-model="devices" type="textarea" :rows="2" placeholder="/dev/snd:/dev/snd:r" />
            <p class="mt-1 text-xs text-status-warn">
              {{ t('container.advanced.devicesWarning') }}
            </p>
          </el-form-item>

          <el-form-item :label="t('container.advanced.health')" :error="advancedErrors.healthCheck ? t('container.advanced.healthInvalid') : undefined">
            <div class="flex flex-col gap-2">
              <el-checkbox v-model="healthDisabled">
                {{ t('container.advanced.healthDisable') }}
              </el-checkbox>
              <el-checkbox v-model="healthEnabled" :disabled="healthDisabled">
                {{ t('container.advanced.healthEnable') }}
              </el-checkbox>

              <template v-if="healthEnabled && !healthDisabled">
                <el-input
                  v-model="healthTest"
                  type="textarea"
                  :rows="2"
                  placeholder="CMD-SHELL curl -f http://localhost/ || exit 1"
                />
                <div class="grid gap-x-4 sm:grid-cols-4">
                  <el-form-item :label="t('container.advanced.healthInterval')">
                    <el-input v-model="healthInterval" clearable placeholder="30" />
                  </el-form-item>
                  <el-form-item :label="t('container.advanced.healthTimeout')">
                    <el-input v-model="healthTimeout" clearable placeholder="5" />
                  </el-form-item>
                  <el-form-item :label="t('container.advanced.healthStartPeriod')">
                    <el-input v-model="healthStartPeriod" clearable placeholder="10" />
                  </el-form-item>
                  <el-form-item :label="t('container.advanced.healthRetries')">
                    <el-input v-model="healthRetries" clearable placeholder="3" />
                  </el-form-item>
                </div>
              </template>
            </div>
          </el-form-item>
        </div>
      </el-collapse-item>
    </el-collapse>
  </el-form>
</template>
