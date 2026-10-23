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
 *   - requiresPermission：指定权限限定（如 `repository.read`）。
 *     ⚠️ **与 requiresAdmin 不是一回事**：`requiresAdmin` 判的是「管理面三项权限」
 *     （`ADMIN_PERMISSIONS`），而 Registry 的入口由后端 `repository.read` 单独把守。
 *     自定义角色可以只带 registry 这两项而不带管理面权限——用 requiresAdmin
 *     会把它挡在门外，而后端明明允许它进来。
 *   - showInSidebar (默认 true)：侧边栏是否展示
 *   - scope (默认 'global')：侧边栏按作用域切换菜单
 *   - icon / title：侧边栏展示用
 *   - narrow (默认 false)：内容区收窄到 `max-w-6xl`。只给正文为长文本的详情 / 日志页开
 *     （容器详情 / 容器日志 / 镜像详情）；表格页一律走默认的 1600px 全宽。
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
      {
        // 角色与权限矩阵。排在「用户管理」之后而不是之前：日常动线是
        // 「看人 → 发现缺一个角色 → 来这里建」，把角色摆在用户前面会让
        // 每次进管理区都先掠过一张平时不动的表。
        //
        // 权限矩阵必须遍历后端的权限目录渲染（见 RolesView），因此本页
        // 与「谁能看到哪些主机」是**同一套模型**的两个视角：这里配角色能干什么，
        // 用户页配某个人以什么角色进哪台机器。
        path: 'roles',
        name: 'roles',
        component: () => import('@/views/RolesView.vue'),
        meta: {
          scope: 'global',
          requiresAdmin: true,
          group: 'manage',
          icon: 'i-carbon-user-role',
          title: 'nav.roles'
        }
      },
      {
        // 私有仓库配置。排在管理组**末尾**：管理组的前三项是「人」的动线
        // （看主机 → 看人 → 发现缺角色再建角色），Registry 是纯基础设施配置，
        // 平时一配就不动，插在中间会把日常动线打断。
        //
        // ⚠️ 用 `requiresPermission` 而不是 `requiresAdmin`：后端这道门是
        // `repository.read`，自定义角色可能只带它。
        path: 'repositories',
        name: 'repositories',
        component: () => import('@/views/RepositoriesView.vue'),
        meta: {
          scope: 'global',
          requiresPermission: 'repository.read',
          group: 'manage',
          icon: 'i-carbon-container-registry',
          title: 'nav.repositories'
        }
      },
      {
        // 全局设置：**声明在管理组之后**，侧边栏才会把它渲染成管理区下面独立的一组。
        // 菜单顺序直接取路由表声明顺序（见 SidebarNav.navItems），所以「排在下面」
        // 靠的是这里的位置，而不是额外的排序字段。
        //
        // 它与「管理」是两类东西：管理组是**多用户部署下的运维操作**（改主机、分人、
        // 配角色，面向管理员且整组对非 admin 隐藏），设置是**这台实例自己的外观与偏好**。
        // 混在一组会让非 admin 看不到任何分组标题（整组被过滤掉），
        // 而 admin 看到的最后一个分组标题也会名不副实。
        path: 'settings',
        name: 'settings',
        component: () => import('@/views/SettingsView.vue'),
        meta: {
          scope: 'global',
          group: 'settings',
          icon: 'i-carbon-settings-adjust',
          title: 'nav.settings'
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
        // 「编排」= Docker Compose / Stack。后端 /api/v1/stack/* 排 0.10.0，当前先占位建好菜单。
        //
        // 位置放在**容器 / 镜像 / 数据卷 / 网络之前**，不是按依赖排的，而是按用户的实际动线：
        // Compose 项目的日常操作是「看编排 → 看它拉起的容器 / 挂载的卷 / 接入的网络」，
        // 从编排往下点最顺手；反过来从容器找编排得先记得这个容器属于哪个项目。
        // 依赖顺序只体现在**实现节奏**上（后端 0.10.0 才排上），不占用菜单的排列。
        path: 'hosts/:hostId/stacks',
        name: 'host-stacks',
        component: () => import('@/views/StacksView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-layers', title: 'nav.stacks' }
      },
      {
        path: 'hosts/:hostId/containers',
        name: 'host-containers',
        component: () => import('@/views/ContainersView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-cube', title: 'nav.containers' }
      },
      {
        // 容器详情。两级而非一级：列表页要先能进，详情才有意图可循。
        // 与 logs 同理不进侧边栏——它们是从列表跳进去的下级页，不是菜单项。
        // narrow：正文是环境变量 / 挂载 / 连接串这类长文本，1600px 的行宽没法读。
        path: 'hosts/:hostId/containers/:id/inspect',
        name: 'host-container-inspect',
        component: () => import('@/views/ContainerInspectView.vue'),
        meta: { scope: 'host', showInSidebar: false, title: 'container.detail.title', narrow: true }
      },
      {
        // 容器日志：同样按长文本页收窄，理由同 inspect。
        path: 'hosts/:hostId/containers/:id/logs',
        name: 'host-container-logs',
        component: () => import('@/views/ContainerLogsView.vue'),
        meta: { scope: 'host', showInSidebar: false, title: 'container.logs.title', narrow: true }
      },
      {
        path: 'hosts/:hostId/images',
        name: 'host-images',
        component: () => import('@/views/ImagesView.vue'),
        meta: { scope: 'host', icon: 'i-carbon-archive', title: 'nav.images' }
      },
      {
        // 镜像详情：与容器详情的安排一致——从列表跳进去的下级页，不进侧边栏。
        // `:id` 传**原始**镜像 ID（含 `sha256:`），由路由负责段内编码；调用方不要自己编码两遍。
        // narrow：镜像配置（Env / Cmd / Volumes）是长文本，理由同容器详情。
        path: 'hosts/:hostId/images/:id/inspect',
        name: 'host-image-inspect',
        component: () => import('@/views/ImageInspectView.vue'),
        meta: { scope: 'host', showInSidebar: false, title: 'image.detail.title', narrow: true }
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
  const requiresPermission =
    typeof to.meta.requiresPermission === 'string' ? to.meta.requiresPermission : null
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

  // 权限型入口：与 requiresAdmin 并列而非合并——两者判的是不同的权限集，
  // 合并会让「带 repository.read 但不带 user.write」的用户被挡在自己的页外。
  if (requiresPermission !== null && !auth.hasPermission(requiresPermission)) {
    return { name: 'overview' }
  }

  // 已登录用户访问 /login → 跳总览
  if (to.name === 'login' && auth.isAuthenticated) {
    return { name: 'overview' }
  }

  return true
})