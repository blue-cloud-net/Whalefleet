<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { updatePassword } from '@/api/user'

/**
 * 修改自己的密码（0.0.13，从顶栏用户菜单进入）。
 *
 * ## 为什么必须验原密码
 * 只凭一个被盗的 access token 就能改密码的话，攻击者可以把真正的主人锁在门外。
 * 这道校验在**后端**（省略 `userId` 时强制要求 `oldPassword`），前端只是把输入项摆出来。
 *
 * ## 为什么改完要退出登录
 * 后端在改密成功时会**撤销该用户全部刷新令牌**——改密码最常见的动因就是「疑似泄漏」，
 * 不撤销等于把旧凭据留在原地。于是本组件成功后必须让本地会话一并结束：
 * 只弹一句「修改成功」而界面还停在已登录状态，用户会在下一次请求时被突然踢回登录页，
 * 完全不知道刚才发生了什么。这里直接把「请用新密码重新登录」说清楚并跳登录页。
 */
const visible = defineModel<boolean>({ required: true })

/** 改密成功后由父组件（用户菜单）负责清会话并跳登录页。 */
const emit = defineEmits<{ changed: [] }>()

const { t } = useI18n()

const oldPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const submitting = ref(false)
const errorMessage = ref<string | null>(null)

const newPasswordIssue = computed(() => {
  if (newPassword.value.length === 0) return null

  return isStrongPassword(newPassword.value) ? null : t('user.form.passwordWeak')
})

/** 两次输入不一致：只在确认框有内容时提示，边输边报会很吵。 */
const confirmIssue = computed(() => {
  if (confirmPassword.value.length === 0) return null

  return confirmPassword.value === newPassword.value ? null : t('user.form.passwordMismatch')
})

/**
 * 新旧密码相同：后端不会拒绝（它只看强度），但这几乎总是误操作——
 * 用户以为改了、实际没变，下次还得再来一遍。
 */
const sameAsOldIssue = computed(() => {
  if (oldPassword.value.length === 0 || newPassword.value.length === 0) return null

  return oldPassword.value === newPassword.value ? t('user.form.passwordSame') : null
})

const canSubmit = computed(
  () =>
    !submitting.value &&
    oldPassword.value.length > 0 &&
    newPassword.value.length > 0 &&
    confirmPassword.value.length > 0 &&
    newPasswordIssue.value === null &&
    confirmIssue.value === null &&
    sameAsOldIssue.value === null
)

watch(visible, (open) => {
  if (!open) return

  oldPassword.value = ''
  newPassword.value = ''
  confirmPassword.value = ''
  errorMessage.value = null
})

function isStrongPassword(value: string): boolean {
  return value.length >= 10 && /[A-Za-z]/.test(value) && /[0-9]|[^A-Za-z0-9]/.test(value)
}

async function submit(): Promise<void> {
  if (!canSubmit.value) return

  submitting.value = true
  errorMessage.value = null
  try {
    // 刻意**不传 userId**：这就是「改自己」，后端据此要求原密码
    await updatePassword({ oldPassword: oldPassword.value, newPassword: newPassword.value })
    emit('changed')
  } catch (e) {
    // 原密码错误会以 1000/400 回来（**不是 401**——401 会被拦截器当会话过期去刷新重放）
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('user.change.title')"
    width="min(480px, 92vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent>
      <el-form-item :label="t('user.form.oldPassword')" required>
        <el-input
          v-model="oldPassword"
          type="password"
          show-password
          autocomplete="current-password"
          :disabled="submitting"
        />
      </el-form-item>

      <el-form-item :label="t('user.form.newPassword')" required>
        <el-input
          v-model="newPassword"
          type="password"
          show-password
          autocomplete="new-password"
          :placeholder="t('user.form.passwordPlaceholder')"
          :disabled="submitting"
        />
        <p v-if="newPasswordIssue || sameAsOldIssue" class="mt-1 text-xs text-status-down">
          {{ newPasswordIssue ?? sameAsOldIssue }}
        </p>
        <p v-else class="mt-1 text-xs text-content-muted">{{ t('user.form.passwordHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('user.form.confirmPassword')" required>
        <el-input
          v-model="confirmPassword"
          type="password"
          show-password
          autocomplete="new-password"
          :disabled="submitting"
          @keyup.enter="submit"
        />
        <p v-if="confirmIssue" class="mt-1 text-xs text-status-down">{{ confirmIssue }}</p>
      </el-form-item>

      <el-alert class="mt-1" type="warning" :closable="false" :title="t('user.change.warning')" />

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
        {{ t('user.change.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
