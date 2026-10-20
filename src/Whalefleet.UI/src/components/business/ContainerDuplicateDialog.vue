<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { duplicateContainer } from '@/api/container'
import { isValidContainerName } from '@/utils/advanced-input'

/**
 * 容器复制对话框（0.3.0）。
 *
 * ## 为什么这里的提示语不带任何警告语气
 * 复制是**纯增量**操作：新建一个配置相同的兄弟容器，原容器毫发无损
 * （不停止、不改名、不删除）。所以确认框不需要像「重建」那样警告——
 * 反过来说，若这里写成警告，用户会以为复制有风险而不敢用，
 * 而它其实比删除安全得多。
 *
 * ⚠️ 真正的警告在 `ContainerAddVolumesDialog` 里（那个操作会替换容器）。
 * 两个对话框长得像、语义却相反，因此文案必须区分清楚，不能互相复用。
 */
const { t } = useI18n()

const props = defineProps<{
  hostId: string
  containerId: string | null
  /** 源容器名，用作默认名「原名 + `-copy`」 */
  currentName: string
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ duplicated: [containerId: string] }>()

const name = ref('')
const start = ref(false)
const submitting = ref(false)
const nameError = ref<string | null>(null)

/** 留空表示「用默认名」，由后端按「原名 + `-copy`」并在冲突时递增。 */
const useDefaultName = computed(() => name.value.trim().length === 0)

const canSubmit = computed(() => !submitting.value)

watch(visible, (open) => {
  if (!open) return

  name.value = ''
  start.value = false
  nameError.value = null
})

watch(name, () => {
  nameError.value = null
})

async function submit(): Promise<void> {
  const target = name.value.trim()

  if (target.length > 0 && !isValidContainerName(target)) {
    nameError.value = t('container.duplicate.invalid')
    return
  }

  submitting.value = true

  try {
    const result = await duplicateContainer(props.hostId, props.containerId ?? '', {
      ...(target.length > 0 ? { name: target } : {}),
      start: start.value
    })

    visible.value = false
    ElMessage({ type: 'success', message: t('container.duplicate.done', { name: result.name }) })
    emit('duplicated', result.containerId)
  } catch {
    // 错误由全局拦截器提示；对话框保持打开。
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('container.duplicate.title')"
    :width="460"
    @close="visible = false"
  >
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="t('container.duplicate.nameLabel')" :error="nameError ?? undefined">
        <el-input v-model="name" :placeholder="t('container.duplicate.namePlaceholder', { name: currentName })" />
      </el-form-item>

      <el-form-item>
        <el-checkbox v-model="start">
          {{ t('container.duplicate.start') }}
        </el-checkbox>
      </el-form-item>

      <p class="text-xs text-content-muted">
        {{ useDefaultName ? t('container.duplicate.defaultName', { name: currentName }) : '' }}
        {{ t('container.duplicate.hint') }}
      </p>
    </el-form>

    <template #footer>
      <el-button @click="visible = false">
        {{ t('common.cancel') }}
      </el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ t('container.duplicate.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
