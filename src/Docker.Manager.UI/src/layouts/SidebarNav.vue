<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'
import StatusDot from '@/components/common/StatusDot.vue'

/**
 * 侧边栏导航内容（**纯内容**，不含外层容器）。
 *
 * 桌面侧边栏与移动端抽屉共用这一份菜单：两处原本各写一遍，改菜单要改两处、
 * 迟早漂移。容器与断点由 `AppSidebar.vue` 决定。
 */

interface NavItem {
  name: string
  title: string
  icon?: string
  /** 分组标记，用于插入分组标题（当前仅全局作用域使用） */
  group?: string
}

const props = withDefaults(
  defineProps<{
    /** 折叠态：只留图标；文字与分组标题隐藏 */
    collapsed?: boolean
    /** desktop = 桌面侧边栏（紧凑行高）；drawer = 移动端抽屉（触控场景，行高更大） */
    variant?: 'desktop' | 'drawer'
  }>(),
  { collapsed: false, variant: 'desktop' }
)

const route = useRoute()
const router = useRouter()
const { t } = useI18n()
const auth = useAuthStore()
const host = useHostStore()

/** 当前作用域：由路由 meta 决定，决定渲染哪一套菜单 */
const isHostScope = computed(() => route.meta.scope === 'host')
const currentHost = computed(() => host.currentHost)

/** 行内边距：抽屉是手指点击，命中区域留大一点 */
const itemPadding = computed(() => (props.variant === 'drawer' ? 'px-3 py-2.5' : 'px-3 py-2'))

/**
 * 导航项：从路由表派生。
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

/** 是否需要插入「管理」分组标题（仅全局作用域有分组） */
const withGroupLabels = computed(() => !isHostScope.value)

/** 分组标题是否出现在当前位置（逐项判断，避免整表重算） */
function startsGroup(index: number): boolean {
  const item = navItems.value[index]
  if (!withGroupLabels.value || !item?.group) return false

  return index > 0 && navItems.value[index - 1]?.group !== item.group
}

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

/** 主机概览页（不进侧边栏菜单，但从主机名与状态点也能进） */
function goHostDashboard() {
  router.push({ name: 'host-dashboard', params: { hostId: String(route.params.hostId ?? '') } })
}
</script>

<template>
  <!-- 主机作用域：顶部固定「返回总览」+ 主机身份 -->
  <template v-if="isHostScope && currentHost">
    <button
      class="app-nav-item app-nav-idle mb-1 text-xs"
      :class="itemPadding"
      :title="t('nav.backToOverview')"
      @click="goOverview"
    >
      <span class="i-carbon-arrow-left text-base shrink-0" />
      <span v-if="!collapsed" class="app-truncate">{{ t('nav.backToOverview') }}</span>
    </button>

    <!-- 主机身份卡；折叠时退化为可点击的状态点（点击进主机概览） -->
    <div
      v-if="!collapsed"
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
      class="app-nav-item mb-1"
      :class="[itemPadding, route.name === 'host-dashboard' ? 'app-nav-active' : 'app-nav-idle']"
      :title="collapsed ? t('nav.hostDashboard') : undefined"
      @click="goHostDashboard"
    >
      <span class="text-lg shrink-0 i-carbon-dashboard" />
      <span v-if="!collapsed" class="app-truncate">{{ t('nav.hostDashboard') }}</span>
    </button>

    <div v-if="!collapsed" class="my-3 border-t border-gray-200 dark:border-gray-700" />
  </template>

  <template v-for="(item, index) in navItems" :key="item.name">
    <!-- 全局作用域的管理分组标题 -->
    <div
      v-if="startsGroup(index) && !collapsed"
      class="flex items-center gap-1.5 px-3 pt-5 pb-1.5 text-[11px] font-semibold uppercase tracking-wider text-gray-400 dark:text-gray-500"
    >
      <span class="i-carbon-settings text-xs" />
      {{ t('nav.groupManage') }}
    </div>

    <button
      class="app-nav-item"
      :class="[itemPadding, route.name === item.name ? 'app-nav-active' : 'app-nav-idle']"
      :title="collapsed ? t(item.title) : undefined"
      @click="navTo(item)"
    >
      <span
        v-if="item.icon"
        class="text-lg shrink-0 transition-transform duration-200"
        :class="[item.icon, route.name === item.name ? 'scale-105' : '']"
      />
      <span v-if="!collapsed" class="app-truncate">{{ t(item.title) }}</span>
    </button>
  </template>
</template>
