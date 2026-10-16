import { http, HttpResponse, delay } from 'msw'
import { ErrorCode, fail, httpStatusOf, ok } from '../envelope'
import { delayFor, type MockScenario } from '../scenarios'
import { MOCK_ROLES_DEFAULT } from '../data/permissions'
import type { LoginResult } from '@/types/api'

/**
 * 认证端点 mock。
 *
 * 复刻的重点不是「能返回 200」，而是**令牌轮换语义**——这是前端最容易写错的地方：
 * 后端每次刷新都会轮换 refresh token，沿用旧值再刷一次会被判为**重放**，
 * 进而撤销该用户全部会话。因此 mock 必须做到：
 *   1. 每次 refresh 返回一个**新的** refresh token
 *   2. 旧 refresh token 立刻作废，再拿来换 → `1102`
 * 3. 并发多个 refresh 时，后到的那些因令牌已轮换而失败 → 正好复刻真实并发行为
 */

/** mock 用户的角色，可用 `?role=` 切换以验证 admin-only 菜单的显示与拦截。 */
const DEFAULT_ROLE = 'admin'
/** access token 有效期（秒）。前端据此安排静默刷新，与后端配置一致即可。 */
const EXPIRES_IN = 3600

/**
 * 已签发的 refresh token 集合。
 *
 * 用模块级状态而非纯函数返回值，是为了让「作废」这件事可观察——
 * 纯函数 mock 做不到重放检测，也就测不出前端的单飞刷新逻辑。
 */
const issuedRefreshTokens = new Set<string>()

let tokenSeq = 0
function nextToken(prefix: string): string {
  tokenSeq += 1
  return `${prefix}.mock.${tokenSeq}.${Math.random().toString(36).slice(2, 10)}`
}

function readRole(request: Request): string {
  const fromQuery = new URL(request.url).searchParams.get('role')
  return fromQuery ?? DEFAULT_ROLE
}

/** 由用户名推导角色：`viewer` 是唯一的非 admin 测试账号，其余一律 admin。 */
function roleOf(username: string, request: Request): string {
  return username === 'viewer' ? 'viewer' : readRole(request)
}

/**
 * 角色 → **全局**有效权限集。
 *
 * 取角色夹具的权限（也就是后端迁移里种下的那份）：界面靠它决定菜单与按钮的显隐，
 * 而 `stores/auth.ts` 的 `isAdmin` 就是「这组权限是否涵盖管理面」的判定。
 * 认不出的角色名一律给空集——**宁可为空也不给全套**，猜错的方向永远是「权限更多」。
 */
function permissionsOf(roleName: string): string[] {
  return MOCK_ROLES_DEFAULT.find((role) => role.name === roleName)?.permissions ?? []
}

/**
 * 构造一次登录成功响应，并登记新签发的 refresh token。
 *
 * 结构严格对齐 `types/api.ts` 的 `LoginResult`：
 * 用户信息嵌在 `user` 里，且 `roles` 是**数组**、`permissions` 是全局权限集——
 * 写扁平结构或单值角色会让 `auth.store` 与菜单权限判定一起失准。
 */
function issueTokens(username: string, role: string) {
  const accessToken = nextToken('access')
  const refreshToken = nextToken('refresh')
  issuedRefreshTokens.add(refreshToken)
  const result: LoginResult = {
    accessToken,
    refreshToken,
    expiresIn: EXPIRES_IN,
    user: {
      id: 'u-0000a1',
      username,
      roles: [role],
      permissions: permissionsOf(role)
    }
  }
  return ok(result)
}

export function authHandlers(scenario: () => MockScenario) {
  // 路径统一用 `*/` 前缀通配：MSW 3 在 node 环境（vitest）下**不匹配**相对路径，
  // 而浏览器环境两者皆可。写死通配能让同一份 handler 在两边行为一致。
  return [
    // ===== 登录 =====
    http.post('*/api/v1/auth/login', async ({ request }) => {
      await delay(delayFor(scenario()))
      const body = (await request.json().catch(() => ({}))) as {
        username?: string
        password?: string
      }

      // 参数校验：缺字段 → 1000/400
      if (!body.username || !body.password) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // `unauthorized` 场景：验证凭据错误路径
      if (scenario() === 'unauthorized') {
        return HttpResponse.json(fail(ErrorCode.Unauthenticated), {
          status: httpStatusOf(ErrorCode.Unauthenticated)
        })
      }

      // 密码错误：后端对「用户不存在」与「密码错」返回**一致的响应**（防用户名枚举）
      if (body.password !== 'demo1234') {
        return HttpResponse.json(fail(ErrorCode.Unauthenticated), {
          status: httpStatusOf(ErrorCode.Unauthenticated)
        })
      }

      // 非 admin 用户的用户名，用于验证 requiresAdmin 路由拦截
      return HttpResponse.json(issueTokens(body.username, roleOf(body.username, request)))
    }),

    // ===== 刷新（轮换）=====
    http.post('*/api/v1/auth/refresh', async ({ request }) => {
      await delay(delayFor(scenario()))
      const body = (await request.json().catch(() => ({}))) as { refreshToken?: string }

      if (!body.refreshToken) {
        return HttpResponse.json(fail(ErrorCode.ValidationFailed), {
          status: httpStatusOf(ErrorCode.ValidationFailed)
        })
      }

      // 重放检测：令牌不在集合内 = 已作废或从未签发
      if (!issuedRefreshTokens.has(body.refreshToken)) {
        return HttpResponse.json(fail(ErrorCode.RefreshTokenRevoked), {
          status: httpStatusOf(ErrorCode.RefreshTokenRevoked)
        })
      }

      // 轮换：旧令牌立即作废
      issuedRefreshTokens.delete(body.refreshToken)
      return HttpResponse.json(issueTokens('admin', DEFAULT_ROLE))
    }),

    // ===== 登出 =====
    http.post('*/api/v1/auth/logout', async ({ request }) => {
      const body = (await request.json().catch(() => ({}))) as { refreshToken?: string }
      if (body.refreshToken) issuedRefreshTokens.delete(body.refreshToken)
      return HttpResponse.json(ok(null))
    }),

    // ===== 当前用户 =====
    http.post('*/api/v1/auth/me', async ({ request }) => {
      await delay(delayFor(scenario()))
      const role = readRole(request)
      const username = role === 'viewer' ? 'viewer' : 'admin'
      return HttpResponse.json(
        ok({
          id: 'u-0000a1',
          username,
          roles: [role],
          permissions: permissionsOf(role)
        })
      )
    })
  ]
}