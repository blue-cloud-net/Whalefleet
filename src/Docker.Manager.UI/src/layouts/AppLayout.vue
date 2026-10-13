<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useTheme } from '@/composables/useTheme'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'
import { setLocale, type AppLocale } from '@/locales'
import ThemeSwitcher from '@/components/common/ThemeSwitcher.vue'
import LangSwitcher from '@/components/common/LangSwitcher.vue'
import StatusDot from '@/components/common/StatusDot.vue'
import HostSelector from '@/components/business/HostSelector.vue'

const route = useRoute()
const router = useRouter()
const { t, locale } = useI18n()
const auth = useAuthStore()
const host = useHostStore()
const { sidebarCollapsed, toggleSidebar } = useTheme()

/** 移动端抽屉开关 */
const drawerOpen = ref(false)

/** 当前作用域：由路由 meta 决定，决定侧边栏渲染哪一套菜单 */
const isHostScope = computed(() => route.meta.scope === 'host')
const currentHost = computed(() => host.currentHost)

interface NavItem {
  name: string
  title: string
  icon?: string
  /** 分组标记，用于插入分组标题（当前仅全局作用域使用） */
  group?: string
}

/**
 * 侧边栏导航项：从路由表派生。
 * - 按 `scope` 过滤：全局作用域只看到全局路由，主机作用域只看到主机路由
 * - 过滤 `showInSidebar === false` 与 admin 限定项
 * - 保留路由声明顺序（概览 → 资源；总览 → 设置 → 管理）
 */
const navItems = computed<NavItem[]>(() => {
  const layoutRoute = router.options.routes.find((r) => r.path === '/')
  const children = layoutRoute?.children ?? []
  const scope = isHostScope.value ? 'host' : 'global'
  return children
    .filter((r) => r.meta?.showInSidebar !== false)
    // 防御：重定向路由等无 title 的条目不进菜单（否则 t(undefined) 会抛错）
    .filter((r) => typeof r.meta?.title === 'string')
    .filter((r) => (r.meta?.scope ?? 'global') === scope)
    .filter((r) => !r.meta?.requiresAdmin || auth.isAdmin)
    .map((r) => ({
      name: r.name as string,
      title: r.meta?.title as string,
      icon: r.meta?.icon as string | undefined,
      group: r.meta?.group as string | undefined
    }))
})

/** 是否需要在导航项之间插入「管理」分组标题（全局作用域才有） */
const withGroupLabels = computed(() => !isHostScope.value)

/** 分组标题是否出现在当前位置：避免每项都算一次 */
function startsGroup(index: number): boolean {
  const item = navItems.value[index]
  if (!withGroupLabels.value || !item?.group) return false
  const prev = navItems.value[index - 1]
  return index > 0 && prev?.group !== item.group
}

/** 当前页标题：优先路由 meta，其次全局标题 */
const currentTitle = computed(() => {
  const key = route.meta?.title
  return key ? t(key as string) : t('app.title')
})

/** 内容区容器：窄屏不额外加宽，避免长行难读 */
const contentWidth = computed(() => (isHostScope.value ? 'max-w-6xl' : ''))

// 路由切换时关闭移动端抽屉
watch(() => route.path, () => {
  drawerOpen.value = false
})

/** 导航目标：主机作用域补上 hostId，保持在当前主机内 */
function navTo(item: NavItem) {
  router.push({
    name: item.name,
    params: isHostScope.value ? { hostId: String(route.params.hostId ?? '') } : {}
  })
}

function goOverview() {
  router.push({ name: 'overview' })
}

/** 主机概览页（不进侧边栏菜单，从主机名 header 进入） */
function goHostDashboard() {
  router.push({ name: 'host-dashboard', params: { hostId: String(route.params.hostId ?? '') } })
}

function logout() {
  auth.logout()
  router.push({ name: 'login' })
}

function switchLocale(next: AppLocale) {
  setLocale(next)
}
</script>

