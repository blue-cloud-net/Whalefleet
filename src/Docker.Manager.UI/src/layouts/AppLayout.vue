<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useTheme } from '@/composables/useTheme'
import { setLocale, type AppLocale } from '@/locales'
import AppBreadcrumb from '@/layouts/AppBreadcrumb.vue'
import AppSidebar from '@/layouts/AppSidebar.vue'
import UserMenu from '@/layouts/UserMenu.vue'
import ThemeSwitcher from '@/components/common/ThemeSwitcher.vue'
import LangSwitcher from '@/components/common/LangSwitcher.vue'
import HostSelector from '@/components/business/HostSelector.vue'

/**
 * 应用外壳：顶栏 + 侧边栏 + 内容区。
 *
 * 三块各自是独立组件（都在 `layouts/` 下）：顶栏里的面包屑与用户菜单、侧边栏及其菜单内容
 * 自成一体。本文件只负责把它们摆到位置，并处理布局级状态（移动端抽屉开关、内容宽度）。
 */

const route = useRoute()
const router = useRouter()
const { t, locale } = useI18n()
const { sidebarCollapsed, toggleSidebar } = useTheme()

/** 移动端抽屉开关（顶栏的菜单按钮打开，由侧边栏组件消费） */
const drawerOpen = ref(false)

/** 当前作用域：由路由 meta 决定 */
const isHostScope = computed(() => route.meta.scope === 'host')

/** 内容区容器：窄屏不额外加宽，避免长行难读 */
const contentWidth = computed(() => (isHostScope.value ? 'max-w-6xl' : ''))

// 路由切换时关闭移动端抽屉
watch(
  () => route.path,
  () => {
    drawerOpen.value = false
  }
)

function goOverview() {
  router.push({ name: 'overview' })
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
      <!-- 移动端：打开侧边栏抽屉 -->
      <el-button text class="lg:!hidden" :aria-label="t('common.menu')" @click="drawerOpen = true">
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

      <!-- 面包屑：与 Logo 之间用竖线分隔，弱化处理不抢主标题 -->
      <span
        class="hidden sm:flex items-center pl-3 ml-1 border-l border-gray-200 dark:border-gray-700 min-w-0"
      >
        <AppBreadcrumb />
      </span>

      <div class="flex-1" />

      <!-- 主机选择：主机作用域显示当前主机，全局作用域显示「选择主机」占位 -->
      <div class="hidden md:flex items-center gap-1">
        <HostSelector />
      </div>

      <LangSwitcher :model-value="locale as AppLocale" @update:model-value="switchLocale" />
      <ThemeSwitcher />
      <UserMenu />
    </header>

    <div class="flex flex-1 min-h-0">
      <AppSidebar v-model:drawer-open="drawerOpen" :collapsed="sidebarCollapsed" />

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
