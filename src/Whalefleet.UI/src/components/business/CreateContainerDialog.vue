<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import {
  createContainer,
  startContainer,
  type CreateContainerPayload
} from '@/api/container'
import EnvVarEditor from '@/components/business/EnvVarEditor.vue'
import PortMappingEditor from '@/components/business/PortMappingEditor.vue'
import VolumeMountEditor from '@/components/business/VolumeMountEditor.vue'
import { listNetworks } from '@/api/network'
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
 * 新建容器对话框（0.0.11，0.0.14 重构输入区）。
 *
 * ## 端口 / 挂载 / 环境变量为什么用行式输入
 * 真实的 `docker run` 命令就是这种行式输入，而嵌套表单控件（端口要两行、协议要下拉、
 * 还要能加第二个端口）在一行里塞不下、读起来还比命令行难懂。**降低输入成本**在这里
 * 比「结构化表单好看」更重要：用户绝大多数情况是把一条现成的命令行粘进来。
 *
 * 0.0.14 把这三个字段从「对话框内部的一组文本框」换成三个**双模编辑器**
 * （`EnvVarEditor` / `PortMappingEditor` / `VolumeMountEditor`）：
 * 批量粘贴为主、逐行编辑为辅，并且解析逻辑下沉到 `utils/container-input.ts`，
 * 两条路径共用同一份校验——否则「粘贴能建、手填建不了」的不一致迟早出现。
 *
 * 因此本文件不再自带任何行解析函数，只负责把结构化结果映射成请求体。
 *
 * ## 关于「高级选项」折叠
 * 后端 `create` 只暴露精选字段（见 `CreateContainerPayload`），本表单与它**一一对应**，
 * 不做「前端支持但后端忽略」的开关——那会让用户以为某项生效了。
 */
const { t } = useI18n()

const props = defineProps<{
  /** 主机标识（主机名） */
  hostId: string
}>()

/**
 * 开关状态用**默认**的 `modelValue`（即 `v-model`），而不是 `v-model:visible`。
 *
 * 刻意如此：`v-model:visible` 要求调用方写全命名参数，而组件只有一个开关，
 * 命名收益为零、写错的风险（漏写导致 `visible` 缺失警告）却是实在的。
 */
const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{
  /** 创建成功（`containerId` 用于「立即启动」） */
  created: [containerId: string]
}>()

const submitting = ref(false)
const createdContainerId = ref<string | null>(null)
const warnings = ref<string[]>([])

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
 * 主机上现有的网络（0.2.0），用于把「网络名」从手打变成选。
 *
 * ⚠️ **加载失败不能阻断创建**：拉列表只是一个便利功能，
 * 用户仍然可以手打网络名（包括本项目没列出来的模式名，如 `container:xxx`）。
 * 因此这里静默失败并把下拉留空——而不是弹一个错误让整个对话框用不了。
 */
const networks = ref<NetworkSummary[]>([])
const networksLoading = ref(false)

/** 本对话框自己校验的字段级错误：key 是字段名，value 是错误 key（交由 t() 取文案）。 */
const fieldErrors = ref<Record<string, string>>({})

/** 输入区有未解析完的行时直接禁用提交，而不是提交后再弹错误。 */
const hasInputError = computed(
  () => envError.value !== null || portError.value !== null || volumeError.value !== null
)

const canSubmit = computed(
  () => image.value.trim().length > 0 && !submitting.value && !hasInputError.value
)

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
  createdContainerId.value = null
  warnings.value = []
}

// 每次打开都清空上一次的残留：否则用户上次填到一半关掉，下次打开会看到一堆他不想要的值。
watch(visible, (open) => {
  if (open) {
    reset()
    void loadNetworks()
  }
})

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
  target: Record<string, unknown>,
  errors: Record<string, string>,
  errorKey: string
): void {
  if (parsed.kind === 'skip') return

  if (parsed.kind === 'error') {
    errors[key] = t(errorKey, parsed)
    return
  }

  target[key] = parsed.value
}

