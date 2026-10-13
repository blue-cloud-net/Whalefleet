<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '@/stores/auth'

/**
 * 顶栏用户菜单：当前用户名 + 角色标签 + 登出。
 *
 * 主题与语言仍留在顶栏的独立控件里（它们切换频繁，藏进下拉多一层点击）；
 * 「修改密码」属 0.0.13 的账号管理，届时加在下拉里。
 */

const router = useRouter()
const { t } = useI18n()
const auth = useAuthStore()

/**
 * 登出：撤销服务端刷新令牌，然后回登录页。
 *
 * 本地会话在 `auth.logout()` 内**先**被清空，因此即使撤销请求失败，
 * 也不会停留在"看起来还登录着"的界面。
 */
async function onLogout() {
  await auth.logout()
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
        <el-dropdown-item disabled>{{ auth.user?.role ?? '—' }}</el-dropdown-item>
        <el-dropdown-item divided @click="onLogout">
          {{ t('auth.logout') }}
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>
