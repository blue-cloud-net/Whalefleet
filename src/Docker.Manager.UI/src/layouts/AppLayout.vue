<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useTheme } from '@/composables/useTheme'
import { useShortcuts } from '@/composables/useShortcuts'
import { requestCreateContainer, requestPullImage } from '@/composables/useActionBus'
import { setLocale, type AppLocale } from '@/locales'
import { shouldUseMock } from '@/mocks/switch'
import AppBreadcrumb from '@/layouts/AppBreadcrumb.vue'
import AppSidebar from '@/layouts/AppSidebar.vue'
import UserMenu from '@/layouts/UserMenu.vue'
import CommandPalette from '@/components/common/CommandPalette.vue'
import ShortcutHelpDialog from '@/components/common/ShortcutHelpDialog.vue'
import ThemeSwitcher from '@/components/common/ThemeSwitcher.vue'
import LangSwitcher from '@/components/common/LangSwitcher.vue'
import MockBadge from '@/components/common/MockBadge.vue'
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

/**
 * 是否有可用的主机上下文。
 *
 * 主机作用域的路由都带 `:hostId`，缺参数会让 vue-router 抛
 * "Missing required param"（白屏级别）。快捷键与命令面板因此都先问这个函数，
 * 在全局作用域下干脆不注册 / 不列出，而不是注册完再靠守卫弹回总览。
 */
const hasHostContext = computed(() => isHostScope.value && route.params.hostId !== undefined)

/**
 * 是否是 Mac —— 只为按键提示文案。
 *
 * 用 `navigator.platform` 而不是 `userAgent`：UA 在 iPad 桌面模式下会声称自己是
 * Mac，而那台设备接的是实体键盘，显示 ⌘K 才是对的，因此这个判断反而正好合适。
 */
const isMac = /Mac|iP(hone|ad|od)/.test(
  typeof navigator === 'undefined' ? '' : navigator.platform
)

const paletteVisible = ref(false)
const shortcutsVisible = ref(false)

/** 快捷键注册。返回的 rows 供帮助弹窗渲染——两者共用同一份注册表。 */
const { rows: shortcutRows } = useShortcuts({
  hasHostContext: () => hasHostContext.value,
  openCommandPalette: () => {
    paletteVisible.value = true
  },
  showHelp: () => {
    shortcutsVisible.value = true
  },
  toggleSidebar
})

/**
 * 命令面板的动作回调。
 *
 * 「创建容器 / 拉取镜像」通过动作总线转给当前页面的对话框（对话框开关仍是各视图的局部状态），
 * 这样命令面板不需要知道任何视图的实现细节。
 */
const paletteActions = {
  createContainer: requestCreateContainer,
  pullImage: requestPullImage,
  showShortcuts: () => {
    shortcutsVisible.value = true
  },
  toggleSidebar
}

/**
 * mock 是否开启。
 *
 * 用 `import.meta.env.DEV` 门控，生产构建下整段与 MockBadge 一起被摇掉。
 */
const mockEnabled = import.meta.env.DEV && shouldUseMock()

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
  <div class="min-h-screen flex flex-col bg-surface-page">
    <!-- ===== 顶栏 64px：毛玻璃 + 细下边线，滚动时内容从下方穿过 ===== -->
    <header
      class="h-16 flex items-center gap-1 sm:gap-2 px-3 sm:px-4 shrink-0 sticky top-0 z-20 bg-surface-card/80 backdrop-blur-md backdrop-saturate-150 border-b border-line-subtle"
    >
      <!--
        左侧品牌区：折叠按钮 / 汉堡 / Logo / 标题 合成一组。
        早先折叠按钮独自占在最左、与 Logo 之间隔了空隙，视觉上像一个无主的图标；
        把它们收进同一组并去掉组内间隙后，这一区读起来才是一个整体。
      -->
      <div class="flex items-center gap-0.5 shrink-0">
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
          class="flex items-center gap-2 min-w-0 group rounded-md px-1.5 py-1 -ml-1 transition-colors hover:bg-surface-hover"
          @click="goOverview"
        >
          <span
            class="i-carbon-cube text-2xl text-brand-500 shrink-0 transition-transform duration-300 group-hover:scale-110"
          />
          <span
            class="font-semibold tracking-tight text-content-strong transition-colors group-hover:text-brand-600 dark:group-hover:text-brand-400 app-truncate"
          >
            {{ t('app.title') }}
          </span>
        </button>
      </div>

      <!-- 面包屑：与品牌区之间用竖线分隔，弱化处理不抢主标题 -->
      <span
        class="hidden sm:flex items-center pl-3 ml-2 border-l border-line-subtle min-w-0"
      >
        <AppBreadcrumb />
      </span>

      <div class="flex-1" />

      <!-- mock 开启时显眼的角标：避免「前端在打假数据却以为是真后端」 -->
      <MockBadge v-if="mockEnabled" />

      <!--
        命令面板入口。刻意在顶栏留一个可见按钮而不只靠 Ctrl+K：
        快捷键是「已经知道的人才会用」的功能，首次用户没有任何路径发现它。
        按钮上的 ⌘K / Ctrl K 提示同时起到「教学」作用。
      -->
      <button
        class="hidden sm:flex items-center gap-2 rounded-md border border-line-subtle bg-surface-sunken px-2.5 py-1.5 text-xs text-content-faint hover:bg-surface-hover hover:border-line-strong transition-colors"
        :aria-label="t('command.placeholder')"
        @click="paletteVisible = true"
      >
        <span class="i-carbon-search" />
        <span>{{ t('command.trigger') }}</span>
        <kbd class="app-kbd">{{ isMac ? '⌘K' : 'Ctrl K' }}</kbd>
      </button>

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

    <!-- 命令面板与快捷键帮助：挂在 layout 上，全站仅一份 -->
    <CommandPalette
      v-model="paletteVisible"
      :has-host-context="() => hasHostContext"
      :actions="paletteActions"
    />
    <ShortcutHelpDialog v-model="shortcutsVisible" :rows="shortcutRows" />
  </div>
</template>

<style scoped>
/* 键帽：与 CommandPalette / ShortcutHelpDialog 里的一致，三处各写一遍样式不划算，
   但共用一个 SFC 会让「一个样式」变成一个必须 import 的组件——这里取后者更轻。 */
.app-kbd {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 1.5rem;
  padding: 0 0.3rem;
  font-family: 'JetBrains Mono', ui-monospace, monospace;
  font-size: 0.68rem;
  line-height: 1.5;
  border-radius: var(--radius-sm);
  border: 1px solid var(--border-subtle);
  background: var(--surface-card);
  color: var(--text-muted);
}
</style>
