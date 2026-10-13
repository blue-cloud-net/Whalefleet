import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'

/**
 * 路由：0.0.5 阶段所有业务页都是占位；0.0.11 起逐步填充真实实现。
 *
 * **两段式信息架构**（Portainer 式）：
 *   - `scope: 'global'` —— 全局作用域。登录后的默认落地页是「总览」，
 *     展示所有服务器；此作用域的请求路径不含 `hostId`。
 *   - `scope: 'host'` —— 主机作用域。路径形如 `/hosts/:hostId/containers`，
 *     由总览页的「进入」按钮进入，URL 自带 `hostId`（可深链 / 可分享）。
 *
 * 路由元信息约定：
 *   - requiresAuth (默认 true)：未登录跳 /login?redirect=...
 *   - requiresAdmin：admin 限定，非 admin 跳总览
 *   - showInSidebar (默认 true)：侧边栏是否展示
 *   - scope (默认 'global')：侧边栏按作用域切换菜单
 *   - icon / title：侧边栏展示用
 */
const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/LoginView.vue'),
    meta: { requiresAuth: false, showInSidebar: false, title: 'auth.login.title' }
  },
  {
    path: '/',
    component: () => import('@/layouts/AppLayout.vue'),
    redirect: '/overview',
    children: [
      // ===== 全局作用域 =====
      {
        path: 'overview',
        name: 'overview',
        component: () => import('@/views/OverviewView.vue'),
        meta: { scope: 'global', icon: 'i-carbon-dashboard', title: 'nav.overview' }
      },
      {
        path: 'settings',
        name: 'settings',
        component: () => import('@/views/SettingsView.vue'),
        meta: { scope: 'global', icon: 'i-carbon-settings-adjust', title: 'nav.settings' }
      },
      {
        path: 'hosts',
        name: 'hosts',
        component: () => import('@/views/HostsView.vue'),
        meta: {
          scope: 'global',
          requiresAdmin: true,
          group: 'manage',
          icon: 'i-carbon-data-center',
          title: 'nav.hosts'
        }
      },
      {
        path: 'users',
        name: 'users',
        component: () => import('@/views/UsersView.vue'),
        meta: {
          scope: 'global',
          requiresAdmin: true,
          group: 'manage',
          icon: 'i-carbon-user-multiple',
          title: 'nav.users'
        }
      },

      // ===== 主机作用域（hosts/:hostId/...）=====
      {
        // 纯重定向：必须标记 showInSidebar=false，否则会被侧边栏派生逻辑当成菜单项
        // （无 meta.title → t(undefined) 抛 "Invalid arguments"）
        path: 'hosts/:hostId',
        redirect: (to) => ({ name: 'host-dashboard', params: { hostId: to.params.hostId } }),
        meta: { showInSidebar: false, scope: 'host' }
      },
      {
        // 主机概览页：作为 /hosts/:hostId 的落地页，承载「主机信息」卡片（版本 / 启动时间）。
        // 不进侧边栏——侧边栏第一项是「状态」，其内容（运行状态 / 资源计数 / 运行时长）
        // 已覆盖概览页的主要职责，重复列出会让菜单变长且信息冗余。
        path: 'hosts/:hostId/dashboard',
        name: 'host-dashboard',
        component: () => import('@/views/HostDashboardView.vue'),
        meta: {
          scope: 'host',
          showInSidebar: false,
          icon: 'i-carbon-dashboard',
          title: 'nav.hostDashboard'
        }
      },
      {
        path: 'hosts/:hostId/status',
        name: 'host-status',
        component: () => import('@/views/HostStatusView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-activity', title: 'nav.status' }
      },
      {
        path: 'hosts/:hostId/containers',
        name: 'host-containers',
        component: () => import('@/views/ContainersView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-container-software', title: 'nav.containers' }
      },
      {
        path: 'hosts/:hostId/images',
        name: 'host-images',
        component: () => import('@/views/ImagesView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-image', title: 'nav.images' }
      },
      {
        path: 'hosts/:hostId/volumes',
        name: 'host-volumes',
        component: () => import('@/views/VolumesView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-data-volume', title: 'nav.volumes' }
      },
      {
        path: 'hosts/:hostId/networks',
        name: 'host-networks',
        component: () => import('@/views/NetworksView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-network-3', title: 'nav.networks' }
      },
      {
        // 「编排」= Docker Compose / Stack。后端 /api/v1/stack/* 排 0.4.0，
        // 当前先占位建好菜单，后续迭代直接填充实现。
        // 放在网络之后：它依赖的容器/镜像能力排在 0.0.11 / 0.0.12，
        // 与交付节奏对齐，菜单里不会先摆一个点不动的入口。
        path: 'hosts/:hostId/stacks',
        name: 'host-stacks',
        component: () => import('@/views/StacksView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-layers', title: 'nav.stacks' }
      },
      {
        // 「事件」= 操作审计日志（谁在何时做了什么），**不是** Docker Events 流。
        // 后端需新建审计表；docs/features.md 明确审计日志排在 1.0.0 之后，当前先占位。
        path: 'hosts/:hostId/events',
        name: 'host-events',
        component: () => import('@/views/EventsView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-events', title: 'nav.events' }
      }
    ]
  },
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { requiresAuth: false, showInSidebar: false }
  }
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
  scrollBehavior: () => ({ top: 0 })
})

// ===== 路由守卫 =====
router.beforeEach(async (to) => {
  const auth = useAuthStore()
  const host = useHostStore()
  const requiresAuth = to.meta.requiresAuth !== false
  const requiresAdmin = to.meta.requiresAdmin === true
  const isHostScope = to.meta.scope === 'host' || typeof to.params.hostId === 'string'

  if (requiresAuth && !auth.isAuthenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  // 主机列表改由 API 加载后，首次导航时 hosts 还是空的。
  // 这里必须 await，否则深链（如直接打开 /hosts/local/dashboard）会因
  // 「找不到该主机」而被误踢回 /overview —— 刷新即失效。
  //
  // ensureLoaded() 幂等：只有第一次会真的发请求，后续导航直接返回。
  await host.ensureLoaded()

  // 加载失败时**放行导航**：host.error 交给页面显示错误态 + 重试。
  // 若在这里也重定向，/overview 会命中同一段逻辑再次被重定向，成死循环。
  const hostsAvailable = host.error === null

  // 主机作用域：把 URL 上的 hostId 同步到 store；未知主机回退总览，避免停在死路上
  if (isHostScope) {
    const hostId = String(to.params.hostId ?? '')
    const known = host.hosts.some((h) => h.id === hostId)
    if (!hostId || (hostsAvailable && host.hosts.length > 0 && !known)) {
      return { name: 'overview' }
    }
    host.setCurrent(hostId)
  } else {
    // 全局作用域：清空当前主机，避免界面还停留在某台机器的上下文里
    host.clearCurrent()
  }

  if (requiresAdmin && !auth.isAdmin) {
    return { name: 'overview' }
  }

  // 已登录用户访问 /login → 跳总览
  if (to.name === 'login' && auth.isAuthenticated) {
    return { name: 'overview' }
  }

  return true
})