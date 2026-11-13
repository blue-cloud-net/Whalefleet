<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'
import { updatePassword } from '@/api/user'
import { useAuthStore } from '@/stores/auth'

/**
 * 强制改密页（0.13.0）。
 *
 * ## 为什么它是独立页面而不是对话框
 * 服务端在强制改密期间只放行白名单端点（`auth/me` / `auth/refresh` /
 * `auth/logout` / `users/update-password`），其余一律 `1205`。
 * 也就是说，此刻**整个应用都点不动**——侧边栏里每一个入口都会失败。
 * 因此这一页不套主布局（无侧边栏、无顶栏）：摆一个点不动的导航栏在那里
 * 只会让人反复去试。
 *
 * ## 为什么它必须能退出
 * 「登出」在白名单里：用户改不了密码时仍然可以登出走人，
 * 否则一个被要求改密的人会被困在这个页里。
 */
const { t } = useI18n()
const router = useRouter()
const auth = useAuthStore()

const oldPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const saving = ref(false)
const error = ref<string | null>(null)

async function submit(): Promise<void> {
  error.value = null

  if (newPassword.value !== confirmPassword.value) {
    error.value = t('password.change.mismatch')
    return
  }

  saving.value = true
  try {
    // 省略 userId = 改自己的密码，因此**必须**提供原密码。
    await updatePassword({
      oldPassword: oldPassword.value,
      newPassword: newPassword.value
    })

    // 改密会撤销该用户的全部会话（服务端行为），因此本地令牌已失效。
    // 重新登录是唯一正确的后续动作——留着旧令牌只会一路收到 401。
    auth.clearSession()
    ElMessage.success(t('password.change.done'))
    await router.replace({ name: 'login' })
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    saving.value = false
  }
}

async function logout(): Promise<void> {
  await auth.logout()
  await router.replace({ name: 'login' })
}
</script>

<template>
  <div class="min-h-screen flex items-center justify-center bg-page px-4 py-10">
    <div class="w-full max-w-md">
      <div class="app-card p-6 sm:p-8">
        <h1 class="text-lg font-semibold text-content-strong">
          {{ t('password.change.title') }}
        </h1>
        <p class="mt-2 text-sm text-content-muted">
          {{ t('password.change.description') }}
        </p>

        <form class="mt-6 space-y-4" @submit.prevent="submit">
          <div>
            <label class="block text-xs text-content-muted mb-1" for="cp-old">
              {{ t('password.change.oldPassword') }}
            </label>
            <el-input
              id="cp-old"
              v-model="oldPassword"
              type="password"
              show-password
              autocomplete="current-password"
            />
          </div>

          <div>
            <label class="block text-xs text-content-muted mb-1" for="cp-new">
              {{ t('password.change.newPassword') }}
            </label>
            <el-input
              id="cp-new"
              v-model="newPassword"
              type="password"
              show-password
              autocomplete="new-password"
            />
          </div>

          <div>
            <label class="block text-xs text-content-muted mb-1" for="cp-confirm">
              {{ t('password.change.confirmPassword') }}
            </label>
            <el-input
              id="cp-confirm"
              v-model="confirmPassword"
              type="password"
              show-password
              autocomplete="new-password"
            />
          </div>

          <p v-if="error" role="alert" class="text-sm text-status-danger">
            {{ error }}
          </p>

          <div class="flex items-center gap-2 pt-1">
            <el-button type="primary" native-type="submit" :loading="saving">
              {{ t('password.change.submit') }}
            </el-button>
            <el-button :disabled="saving" @click="logout">
              {{ t('common.logout') }}
            </el-button>
          </div>
        </form>
      </div>
    </div>
  </div>
</template>
