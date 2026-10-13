<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import {
  createContainer,
  startContainer,
  type CreateContainerPayload
} from '@/api/container'

/**
 * 新建容器对话框（0.0.11）。
 *
 * ## 为什么端口 / 挂载 / 环境变量用「每行一条」的文本框而不是三个联动输入框
 * 真实的 `docker run` 命令就是这种行式输入，而嵌套表单控件（端口要两行、协议要下拉、
 * 还要能加第二个端口）在一行里塞不下、读起来还比命令行难懂。**降低输入成本**在这里
 * 比「结构化表单好看」更重要：用户绝大多数情况是把一条现成的命令行粘进来。
 *
 * 代价是要自己解析并在**提交前**校验（不能靠后端的 400，那只会得到一句
 * "invalid parameter"）。因此解析结果与错误行号都直接显示在输入框下方。
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
const envText = ref('')
const portText = ref('')
const volumeText = ref('')
const restartPolicy = ref('no')
const autoRemove = ref(false)
const network = ref('')
const hostname = ref('')
const workingDir = ref('')
const tty = ref(false)
const stdinOpen = ref(false)

/** 解析出来的字段级错误：key 是字段名，value 是错误 key（交由 t() 取文案）。 */
const fieldErrors = ref<Record<string, string>>({})

const canSubmit = computed(() => image.value.trim().length > 0 && !submitting.value)

function reset(): void {
  image.value = ''
  name.value = ''
  cmd.value = ''
  entrypoint.value = ''
  envText.value = ''
  portText.value = ''
  volumeText.value = ''
  restartPolicy.value = 'no'
  autoRemove.value = false
  network.value = ''
  hostname.value = ''
  workingDir.value = ''
  tty.value = false
  stdinOpen.value = false
  fieldErrors.value = {}
  createdContainerId.value = null
  warnings.value = []
}

// 每次打开都清空上一次的残留：否则用户上次填到一半关掉，下次打开会看到一堆他不想要的值。
watch(visible, (open) => {
  if (open) reset()
})

/**
 * 解析「每行一条」的文本为非空行数组。
 *
 * 刻意**不丢空行语义**：空行只是被忽略（用户粘贴多行命令时难免带上空行），
 * 但每一项的**行号**要保留在解析结果里，错误提示才能指到具体那一行。
 */
function parseLines(text: string): Array<{ line: number; value: string }> {
  return text
    .split('\n')
    .map((raw, index) => ({ line: index + 1, value: raw.trim() }))
    .filter((entry) => entry.value.length > 0)
}

/**
 * 解析环境变量：每行必须是 `KEY=VALUE`。
 *
 * 后端也会校验这条（返回 1000），但它只会说「环境变量xxx不是 KEY=VALUE」，
 * 没有行号。这里提前拦下并指出第几行，用户不用自己数。
 */
function parseEnv(): { values: string[]; errorLine: number | null } {
  const values: string[] = []

  for (const { line, value } of parseLines(envText.value)) {
    const separator = value.indexOf('=')

    if (separator <= 0) {
      return { values: [], errorLine: line }
    }

    values.push(value)
  }

  return { values, errorLine: null }
}

/**
 * 解析端口映射：`容器端口[:宿主端口][/协议]`。
 *
 * 三种写法都要支持，因为它们在实际使用中出现频率接近：
 * - `80` —— 只暴露不绑定
 * - `80:8080` —— 指定宿主端口
 * - `53:53/udp` —— 指定协议
 *
 * 端口号范围在这里校验（1–65535）：后端也会校验，但它的消息是中文且不带行号，
 * 而这里能精确指出「第 2 行」并顺带告诉用户合法范围。
 */
function parsePorts(): {
  ports: NonNullable<CreateContainerPayload['ports']>
  errorLine: number | null
} {
  const ports: NonNullable<CreateContainerPayload['ports']> = []

  for (const { line, value } of parseLines(portText.value)) {
    const [addressPart, protocolPart] = value.split('/')
    const protocol = protocolPart?.trim().toLowerCase()

    if (protocol !== undefined && protocol !== 'tcp' && protocol !== 'udp') {
      return { ports: [], errorLine: line }
    }

    const [privatePart, publicPart] = addressPart.split(':')

    const privatePort = Number(privatePart)
    if (!Number.isInteger(privatePort) || privatePort < 1 || privatePort > 65535) {
      return { ports: [], errorLine: line }
    }

    // 宿主端口可省略（省略即「只暴露不绑定」），给了就必须合法。
    const hostPort = publicPart === undefined ? undefined : Number(publicPart)
    if (hostPort !== undefined && (!Number.isInteger(hostPort) || hostPort < 1 || hostPort > 65535)) {
      return { ports: [], errorLine: line }
    }

    ports.push({ containerPort: privatePort, hostPort, ...(protocol ? { type: protocol } : {}) })
  }

  return { ports, errorLine: null }
}