/** 把 0.3.0 的高级参数组装进 payload，并把解析失败记到 `errors`。 */
function withAdvancedOptions(
  payload: CreateContainerPayload,
  errors: Record<string, string>
): CreateContainerPayload {
  merge(parseCapabilities(capAdd.value), 'capAdd', payload as Record<string, unknown>, errors, 'container.advanced.capabilityInvalid')
  merge(parseCapabilities(capDrop.value), 'capDrop', payload as Record<string, unknown>, errors, 'container.advanced.capabilityInvalid')
  merge(parseMemoryMiB(memoryInput.value), 'memoryLimitBytes', payload as Record<string, unknown>, errors, 'container.advanced.memoryInvalid')
  merge(parseCpuCores(cpusInput.value), 'nanoCpus', payload as Record<string, unknown>, errors, 'container.advanced.cpusInvalid')
  merge(parseSysctls(sysctls.value), 'sysctls', payload as Record<string, unknown>, errors, 'container.advanced.sysctlInvalid')
  merge(parseUlimits(ulimits.value), 'ulimits', payload as Record<string, unknown>, errors, 'container.advanced.ulimitInvalid')
  merge(parseDevices(devices.value), 'devices', payload as Record<string, unknown>, errors, 'container.advanced.deviceInvalid')

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

async function submit(): Promise<void> {
  const errors: Record<string, string> = {}

  if (!validateName(name.value)) errors.name = t('container.create.nameInvalid')

  fieldErrors.value = errors

  // 提交按钮已随 hasInputError 禁用；这里再兜一道，防止键盘直接触发提交。
  if (Object.keys(errors).length > 0 || hasInputError.value) return

  const payload: CreateContainerPayload = {
    image: image.value.trim(),
    ...(name.value.trim() ? { name: name.value.trim() } : {}),
    ...(cmd.value.trim() ? { cmd: splitArgs(cmd.value) } : {}),
    ...(entrypoint.value.trim() ? { entrypoint: splitArgs(entrypoint.value) } : {}),
    ...(envEntries.value.length > 0 ? { env: envEntries.value.map(formatEnvEntry) } : {}),
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

  // ===== 0.3.0 高级参数 =====
  const advancedErrors: Record<string, string> = {}
  const advanced = withAdvancedOptions(payload, advancedErrors)

  // 能力既增又删同一项是跨字段的错误（两个独立解析都合法），因此单独拦一道：
  // Docker 不报错（两个列表都会发过去），生效哪一个取决于守护进程内部的合并顺序。
  const capAddParsed = parseCapabilities(capAdd.value)
  const capDropParsed = parseCapabilities(capDrop.value)

  if (
    capAddParsed.kind === 'ok' &&
    capDropParsed.kind === 'ok' &&
    hasCapabilityConflict(capAddParsed.value, capDropParsed.value)
  ) {
    advancedErrors.capAdd = t('container.advanced.capabilityConflict')
  }

  if (Object.keys(advancedErrors).length > 0) {
    advancedErrors.value = advancedErrors
    // ⚠️ 顺带**清掉旧错误**：用户改完一行后提交，提示必须跟着变。
    // 而不清的话会一直红着，而那一行现在已经合法了。
    return
  }

  advancedErrors.value = {}

  submitting.value = true
  try {
    const result = await createContainer(props.hostId, advanced)

    createdContainerId.value = result.containerId
    // ⚠️ warnings **不是错误**：创建已经成功。这些提示（例如「端口未映射」）恰恰是
    // 「容器起来了但访问不通」的原因，必须让用户看见，但绝不能因此把表单当成失败。
    warnings.value = result.warnings ?? []
    ElMessage({ type: 'success', message: t('container.create.success') })
  } catch {
    // 错误提示已由 axios 拦截器统一弹出，这里保持对话框打开让用户能改。
    createdContainerId.value = null
  } finally {
    submitting.value = false
  }
}

/** 创建后接着启动：省掉「先创建、再去列表点启动」两步。 */
async function createAndStart(): Promise<void> {
  const containerId = createdContainerId.value
  if (!containerId) return

  submitting.value = true
  try {
    await startContainer(props.hostId, containerId)
    emit('created', containerId)
  } finally {
    submitting.value = false
  }
}

/** 只创建，稍后手动启动。 */
function createOnly(): void {
  const containerId = createdContainerId.value
  if (containerId) emit('created', containerId)
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
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('container.create.title')"
    width="min(720px, 94vw)"
    top="6vh"
    append-to-body
  >
    <!-- 创建成功后的二次状态：不再显示表单，避免「再点一次创建」重复创建。 -->
    <div v-if="createdContainerId" class="flex flex-col gap-4 py-2">
      <el-alert type="success" :closable="false" :title="t('container.create.success')" />

      <div v-if="warnings.length > 0">
        <p class="mb-1.5 text-xs font-medium text-content-muted">
          {{ t('container.create.warnings') }}
        </p>
        <ul class="app-warn-list">
          <li v-for="(warning, index) in warnings" :key="index" class="text-xs">{{ warning }}</li>
        </ul>
      </div>
    </div>

    <el-form v-else label-position="top" @submit.prevent>
      <el-form-item :label="t('container.create.image')" :error="image.trim().length === 0 && submitting ? t('container.create.imageRequired') : undefined">
        <el-input v-model="image" :placeholder="t('container.create.imagePlaceholder')" clearable />
      </el-form-item>

      <div class="grid gap-x-4 sm:grid-cols-2">
        <el-form-item :label="t('container.create.name')" :error="fieldErrors.name">
          <el-input v-model="name" :placeholder="t('container.create.namePlaceholder')" clearable />
        </el-form-item>

        <el-form-item :label="t('container.create.restartPolicy')">
          <el-select v-model="restartPolicy" class="w-full">
            <el-option value="no" :label="t('container.restartPolicy.no')" />
            <el-option value="always" :label="t('container.restartPolicy.always')" />
            <el-option value="on-failure" :label="t('container.restartPolicy.onFailure')" />
            <el-option value="unless-stopped" :label="t('container.restartPolicy.unlessStopped')" />
          </el-select>
        </el-form-item>
      </div>

      <el-form-item :label="t('container.create.cmd')">
        <el-input v-model="cmd" :placeholder="t('container.create.cmdHint')" />
      </el-form-item>

      <el-form-item :label="t('container.create.entrypoint')">
        <el-input v-model="entrypoint" :placeholder="t('container.create.entrypointHint')" />
      </el-form-item>

      <!-- 三个编辑器自带行号校验与重复项提示；这里只把 errorLine 接上来用于禁用提交。 -->
      <el-form-item :label="t('container.create.env')">
        <EnvVarEditor v-model="envEntries" v-model:error-line="envError" />
      </el-form-item>

      <el-form-item :label="t('container.create.ports')">
        <PortMappingEditor v-model="portEntries" v-model:error-line="portError" />
      </el-form-item>

      <el-form-item :label="t('container.create.volumes')">
        <VolumeMountEditor
          v-model="volumeEntries"
          v-model:error-line="volumeError"
          :host-id="hostId"
        />
      </el-form-item>

      <el-collapse>
        <el-collapse-item :title="t('container.create.basic')" name="basic">
          <div class="grid gap-x-4 sm:grid-cols-2">
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
            <el-form-item :label="t('container.create.hostname')">
              <el-input v-model="hostname" clearable />
            </el-form-item>
            <el-form-item :label="t('container.create.workingDir')">
              <el-input v-model="workingDir" clearable />
            </el-form-item>
            <el-form-item :label="t('container.action.create')">
              <div class="flex items-center gap-5 pt-1.5">
                <el-checkbox v-model="autoRemove">{{ t('container.create.autoRemove') }}</el-checkbox>
                <el-checkbox v-model="tty">{{ t('container.create.tty') }}</el-checkbox>
                <el-checkbox v-model="stdinOpen">{{ t('container.create.stdinOpen') }}</el-checkbox>
              </div>
            </el-form-item>
          </div>
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

    <template #footer>
      <div v-if="createdContainerId" class="flex justify-end gap-2">
        <el-button @click="createOnly">{{ t('common.close') }}</el-button>
        <el-button type="primary" :loading="submitting" @click="createAndStart">
          {{ t('container.create.startNow') }}
        </el-button>
      </div>
      <div v-else class="flex justify-end gap-2">
        <el-button @click="visible = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
          {{ submitting ? t('container.create.submitting') : t('container.create.submit') }}
        </el-button>
      </div>
    </template>
  </el-dialog>
</template>