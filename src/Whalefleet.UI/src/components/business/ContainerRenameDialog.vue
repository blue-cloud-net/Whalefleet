<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { renameContainer } from '@/api/container'
import { isValidContainerName } from '@/utils/advanced-input'

/**
 * 容器重命名对话框（0.3.0）。
 *
 * ## 为什么前端也要校验一遍
 * 名称规则（字母/数字开头，其后含 `_ . -`）后端一定会拒（`1000`），
 * 但那时用户已经填完并提交了。前端提前拦是**为了把提示落到输入框上**，
 * 而不是让一个填错的名字弹到页面顶部的全局错误条里。
 *
 * ⚠️ 规则本身收在 `utils/advanced-input.ts`，与复制对话框共用一份——
 * 两处各写一遍正则，迟早只改一处，而漏掉的那处会让同一个名字在两个对话框里判定不一致。
 */
const { t } = useI18n()

const props = defineProps<{
  hostId: string
  /** 待重命名的容器 id */
  containerId: string | null
  /** 容器当前名字（作为输入框初值，也是「有没有真的改名」的判据） */
  currentName: string
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ renamed: [] }>()

const name = ref('')
const submitting = ref(false)

/** 输入级错误：null 表示无错，否则是一段可直接展示的原因。 */
const nameError = ref<string | null>(null)

const canSubmit = computed(() => name.value.trim().length > 0 && !submitting.value)

watch(visible, (open) => {
  if (!open) return

  name.value = props.currentName
  nameError.value = null
})

watch(name, () => {
  nameError.value = null
})

async function submit(): Promise<void> {
  const target = name.value.trim()

  if (!isValidContainerName(target)) {
    nameError.value = t('container.rename.invalid')
    return
  }

  // 与当前名字相同就没什么可改的：拦住它省掉一次必然 200 但毫无变化的请求
  if (target === props.currentName) {
    visible.value = false
    return
  }

  submitting.value = true

  try {
    const result = await renameContainer(props.hostId, props.containerId ?? '', target)

    visible.value = false
    ElMessage({ type: 'success', message: t('container.rename.done', { name: result.name }) })
    emit('renamed')
  } catch {
    // 错误消息由全局拦截器统一提示（名称已被占用是 1404，它有专属文案）；
    // 这里只保证对话框不关闭，用户能直接改名字再提交。
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('container.rename.title')"
    :width="440"
    @close="visible = false"
  >
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="t('container.rename.label')" :error="nameError ?? undefined">
        <el-input
          v-model="name"
          :placeholder="t('container.rename.placeholder')"
          autofocus
          @keyup.enter="submit"
        />
      </el-form-item>

      <p class="text-xs text-content-muted">
        {{ t('container.rename.hint') }}
      </p>
    </el-form>

    <template #footer>
      <el-button @click="visible = false">
        {{ t('common.cancel') }}
      </el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ t('common.confirm') }}
      </el-button>
    </template>
  </el-dialog>
</template>
