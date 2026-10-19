<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useTheme } from '@/composables/useTheme'
import { useShortcuts } from '@/composables/useShortcuts'
import { requestCreateContainer, requestPullImage } from '@/composables/useActionBus'
import { shouldUseMock } from '@/mocks/switch'
import AppBreadcrumb from '@/layouts/AppBreadcrumb.vue'
import AppSidebar from '@/layouts/AppSidebar.vue'
import UserMenu from '@/layouts/UserMenu.vue'
import CommandPalette from '@/components/common/CommandPalette.vue'
import ShortcutHelpDialog from '@/components/common/ShortcutHelpDialog.vue'
import ThemeSwitcher from '@/components/common/ThemeSwitcher.vue'
import MockBadge from '@/components/common/MockBadge.vue'
import HostSelector from '@/components/business/HostSelector.vue'

/**
 * 应用外壳：顶栏 + 侧边栏 + 内容区。
 *
 * 三块各自是独立组件（都在 `layouts/` 下）：顶栏里的面包屑与用户菜单、侧边栏及其菜单内容
 * 自成一体。本文件只负责把它们摆到位置，并处理布局级状态（移动端抽屉开关、内容宽度）。
 *
 * **顶栏的「值不值得放在这里」判据**：功能切换得越频繁越该留在视线内（主题 / 侧栏折叠 /
 * 主机切换），一年用几次的一律收进用户菜单（改密、语言、主题色）。
 * 早期版本把语言 + 主题模式 + 主题色三个控件并排挂在右侧，顶栏右半边读起来像一排图标；
 * 现在右侧只留「主机切换 + 用户头像」两件真正需要反复点的东西。
 */

const route = useRoute()
const router = useRouter()
const { t } = useI18n()
const { sidebarCollapsed, toggleSidebar } = useTheme()

/** 移动端抽屉开关（顶栏的菜单按钮打开，由侧边栏组件消费） */
const drawerOpen = ref(false)

/** 当前是否处于主机作用域（`route.meta.scope === 'host'`）。 */
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

/**
 * 内容区宽度。
 *
 * 默认全宽（`app-page` 自带的 `max-w-[1600px]`）——表格类页面列多，
 * 收窄到 6xl 会让「名称 / 镜像 / 端口」同时挤到换行，反而更窄。
 *
 * 只有 `meta.narrow` 的页才收窄到 `max-w-6xl`：它们是详情 / 日志页，
 * 正文是一行行长文本（环境变量、连接串、日志），1600px 的行宽眼睛要横跨整个屏幕才对得上行尾。
 * 做成**默认宽、按需窄**而非反过来，是为了新增列表页时不必记得挂标记——
 * 忘了的后果是多出一段空行，而反过来则是表格被挤到换行。
 */
const contentWidth = computed(() => (route.meta.narrow === true ? 'max-w-6xl' : ''))

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
</script>

