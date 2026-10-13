import { defineStore } from 'pinia'
import type { CurrentUser } from '@/types/api'

/**
 * 认证 store：仅存 token + 当前用户信息。
 * 真正的 access/refresh 流程由 0.0.4 后端登录 API 落地后再接；
 * 0.0.5 阶段先用静态占位跑通路由守卫。
 */
export const useAuthStore = defineStore('auth', {
  state: () => ({
    accessToken: '' as string,
    refreshToken: '' as string,
    user: null as CurrentUser | null
  }),
  getters: {
    isAuthenticated: (state) => Boolean(state.accessToken),
    isAdmin: (state) => state.user?.role === 'admin'
  },
  actions: {
    /** 启动时尝试从 localStorage 恢复；后续接后端时再补 /api/v1/auth/refresh 校验 */
    bootstrap() {
      // 当前已是恢复结果（pinia-plugin-persistedstate 已自动从 storage 还原 state）
    },
    setTokens(access: string, refresh: string) {
      this.accessToken = access
      this.refreshToken = refresh
    },
    setUser(user: CurrentUser | null) {
      this.user = user
    },
    logout() {
      this.accessToken = ''
      this.refreshToken = ''
      this.user = null
    }
  },
  persist: {
    key: 'docker-manager:auth',
    storage: localStorage
  }
})