<template>
  <div class="min-h-screen flex flex-col bg-gray-50 dark:bg-gray-950">
    <!-- ===== 顶栏 64px：毛玻璃 + 细下边线，滚动时内容从下方穿过 ===== -->
    <header
      class="h-16 flex items-center gap-2 sm:gap-3 px-3 sm:px-4 shrink-0 sticky top-0 z-20 bg-white/80 dark:bg-gray-900/80 backdrop-blur-md backdrop-saturate-150 border-b border-gray-200 dark:border-gray-800"
    >
      <el-button
        text
        class="lg:!hidden"
        :aria-label="t('common.menu')"
        @click="drawerOpen = true"
      >
        <span class="i-carbon-menu text-xl" />
      </el-button>

      <!-- 侧栏折叠/展开：图标表达「点击后将发生的动作」 -->
      <!-- 折叠时显示 open(← 面板向左展开)，展开时显示 close(→ 面板向右收起) -->
      <el-button
        text
        class="!hidden lg:!inline-flex"
        :aria-label="t('common.sidebar')"
        @click="toggleSidebar"
      >
        <span
          class="text-xl transition-transform duration-200"
          :class="sidebarCollapsed ? 'i-carbon-side-panel-open' : 'i-carbon-side-panel-close'"
        />
      </el-button>

      <!-- Logo / 标题（点击回全局总览） -->
      <button
        class="flex items-center gap-2 min-w-0 group rounded-md px-1 py-1 -mx-1 transition-colors hover:bg-gray-100 dark:hover:bg-gray-800"
        @click="goOverview"
      >
        <span
          class="i-carbon-cube text-2xl text-brand-500 shrink-0 transition-transform duration-300 group-hover:scale-110"
        />
        <span
          class="font-semibold tracking-tight text-gray-900 dark:text-gray-100 transition-colors group-hover:text-brand-600 dark:group-hover:text-brand-400 app-truncate"
        >
          {{ t('app.title') }}
        </span>
      </button>

      <!-- 当前页标题：与 Logo 之间用竖线分隔，弱化处理不抢主标题 -->
      <span
        v-if="currentTitle !== t('app.title')"
        class="hidden sm:flex items-center gap-2 pl-3 ml-1 border-l border-gray-200 dark:border-gray-700"
      >
        <span class="text-sm text-gray-500 dark:text-gray-400 app-truncate">{{ currentTitle }}</span>
      </span>

      <div class="flex-1" />

      <!-- 主机选择：主机作用域显示当前主机，全局作用域显示「选择主机」占位 -->
      <div class="hidden md:flex items-center gap-1">
        <HostSelector />
      </div>

      <LangSwitcher :model-value="locale as AppLocale" @update:model-value="switchLocale" />
      <ThemeSwitcher />

      <!-- 用户菜单 -->
      <el-dropdown trigger="click">
        <el-button text class="!px-2 flex items-center gap-1.5">
          <span
            class="w-7 h-7 rounded-full grid place-items-center text-base shrink-0 bg-brand-500/12 text-brand-600 dark:text-brand-400"
          >
            <span class="i-carbon-user-avatar" />
          </span>
          <span class="hidden sm:inline text-sm text-gray-700 dark:text-gray-200 app-truncate max-w-24">
            {{ auth.user?.username ?? 'admin' }}
          </span>
        </el-button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item disabled>
              {{ auth.user?.role ?? 'admin' }}
            </el-dropdown-item>
            <el-dropdown-item divided @click="logout">
              {{ t('auth.logout') }}
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </header>

    <div class="flex flex-1 min-h-0">
      <!-- ===== 桌面端侧边栏 200px ===== -->
      <aside
        class="hidden lg:flex flex-col shrink-0 border-r border-gray-200 dark:border-gray-800 bg-white dark:bg-gray-900 transition-[width] duration-200 ease-out"
        :class="sidebarCollapsed ? 'w-16' : 'w-50'"
      >
        <nav class="flex-1 p-3 space-y-1 overflow-y-auto">
          <!-- 主机作用域：侧栏顶部显示当前主机 + 返回总览 -->
          <template v-if="isHostScope && currentHost">
            <button
              class="app-nav-item app-nav-idle px-2 py-2 mb-1 text-xs"
              :title="t('nav.backToOverview')"
              @click="goOverview"
            >
              <span class="i-carbon-arrow-left text-base shrink-0" />
              <span v-if="!sidebarCollapsed" class="app-truncate">{{ t('nav.backToOverview') }}</span>
            </button>

            <!-- 主机身份卡：折叠时退化为状态点 -->
            <div
              v-if="!sidebarCollapsed"
              class="flex items-center gap-2 px-3 py-2.5 mb-2 rounded-md bg-gray-50 dark:bg-gray-800 ring-1 ring-inset ring-gray-200/70 dark:ring-gray-700/50"
            >
              <StatusDot :status="currentHost.status" size="md" />
              <span class="text-sm font-medium text-gray-900 dark:text-gray-100 app-truncate">
                {{ currentHost.name }}
              </span>
            </div>
            <div v-else class="flex justify-center py-2 mb-2">
              <button
                class="rounded-full shrink-0 hover:scale-110 transition-transform"
                :title="`${currentHost.name} · ${t('nav.hostDashboard')}`"
                :aria-label="t('nav.hostDashboard')"
                @click="goHostDashboard"
              >
                <StatusDot :status="currentHost.status" size="lg" />
              </button>
            </div>

            <!--
              主机概览页（host-dashboard）不占用侧边栏条目（其内容已被「状态」覆盖），
              但它仍是 /hosts/:hostId 的落地页，因此这里提供显式入口。
            -->
            <button
              class="app-nav-item px-3 py-2 mb-1"
              :class="route.name === 'host-dashboard' ? 'app-nav-active' : 'app-nav-idle'"
              :title="sidebarCollapsed ? t('nav.hostDashboard') : undefined"
              @click="goHostDashboard"
            >
              <span class="text-lg shrink-0 i-carbon-dashboard" />
              <span v-if="!sidebarCollapsed" class="app-truncate">{{ t('nav.hostDashboard') }}</span>
            </button>

            <div v-if="!sidebarCollapsed" class="my-3 border-t border-gray-200 dark:border-gray-700" />
          </template>

          <template v-for="(item, index) in navItems" :key="item.name">
            <!-- 全局作用域的管理分组标题 -->
            <div
              v-if="startsGroup(index)"
              v-show="!sidebarCollapsed"
              class="flex items-center gap-1.5 px-3 pt-5 pb-1.5 text-[11px] font-semibold uppercase tracking-wider text-gray-400 dark:text-gray-500"
            >
              <span class="i-carbon-settings text-xs" />
              {{ t('nav.groupManage') }}
            </div>

            <button
              class="app-nav-item px-3 py-2"
              :class="route.name === item.name ? 'app-nav-active' : 'app-nav-idle'"
              :title="sidebarCollapsed ? t(item.title) : undefined"
              @click="navTo(item)"
            >
              <span
                v-if="item.icon"
                class="text-lg shrink-0 transition-transform duration-200"
                :class="[item.icon, route.name === item.name ? 'scale-105' : '']"
              />
              <span v-if="!sidebarCollapsed" class="app-truncate">{{ t(item.title) }}</span>
            </button>
          </template>
        </nav>

        <!-- 折叠态底部的展开提示 -->
        <div
          v-if="sidebarCollapsed"
          class="p-3 border-t border-gray-200 dark:border-gray-700 text-center"
        >
          <span class="text-[10px] text-gray-400 dark:text-gray-600">{{ t('common.sidebar') }}</span>
        </div>
      </aside>

      <!-- ===== 移动端抽屉 ===== -->
      <el-drawer
        v-model="drawerOpen"
        direction="ltr"
        :size="260"
        :with-header="false"
      >
        <nav class="flex flex-col gap-1">
          <template v-if="isHostScope && currentHost">
            <button
              class="app-nav-item app-nav-idle px-3 py-2.5 text-xs"
              @click="goOverview"
            >
              <span class="i-carbon-arrow-left text-base shrink-0" />
              <span class="app-truncate">{{ t('nav.backToOverview') }}</span>
            </button>

            <div
              class="flex items-center gap-2 px-3 py-2.5 rounded-md mb-1 bg-gray-50 dark:bg-gray-800 ring-1 ring-inset ring-gray-200/70 dark:ring-gray-700/50"
            >
              <StatusDot :status="currentHost.status" size="md" />
              <span class="text-sm font-medium text-gray-900 dark:text-gray-100 app-truncate">
                {{ currentHost.name }}
              </span>
            </div>

            <button
              class="app-nav-item px-3 py-2.5"
              :class="route.name === 'host-dashboard' ? 'app-nav-active' : 'app-nav-idle'"
              @click="goHostDashboard"
            >
              <span class="text-lg i-carbon-dashboard" />
              <span>{{ t('nav.hostDashboard') }}</span>
            </button>

            <div class="my-2 border-t border-gray-200 dark:border-gray-700" />
          </template>

          <template v-for="(item, index) in navItems" :key="item.name">
            <div
              v-if="startsGroup(index)"
              class="flex items-center gap-1.5 px-3 pt-4 pb-1.5 text-[11px] font-semibold uppercase tracking-wider text-gray-400 dark:text-gray-500"
            >
              <span class="i-carbon-settings text-xs" />
              {{ t('nav.groupManage') }}
            </div>
            <button
              class="app-nav-item px-3 py-2.5"
              :class="route.name === item.name ? 'app-nav-active' : 'app-nav-idle'"
              @click="navTo(item)"
            >
              <span v-if="item.icon" class="text-lg shrink-0" :class="item.icon" />
              <span>{{ t(item.title) }}</span>
            </button>
          </template>
        </nav>
      </el-drawer>

      <!-- ===== 内容区 ===== -->
      <main class="flex-1 min-w-0 overflow-y-auto">
        <div class="app-page" :class="contentWidth">
          <RouterView v-slot="{ Component }">
            <Transition name="app-view" mode="out-in">
              <component :is="Component" :key="route.fullPath" />
            </Transition>
          </RouterView>
        </div>
      </main>
    </div>
  </div>
</template>