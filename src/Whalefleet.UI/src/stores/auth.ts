import { defineStore } from 'pinia'
import { login as loginRequest, logoutSession, refreshSession } from '@/api/auth'
import type { CurrentUser, LoginResult } from '@/types/api'

/**
 * 认证 store：持有令牌对与当前用户。
 *
 * **职责边界**：本 store 只管「会话数据的读写」，不做 401 重试、不做跳转。
 * 静默刷新与跳登录页属于 HTTP 层的关注点（见 `src/api/index.ts`），
 * 否则每个调用点都要重复一遍重试逻辑。
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
    /**
     * 启动时的登录态恢复。
     *
     * 令牌已由持久化插件从 `localStorage` 还原，这里**刻意不做预先刷新**：
     * 多个标签页共用同一份 storage，若每个标签页启动都刷一次，第二次就会被
     * 服务端判定为重放（刷新令牌是一次性的）而撤销全部会话——表现为「一开新标签页就被踢」。
     * 因此续期一律按需发生：等某个请求真的收到 401 再刷。
     */
    bootstrap() {
      // 无需动作：状态由 pinia-plugin-persistedstate 还原。
      // 保留方法是为了给未来的「版本迁移 / 过期预检」留一个明确的挂点。
    },
    /**
     * 登录成功 / 刷新成功后的统一落库。
     *
     * 登录与刷新返回同一形状（含新的 `refreshToken`），因此共用一个入口；
     * 分开写迟早会出现「刷新时忘了覆盖 refreshToken」这种致命遗漏。
     */
    setSession(result: LoginResult) {
      this.accessToken = result.accessToken
      this.refreshToken = result.refreshToken
      this.user = result.user
    },
    /**
     * 用户名 / 密码登录。
     *
     * 失败时抛 `ApiRequestError`，由调用方（登录页）决定如何提示；
     * 本方法不吞异常，也不做跳转。
     */
    async login(username: string, password: string): Promise<void> {
      this.setSession(await loginRequest({ username, password }))
    },
    /**
     * 用刷新令牌换取新令牌对（轮换），返回新的 access token。
     *
     * **不做并发去重**：去重需要「原请求待重放」的上下文，那是 HTTP 层的事
     * （见 `api/index.ts` 的 `refreshAccessToken`）。
     */
    async refresh(): Promise<string> {
      this.setSession(await refreshSession(this.refreshToken))

      return this.accessToken
    },
    /**
     * 主动登出：先撤销服务端刷新令牌，再清本地会话。
     *
     * 服务端调用失败也照样清本地——否则用户会遇到「点了退出却还留在页面里」，
     * 那种状态下界面显示还是已登录，比清空后回登录页更糟。
     * 撤销失败最多让旧令牌多活到 7 天有效期结束。
     */
    async logout(): Promise<void> {
      const token = this.refreshToken

      this.clearSession()

      if (token) {
        try {
          await logoutSession(token)
        } catch {
          // 登出是幂等的，且本地会话已清空：这里刻意不打扰用户。
        }
      }
    },
    /**
     * 仅清本地会话（令牌失效 / 服务端不可用时使用）。
     *
     * 与 `logout()` 分离是必要的：401 处理路径必须**同步**清空（随后整页跳转），
     * 不能再发起一个可能同样失败的登出请求。
     */
    clearSession() {
      this.accessToken = ''
      this.refreshToken = ''
      this.user = null
    }
  },
  persist: {
    key: 'whalefleet:auth',
    storage: localStorage
  }
})