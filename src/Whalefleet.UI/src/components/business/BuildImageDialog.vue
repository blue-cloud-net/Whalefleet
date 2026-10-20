<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { buildImage } from '@/api/image'
import { isTarArchive, parseKeyValueJson, validateBuildTag } from '@/utils/image-build'
import type { ImageBuildResult } from '@/types/api'

/**
 * 构建镜像对话框（0.4.0）。
 *
 * ## 为什么构建上下文是「上传一个 tar」
 * 后端只收**已打包**的 tar，不收多文件再由服务端打包：后者要在服务端实现一套
 * tar 写入器，并配套路径穿越防护（tar 条目名可以含 `../`），而收益仅是界面少一步。
 * 用户 `tar -cf context.tar .` 一次，比在文件选择器里逐个点选更快也更可控
 * （能精确控制 .dockerignore 的效果）。
 *
 * ## 为什么必须显示构建日志
 * 构建失败时守护进程只回一行 `returned a non-zero code: 1`，真正的原因
 * （`COPY failed: no such file`）在它上面几百行里。后端保留最后 500 行并随
 * **成功响应**带回；失败时只有 `1806` 的错误消息（含第一条原因）。
 *
 * ## 为什么等这么久还不能取消
 * 本版构建是**一次性请求**（与 `pull` 同形态），服务端在请求内同步等完，
 * 进度推送排到 0.5.0 的 SignalR。因此这里只能给不确定态，并在文案里
 * 明说「可能要几分钟」——否则用户会在第 30 秒以为卡死而重复提交。
 */
const props = defineProps<{
  /** 当前主机 id（等于主机名） */
  hostId: string
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ built: [ImageBuildResult] }>()

const { t } = useI18n()

const contextFile = ref<File | null>(null)
const tag = ref('')
const dockerfile = ref('Dockerfile')
const buildArgs = ref('')
const labels = ref('')
const target = ref('')
const platform = ref('')
const noCache = ref(false)
const showAdvanced = ref(false)

const submitting = ref(false)
const errorMessage = ref<string | null>(null)
/** 上一次成功构建的结果（含日志），用于构建后展示 */
const lastResult = ref<ImageBuildResult | null>(null)

const fileInput = ref<HTMLInputElement | null>(null)

/** 前端能提前发现的问题；`null` 表示可以提交。 */
const validationError = computed<string | null>(() => {
  if (contextFile.value === null) return t('image.build.error.contextRequired')

  const tagError = validateBuildTag(tag.value)
  if (tagError !== null) return t(tagError)

  const args = parseKeyValueJson(buildArgs.value)
  if (!args.ok) return `${t('image.build.buildArgs')}：${t(args.errorKey)}`

  const labelResult = parseKeyValueJson(labels.value)
  if (!labelResult.ok) return `${t('image.build.labels')}：${t(labelResult.errorKey)}`

  return null
})

const canSubmit = computed(() => !submitting.value && validationError.value === null)

watch(visible, (open) => {
  if (!open) return

  contextFile.value = null
  tag.value = ''
  dockerfile.value = 'Dockerfile'
  buildArgs.value = ''
  labels.value = ''
  target.value = ''
  platform.value = ''
  noCache.value = false
  showAdvanced.value = false
  errorMessage.value = null
  lastResult.value = null
})

function pickFile(): void {
  fileInput.value?.click()
}

function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0] ?? null

  // 清空 input 的 value：否则选中同一个文件不会触发 change（第二次重新选它就毫无反应）
  input.value = ''

  if (file === null) return

  // 扩展名不对时**不静默接受**：Docker 只认 tar，zip 是 Windows 上最容易打错的一种，
  // 放过去要等服务端报「不是合法 tar」才知道。
  if (!isTarArchive(file.name)) {
    errorMessage.value = t('image.build.error.notTar', { name: file.name })
    contextFile.value = null
    return
  }

  errorMessage.value = null
  contextFile.value = file

  // Dockerfile 路径的默认值只给一次：用户改成 `docker/Dockerfile` 之后
  // 再选一个 tar 不该把它重置回去
  if (dockerfile.value.trim().length === 0) dockerfile.value = 'Dockerfile'
}

