<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import ChangePasswordDialog from '@/components/business/ChangePasswordDialog.vue'
import { useAuthStore } from '@/stores/auth'
import { roleLabelKey } from '@/utils/roles'

/**
 * 顶栏用户菜单：当前用户名 + 角色标签 + 修改密码 + 登出。
 *
 * 主题与语言仍留在顶栏的独立控件里（它们切换频繁，藏进下拉多一层点击）；
 * **修改密码**放这里——它是账号级操作，一年用不了几次，正适合收进下拉。
 */

const router = useRouter()
const { t } = useI18n()
const auth = useAuthStore()

const changePasswordVisible = ref(false)

/**
 * 角色展示文本。
 *
 * 一个用户可以持多个角色，因此这里是**数组**——只显示第一个会让「他为什么能点这个」
 * 在界面上找不到依据。内置角色走 i18n，自定义角色原样显示标识。
 */
const roleText = computed(() => {
  const roles = auth.user?.roles ?? []
  if (roles.length === 0) return t('user.role.none')

  return roles
    .map((role) => {
      const key = roleLabelKey(role)

      return key ? t(key) : role
    })
    .join(' · ')
})

/**
 * 登出：撤销服务端刷新令牌，然后回登录页。
 *
 * 本地会话在 `auth.logout()` 内**先**被清空，因此即使撤销请求失败，
 * 也不会停留在「看起来还登录着」的界面。
 */
async function onLogout() {
  await auth.logout()
  await router.push({ name: 'login' })
}

/**
 * 改密成功后的收尾。
 *
 * 后端在改密时撤销了该用户全部刷新令牌，因此本地会话**必须**一并结束：
 * 留着已登录的样子，用户会在下一次请求时被毫无征兆地踢回登录页。
 * 这里主动清会话并跳登录页，同时把「请用新密码登录」说清楚。
 */
async function onPasswordChanged() {
  changePasswordVisible.value = false
  auth.clearSession()
  ElMessage({
    type: 'success',
    message: t('user.change.done'),
    showClose: true,
    duration: 5000
  })
  await router.push({ name: 'login' })
}
</script>

<template>
  <el-dropdown trigger="click">
    <el-button text class="!px-2 flex items-center gap-1.5">
      <span
        class="w-7 h-7 rounded-full grid place-items-center text-base shrink-0 bg-brand-500/12 text-brand-600 dark:text-brand-400"
      >
        <span class="i-carbon-user-avatar" />
      </span>
      <span class="hidden sm:inline text-sm text-content-body app-truncate max-w-24">
        <!-- 理论上登录后必有 user；恢复失败时用占位符，别硬编码成 admin 冒充身份 -->
        {{ auth.user?.username ?? '—' }}
      </span>
    </el-button>

    <template #dropdown>
      <el-dropdown-menu>
        <el-dropdown-item disabled>{{ roleText }}</el-dropdown-item>
        <el-dropdown-item divided @click="changePasswordVisible = true">
          {{ t('user.change.title') }}
        </el-dropdown-item>
        <el-dropdown-item divided @click="onLogout">
          {{ t('auth.logout') }}
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>

  <ChangePasswordDialog v-model="changePasswordVisible" @changed="onPasswordChanged" />
</template>