<template>
  <div class="min-h-screen flex flex-col bg-surface-page">
    <!--
      ===== 顶栏 64px：毛玻璃 + 细下边线，滚动时内容从下方穿过 =====

      布局用 **左中右三列网格**（`1fr auto 1fr`）而不是「左 + flex-1 撑开 + 右」。
      命令面板是全站唯一的全局搜索入口，把它放在正中，两侧内容再多也不会把它挤偏；
      `auto` 列的宽度由按钮自身决定，两侧 `1fr` 等宽 → 中列始终落在视觉中线。
      `overflow-hidden` 是必要的：面包屑是 `whitespace-nowrap` 的，长主机名
      不裁剪的话会顶开中列，「居中」在那一刻就失效了。
    -->
    <header
      class="h-16 shrink-0 sticky top-0 z-20 grid grid-cols-[1fr_auto_1fr] items-center gap-2 px-3 sm:px-4 bg-surface-card/80 backdrop-blur-md backdrop-saturate-150 border-b border-line-subtle"
    >
      <!-- 左列：品牌区 + 面包屑 -->
      <div class="flex items-center gap-2 min-w-0 overflow-hidden">
        <!--
          品牌区：折叠按钮 / 汉堡 / Logo / 标题 合成一组。

          **组内用 `gap-3`（12px）而不是 `gap-2`**：这个 12px 不是随手挑的，而是为了
          让面包屑前那道竖线正好落在**侧边栏的右边框**上，形成一条贯穿顶栏与侧栏的直线。
          实测坐标（侧边栏展开 = `w-50` = 200px）：
            折叠按钮 16–48 → `gap-2` 时竖线在 196px，差 4px 对不齐；
            改成 `gap-3` 后 Logo 整体右移 4px，竖线正好落在 200px。
          产品名 `app.title` 中英文都是 `Whalefleet`（定长），所以这条对齐不会被 i18n 撑歪。
          ⚠️ 若日后产品名改长或侧边栏宽度调整，这里要重新量一次——这条竖线没有约束在代码里。
        -->
        <div class="flex items-center gap-2.5 shrink-0">
          <!-- 移动端：打开侧边栏抽屉 -->
          <el-button
            text
            class="lg:!hidden !w-8 !h-8 !p-0 rounded-md"
            :aria-label="t('common.menu')"
            @click="drawerOpen = true"
          >
            <span class="i-carbon-menu text-xl leading-none" />
          </el-button>

          <!-- 侧栏折叠/展开：图标表达「点击后将发生的动作」 -->
          <!-- 折叠时显示 open(← 面板向左展开)，展开时显示 close(→ 面板向右收起) -->
          <!--
            `!ml-0` 是必需的，不是微调：Element Plus 有 `.el-button + .el-button { margin-left: 12px }`，
            而上一行的汉堡按钮在桌面端是 `display: none` —— **`display:none` 不会让 `+` 选择器失效**，
            它仍在 DOM 里、仍是前一个兄弟，于是折叠按钮白拿 12px 左边距（实测 left=28，
            比顶栏 16px 内边距还右 12px）。不抵消它，按钮就永远对不齐顶栏左缘。
            用 `!ml-0` 而不是负边距：负值会连移动端的汉堡按钮一起拽走。
          -->
          <el-button
            text
            class="!hidden lg:!inline-flex !w-8 !h-8 !p-0 !ml-0 rounded-md"
            :aria-label="t('common.sidebar')"
            @click="toggleSidebar"
          >
            <span
              class="text-xl leading-none transition-transform duration-200"
              :class="sidebarCollapsed ? 'i-carbon-side-panel-open' : 'i-carbon-side-panel-close'"
            />
          </el-button>

          <!-- Logo / 标题（点击回全局总览） -->
          <!--
            `-ml-1` 已去掉：它原本用来抵消自身 `px-1.5` 的内边距，让立方体贴齐顶栏左缘，
            但折叠按钮改成 32×32 方形后，这个负边距会啃掉组内间距——
            实测两个按钮的盒子重叠了 2px（折叠 28–60 / Logo 58–190），
            看上去就是「折叠按钮紧贴产品图标」。改由组内 `gap-2` 负责间距。
          -->
          <button
            class="flex items-center gap-2 min-w-0 group rounded-md px-1.5 py-1 transition-colors hover:bg-surface-hover"
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
          class="hidden sm:flex items-center pl-3 border-l border-line-subtle min-w-0 overflow-hidden"
        >
          <AppBreadcrumb />
        </span>
      </div>

      <!--
        中列：命令面板入口。刻意在顶栏留一个可见按钮而不只靠 Ctrl+K：
        快捷键是「已经知道的人才会用」的功能，首次用户没有任何路径发现它。
        按钮上的 ⌘K / Ctrl K 提示同时起到「教学」作用。
        放在正中还有个副作用上的好处——它是全站唯一与「当前在哪台机器」无关的操作，
        居中让它读起来像一个全局搜索框，而不是某个页面的工具条按钮。

        `w-64 sm:w-80 xl:w-96` 而不是定宽：定宽在窄屏会挤掉两侧控件，
        而搜索框「宽一点更好用」——这是唯一一个值得多占地方的地方。
      -->
      <button
        class="hidden sm:flex items-center gap-2 w-64 xl:w-96 rounded-md border border-line-subtle bg-surface-sunken px-3 py-1.5 text-xs text-content-faint hover:bg-surface-hover hover:border-line-strong transition-colors"
        :aria-label="t('command.placeholder')"
        @click="paletteVisible = true"
      >
        <span class="i-carbon-search shrink-0" />
        <span class="app-truncate">{{ t('command.trigger') }}</span>
        <kbd class="app-kbd ml-auto shrink-0">{{ isMac ? '⌘K' : 'Ctrl K' }}</kbd>
      </button>

      <!--
        右列：主机选择 + 主题切换 + 用户菜单。
        主机选择与主题按钮相邻——两者都是「全局偏好 / 当前上下文」类控件，
        而左侧只回答「我认得这个站、我在这台机器的哪个页面」，不混入会被频繁切换的项。
        靠外的是主机：它比主题更常被点到（换机器看的东西和换明暗不在一个频率上）。
      -->
      <div class="flex items-center justify-end gap-1 min-w-0">
        <!-- mock 开启时显眼的角标：避免「前端在打假数据却以为是真后端」 -->
        <MockBadge v-if="mockEnabled" />

        <!-- 主机选择：主机作用域显示当前主机，全局作用域显示「选择主机」占位 -->
        <div class="hidden md:flex items-center gap-1 min-w-0">
          <HostSelector />
        </div>

        <ThemeSwitcher />

        <UserMenu />
      </div>
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