/**
 * 解析挂载：`来源:容器内路径[:ro]`。
 *
 * 来源既可以是宿主路径也可以是具名卷——Docker 自己根据它判定类型，
 * 因此表单里**不**要求用户额外选「bind 还是 volume」。
 *
 * ⚠️ Windows 路径含冒号（`C:\data:/data`）。这里的处理是**只按第一个和最后一个冒号切**，
 * 因此 `C:\data` 会被当成完整来源而不是被切成 `C` 与 `\data`。
 */
function parseVolumes(): {
  volumes: NonNullable<CreateContainerPayload['volumes']>
  errorLine: number | null
} {
  const volumes: NonNullable<CreateContainerPayload['volumes']> = []

  for (const { line, value } of parseLines(volumeText.value)) {
    const readOnly = value.endsWith(':ro')
    const body = readOnly ? value.slice(0, -3) : value

    const firstColon = body.indexOf(':')
    const lastColon = body.lastIndexOf(':')

    // 少于两个冒号说明缺「容器内路径」；多于两个说明格式里有额外冒号（除了 Windows 盘符那种情况）。
    if (firstColon <= 0 || lastColon === firstColon) {
      return { volumes: [], errorLine: line }
    }

    volumes.push({
      source: body.slice(0, firstColon),
      destination: body.slice(firstColon + 1, lastColon === firstColon ? undefined : lastColon),
      readOnly
    })
  }

  return { volumes, errorLine: null }
}

/** 容器名的合法性规则与后端一致（见其 `RequireContainerName`）。 */
function validateName(value: string): boolean {
  if (value.trim().length === 0) return true

  const trimmed = value.trim()

  return /^[A-Za-z0-9][A-Za-z0-9_.-]*$/.test(trimmed)
}

async function submit(): Promise<void> {
  const env = parseEnv()
  const ports = parsePorts()
  const volumes = parseVolumes()
  const errors: Record<string, string> = {}

  if (!validateName(name.value)) errors.name = t('container.create.nameInvalid')
  if (env.errorLine !== null) errors.env = t('container.create.envInvalid', { line: env.errorLine })
  if (ports.errorLine !== null) errors.ports = t('container.create.portInvalid', { line: ports.errorLine })
  if (volumes.errorLine !== null) errors.volumes = t('container.create.volumeInvalid', { line: volumes.errorLine })

  fieldErrors.value = errors
  if (Object.keys(errors).length > 0) return

  const payload: CreateContainerPayload = {
    image: image.value.trim(),
    ...(name.value.trim() ? { name: name.value.trim() } : {}),
    ...(cmd.value.trim() ? { cmd: splitArgs(cmd.value) } : {}),
    ...(entrypoint.value.trim() ? { entrypoint: splitArgs(entrypoint.value) } : {}),
    ...(env.values.length > 0 ? { env: env.values } : {}),
    ...(ports.ports.length > 0 ? { ports: ports.ports } : {}),
    ...(volumes.volumes.length > 0 ? { volumes: volumes.volumes } : {}),
    restartPolicy: restartPolicy.value,
    autoRemove: autoRemove.value,
    ...(network.value.trim() ? { network: network.value.trim() } : {}),
    ...(hostname.value.trim() ? { hostname: hostname.value.trim() } : {}),
    ...(workingDir.value.trim() ? { workingDir: workingDir.value.trim() } : {})
  }

  if (tty.value) payload.tty = true
  if (stdinOpen.value) payload.stdinOpen = true

  submitting.value = true
  try {
    const result = await createContainer(props.hostId, payload)

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

      <el-form-item :label="t('container.create.env')" :error="fieldErrors.env">
        <el-input v-model="envText" type="textarea" :rows="3" :placeholder="t('container.create.envHint')" />
      </el-form-item>

      <el-form-item :label="t('container.create.ports')" :error="fieldErrors.ports">
        <el-input v-model="portText" type="textarea" :rows="2" :placeholder="t('container.create.portHint')" />
      </el-form-item>

      <el-form-item :label="t('container.create.volumes')" :error="fieldErrors.volumes">
        <el-input v-model="volumeText" type="textarea" :rows="2" :placeholder="t('container.create.volumeHint')" />
      </el-form-item>

      <el-collapse>
        <el-collapse-item :title="t('container.create.basic')" name="basic">
          <div class="grid gap-x-4 sm:grid-cols-2">
            <el-form-item :label="t('container.create.network')">
              <el-input v-model="network" clearable />
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