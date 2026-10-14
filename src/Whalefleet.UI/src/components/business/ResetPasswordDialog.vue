<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { updatePassword } from '@/api/user'
import type { AdminUserSummary } from '@/types/api'

/**
 * 重置密码对话框（管理员对他人，0.0.13）。
 *
 * ## 为什么与「修改自己的密码」分成两个对话框
 * 共用的是**同一个后端端点**（`admin/users/update-password`），差别只在语义：
 * 指定 `userId` = 管理员重置（不需要原密码），省略 = 自助改密（必须验原密码）。
 * 拆成两个组件是因为两者的**输入项与心理模型完全不同**：
 * 「证明我是我」与「替忘记密码的同事恢复」不是一回事，塞进一个表单会出现
 * 「为什么这里要我填原密码、那里不用」的解释负担。
 *
 * ## 为什么必须提示「会踢下线」
 * 改密码后该用户**全部刷新令牌被撤销**。不说清楚，管理员会以为重置没生效
 * （对方下次点任何东西就被弹回登录页），而对方会以为系统坏了。
 */
const props = defineProps<{
  /** 目标用户；为 null 时不显示 */
  user: AdminUserSummary | null
}>()

const visible = defineModel<boolean>({ required: true })
const emit = defineEmits<{ updated: [] }>()

const { t } = useI18n()

const password = ref('')
const submitting = ref(false)
const errorMessage = ref<string | null>(null)

const passwordIssue = computed(() => {
  if (password.value.length === 0) return null

  return isStrongPassword(password.value) ? null : t('user.form.passwordWeak')
})

const canSubmit = computed(
  () => !submitting.value && password.value.length > 0 && passwordIssue.value === null
)

watch(visible, (open) => {
  if (!open) return

  password.value = ''
  errorMessage.value = null
})

function isStrongPassword(value: string): boolean {
  return value.length >= 10 && /[A-Za-z]/.test(value) && /[0-9]|[^A-Za-z0-9]/.test(value)
}

async function submit(): Promise<void> {
  if (!canSubmit.value || props.user === null) return

  submitting.value = true
  errorMessage.value = null
  try {
    await updatePassword({ userId: props.user.id, newPassword: password.value })

    ElMessage({
      type: 'success',
      message: t('user.reset.done', { name: props.user.username }),
      showClose: true
    })
    emit('updated')
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('user.reset.title')"
    width="min(480px, 92vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <p class="mb-4 text-sm text-content-body">
      {{ t('user.reset.for', { name: user?.username ?? '—' }) }}
    </p>

    <el-form label-position="top" @submit.prevent>
      <el-form-item :label="t('user.form.newPassword')" required>
        <el-input
          v-model="password"
          type="password"
          show-password
          autocomplete="new-password"
          :placeholder="t('user.form.passwordPlaceholder')"
          :disabled="submitting"
          @keyup.enter="submit"
        />
        <p v-if="passwordIssue" class="mt-1 text-xs text-status-down">{{ passwordIssue }}</p>
        <p v-else class="mt-1 text-xs text-content-muted">{{ t('user.form.passwordHint') }}</p>
      </el-form-item>

      <el-alert
        class="mt-1"
        type="warning"
        :closable="false"
        :title="t('user.reset.warning')"
      />

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
        {{ t('user.reset.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