async function submit(): Promise<void> {
  if (!canSubmit.value || contextFile.value === null) {
    errorMessage.value = validationError.value
    return
  }

  const args = parseKeyValueJson(buildArgs.value)
  const labelResult = parseKeyValueJson(labels.value)

  // 前面已校验过，这里只是让类型的收窄成立；同时避免把非法 JSON 发出去
  if (!args.ok || !labelResult.ok) return

  submitting.value = true
  errorMessage.value = null
  lastResult.value = null
  try {
    const result = await buildImage(props.hostId, {
      context: contextFile.value,
      tag: tag.value.trim(),
      dockerfile: dockerfile.value.trim() || undefined,
      // 解析后的对象重新序列化：`parseKeyValueJson` 会把数字 / 布尔转成字符串，
      // 直接转发原始文本会让后端收到 `{"RETRIES":3}` 这种不是 Dictionary<string,string>
      // 的东西（可以反序列化，但语义上我们已经在本地规范化了）
      buildArgs: args.value ? JSON.stringify(args.value) : undefined,
      labels: labelResult.value ? JSON.stringify(labelResult.value) : undefined,
      target: target.value.trim() || undefined,
      platform: platform.value.trim() || undefined,
      noCache: noCache.value || undefined
    })

    lastResult.value = result
    ElMessage({
      type: 'success',
      message: t('image.build.done', { reference: result.reference }),
      showClose: true
    })
    // 立刻通知父组件刷新：用户可能直接关掉对话框，而列表该已经能看到新镜像
    emit('built', result)
  } catch (e) {
    // 1806：消息里是守护进程给的第一条原因；HTTP 层并不总是 5xx（构建失败时
    // 守护进程的响应是 200 + 进度流里的错误，由后端翻译）
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}

function close(): void {
  visible.value = false
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('image.build.title')"
    width="min(640px, 94vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="t('image.build.context')" required>
        <!-- 用隐藏的原生 input 而不是 el-upload：只需要「选一个文件」，
             el-upload 的文件列表与自动上传语义在这里都要额外关掉，反而更容易出错 -->
        <input
          ref="fileInput"
          type="file"
          accept=".tar,.tar.gz,.tgz"
          class="hidden"
          @change="onFileChange"
        />
        <div class="flex items-center gap-2 w-full">
          <el-button :disabled="submitting" @click="pickFile">
            {{ t('image.build.contextPick') }}
          </el-button>
          <span class="app-truncate font-mono text-xs text-content-body">
            {{ contextFile?.name ?? t('image.build.contextNone') }}
          </span>
        </div>
        <p class="mt-1 text-xs text-content-muted">{{ t('image.build.contextHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('image.build.tag')" required>
        <el-input
          v-model="tag"
          :placeholder="t('image.build.tagPlaceholder')"
          :disabled="submitting"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('image.build.tagHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('image.build.dockerfile')">
        <el-input v-model="dockerfile" :disabled="submitting" />
        <p class="mt-1 text-xs text-content-muted">{{ t('image.build.dockerfileHint') }}</p>
      </el-form-item>

      <el-collapse-transition>
        <div v-show="showAdvanced">
          <el-form-item :label="t('image.build.buildArgs')">
            <el-input
              v-model="buildArgs"
              type="textarea"
              :rows="3"
              :placeholder='{"VERSION":"1.0"}'
              :disabled="submitting"
            />
            <p class="mt-1 text-xs text-content-muted">{{ t('image.build.buildArgsHint') }}</p>
          </el-form-item>

          <el-form-item :label="t('image.build.labels')">
            <el-input
              v-model="labels"
              type="textarea"
              :rows="3"
              :placeholder='{"org.opencontainers.image.title":"myapp"}'
              :disabled="submitting"
            />
            <p class="mt-1 text-xs text-content-muted">{{ t('image.build.labelsHint') }}</p>
          </el-form-item>

          <div class="grid gap-3 sm:grid-cols-2">
            <el-form-item :label="t('image.build.target')">
              <el-input
                v-model="target"
                :placeholder="t('image.build.targetPlaceholder')"
                :disabled="submitting"
              />
            </el-form-item>

            <el-form-item :label="t('image.build.platform')">
              <el-input
                v-model="platform"
                :placeholder="t('image.build.platformPlaceholder')"
                :disabled="submitting"
              />
            </el-form-item>
          </div>

          <el-form-item :label="t('image.build.noCache')">
            <el-switch v-model="noCache" :disabled="submitting" />
            <p class="mt-1 text-xs text-content-muted">{{ t('image.build.noCacheHint') }}</p>
          </el-form-item>
        </div>
      </el-collapse-transition>

      <el-button link type="primary" :disabled="submitting" @click="showAdvanced = !showAdvanced">
        {{ showAdvanced ? t('image.build.advancedHide') : t('image.build.advanced') }}
      </el-button>

      <el-alert
        v-if="errorMessage"
        class="mt-3"
        type="error"
        :closable="false"
        :title="errorMessage"
      />

      <!-- 成功后的构建日志：默认折叠。成功时日志很少需要看，但真要排查
           「缓存是不是生效了」时它是唯一的信息来源。 -->
      <div v-if="lastResult" class="mt-3">
        <el-alert
          type="success"
          :closable="false"
          :title="t('image.build.logTitle', { count: lastResult.buildLog.length })"
        />
        <pre
          v-if="lastResult.buildLog.length > 0"
          class="mt-2 max-h-56 overflow-auto rounded-md bg-surface-sunken p-3 text-xs font-mono text-content-body whitespace-pre-wrap"
          >{{ lastResult.buildLog.join('\n') }}</pre
        >
      </div>

      <p v-if="submitting" class="mt-3 text-xs text-content-muted">
        {{ t('image.build.waiting') }}
      </p>
    </el-form>

    <template #footer>
      <el-button :disabled="submitting" @click="close">{{ t('common.close') }}</el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ submitting ? t('image.build.submitting') : t('image.build.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
