<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { createHostGroup, updateHostGroup } from '@/api/host-group'
import type { HostGroup } from '@/types/api'

/**
 * 新建 / 重命名分组对话框（0.0.13）。
 *
 * 一个组件管两种模式：新建与重命名共享「名称 + 描述」两个字段，
 * 差别只是提交到哪个端点。
 *
 * **排序权重刻意不进界面**：它是一个纯技术性的调序手段，
 * 管理员真正想要的是「把这条拖到上面」，而拖拽排序排在后面。
 * 现在给出数字输入只会让人以为必须想清楚一个数，而绝大多数分组用默认值就够。
 */
const props = defineProps<{
  /** 编辑目标；`null` 表示新建 */
  group: HostGroup | null
}>()

const visible = defineModel<boolean>({ required: true })
const emit = defineEmits<{ saved: [] }>()

const { t } = useI18n()

const isEdit = computed(() => props.group !== null)

const name = ref('')
const description = ref('')
const submitting = ref(false)
const errorMessage = ref<string | null>(null)

const canSubmit = computed(() => !submitting.value && name.value.trim().length > 0)

watch(visible, (open) => {
  if (!open) return

  errorMessage.value = null
  name.value = props.group?.name ?? ''
  description.value = props.group?.description ?? ''
})

async function submit(): Promise<void> {
  if (!canSubmit.value) return

  submitting.value = true
  errorMessage.value = null
  try {
    if (props.group !== null) {
      await updateHostGroup(props.group.id, {
        name: name.value.trim(),
        // 空串表示清空——凭据那边需要额外的 clearCredentials 开关，
        // 描述不需要：为空是无害的，一个空串就够表达
        description: description.value.trim()
      })
    } else {
      await createHostGroup({
        name: name.value.trim(),
        description: description.value.trim() || undefined
      })
    }

    ElMessage({ type: 'success', message: t('host.group.saved') })
    emit('saved')
  } catch (e) {
    // 1304（分组名已被占用）落在这里，文案由错误码映射给出
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="isEdit ? t('host.group.editTitle') : t('host.group.createTitle')"
    width="min(480px, 92vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent>
      <el-form-item :label="t('host.group.name')" required>
        <el-input
          v-model="name"
          :placeholder="t('host.group.namePlaceholder')"
          :disabled="submitting"
          @keyup.enter="submit"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('host.group.nameHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('host.group.description')">
        <el-input
          v-model="description"
          type="textarea"
          :rows="2"
          :placeholder="t('host.group.descriptionPlaceholder')"
          :disabled="submitting"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('host.group.descriptionHint') }}</p>
      </el-form-item>

      <el-alert
        v-if="errorMessage"
        class="mt-3"
        type="error"
        :closable="false"
        :title="errorMessage"
      />
    </el-form>

    <template #footer>
      <el-button :disabled="submitting" @click="visible = false">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ isEdit ? t('common.save') : t('common.confirm') }}
      </el-button>
    </template>
  </el-dialog>
</template>
