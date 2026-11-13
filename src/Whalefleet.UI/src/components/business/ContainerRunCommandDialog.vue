<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { buildContainerRunSpec } from '@/api/transfer'
import { saveText } from '@/utils/download'
import type { ContainerRunSpec, ContainerRunSpecFormat } from '@/types/api'

/**
 * 「生成可复制定义」对话框（0.14.0）。
 *
 * ## 为什么是对话框而不是直接复制到剪贴板
 * 生成结果要**看一眼**：它可能很长（几十个参数），也可能含明文环境变量。
 * 直接塞进剪贴板会让用户在不知情的情况下把密码带到别处，
 * 而这里至少让人看到「这东西里有敏感值」。
 *
 * ## 两档一定要有两个标题
 * 「docker run」与「compose 片段」是两种语法，同一个标题会让人分不清自己在看
 * 哪一档；而且**副标题的口径不同**（前者粘进终端、后者粘进 `services:` 之下）。
 */

const props = defineProps<{
  modelValue: boolean
  hostId: string
  containerId: string
  /** 容器名（用在文件名里，让导出的文件可辨认） */
  containerName?: string
}>()

const emit = defineEmits<{ 'update:modelValue': [boolean] }>()

const { t } = useI18n()

const format = ref<ContainerRunSpecFormat>('dockerRun')
const spec = ref<ContainerRunSpec | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

const visible = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value)
})

watch(
  () => [props.modelValue, format.value, props.containerId] as const,
  ([open]) => {
    if (open) {
      void load()
    }
  },
  { immediate: true }
)

/** 拉取生成结果。 */
async function load(): Promise<void> {
  loading.value = true
  error.value = null

  try {
    spec.value = await buildContainerRunSpec(props.hostId, props.containerId, format.value)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
    spec.value = null
  } finally {
    loading.value = false
  }
}

/** 复制到剪贴板。 */
async function copy(): Promise<void> {
  if (!spec.value) {
    return
  }

  try {
    await navigator.clipboard.writeText(spec.value.content)
    ElMessage.success(t('container.runCommand.copied'))
  } catch {
    // 非安全上下文（http + 非 localhost）下剪贴板不可用：
    // 这时不该静默失败，让用户知道要手动选中复制
    ElMessage.warning(t('container.runCommand.copyFailed'))
  }
}

/** 存成文件。 */
function save(): void {
  if (!spec.value) {
    return
  }

  const base = props.containerName || props.containerId.slice(0, 12)
  const fileName = format.value === 'dockerRun' ? `${base}.sh` : `${base}.compose.yaml`

  saveText(spec.value.content, fileName, format.value === 'dockerRun' ? 'text/x-shellscript' : 'text/yaml')
}

/** 各档的副标题（逐档写：两档的用途不同，不能共用一句）。 */
const subtitle = computed(() =>
  format.value === 'dockerRun'
    ? t('container.runCommand.subtitleDockerRun')
    : t('container.runCommand.subtitleCompose')
)
</script>

<template>
  <el-dialog v-model="visible" :title="t('container.runCommand.title')" width="720px" destroy-on-close>
    <div class="flex flex-col gap-3">
      <el-radio-group v-model="format" size="small">
        <el-radio-button value="dockerRun">docker run</el-radio-button>
        <el-radio-button value="composeYaml">compose</el-radio-button>
      </el-radio-group>

      <p class="text-xs text-content-muted">{{ subtitle }}</p>

      <el-alert type="warning" :closable="false" show-icon :title="t('container.runCommand.sensitiveHint')" />

      <div v-if="error" class="rounded border border-danger/40 bg-danger/5 p-3 text-sm">
        {{ error }}
      </div>

      <pre
        v-else
        v-loading="loading"
        class="max-h-80 overflow-auto rounded bg-surface-sunken p-3 text-xs leading-relaxed"
      ><code>{{ spec?.content ?? '' }}</code></pre>
    </div>

    <template #footer>
      <el-button @click="visible = false">{{ t('common.close') }}</el-button>
      <el-button :disabled="!spec" @click="save">{{ t('container.runCommand.save') }}</el-button>
      <el-button type="primary" :disabled="!spec" @click="copy">
        {{ t('container.runCommand.copy') }}
      </el-button>
    </template>
  </el-dialog>
</template>
