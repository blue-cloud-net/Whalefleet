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
    // 权限型入口：列表要跟着**权限**走，否则没权限的人点进去会被守卫弹回总览——
    // 一个点了就跳走的菜单项比不显示更让人困惑。
    .filter((r) => {
      const required = r.meta?.requiresPermission

      return typeof required !== 'string' || auth.hasPermission(required)
    })
    .map((r) => ({
      name: r.name as string,
      title: r.meta?.title as string,
      icon: r.meta?.icon as string | undefined,
      group: r.meta?.group as string | undefined
    }))
})

/** 是否需要插入分组标题（仅全局作用域有分组） */
const withGroupLabels = computed(() => !isHostScope.value)

/**
 * 分组标题的文案与图标。
 *
 * 此前这里是写死的「管理」+ 一个齿轮图标，`startsGroup` 只回答「是不是组首」，
 * 于是任何新增分组都会顶着「管理」这个标题——把「设置」单独分组时立刻露馅。
 * 改成查表后，新增分组只需在这里加一行。
 *
 * ## 图标怎么选
 *
 * **齿轮给「设置」而不是「管理」**：齿轮是「调参数」的通用符号，
 * 而「设置」组里恰好就一项外观与偏好，齿轮放在它头上是字面意义的对应。
 * 「管理」组则是主机 / 用户 / 角色权限三件「对系统动手」的操作，
 * 用 `tools`（扳手 + 螺丝刀）表达这一层意思。
 *
 * 早先给管理组试过 `tree-view`（想呼应「用户 × 角色 × 作用域」的层级结构），
 * 但那是**从代码结构推出来的语义**：用户看到一棵树未必联想到「管理」。
 * 分组图标要的是用户一眼能懂的日常意象，不是模型结构的精确写照。
 *
 * **避开 `i-carbon-user-access`**：它语义上很像，但它与组内「角色与权限」的
 * `i-carbon-user-role` 只差一个词，两行叠在一起读起来像同一件事。
 *
 * ⚠️ 图标名必须在 Carbon 里真实存在，`pnpm dev` 会在启动时逐个校验
 * （`uno.config.ts` 的 safelist 断言）。`i-carbon-tune` / `i-carbon-slider` /
 * `i-carbon-admin` / `i-carbon-manage` 都不存在，别再凭直觉写 ——
 * 查：`node -e "const i=require('@iconify-json/carbon/icons.json');console.log('tools' in i.icons)"`。
 */
const GROUP_META: Record<string, { labelKey: string; icon: string }> = {
  manage: { labelKey: 'nav.groupManage', icon: 'i-carbon-tools' },
  settings: { labelKey: 'nav.groupSettings', icon: 'i-carbon-settings' }
}

/** 分组标题：未登记的分组不给标题（宁可不分组，也不要顶着别的组的名字） */
function groupMeta(group?: string) {
  return group ? GROUP_META[group] : undefined
}

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
    <!--
      折叠态：身份卡退化为可点的状态点。
      此时顶栏 HostSelector 仍显示主机名，而这里是菜单区内**唯一**能进主机概览的
      入口，因此必须保留——不能因为「展开态删了身份卡」而把折叠态一起删掉。
    -->
    <div v-if="collapsed" class="flex justify-center py-2 mb-1">
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
      展开态：只留一个分组标题。
      早先是「主机名 + 状态点」的身份卡，但它与顶栏 HostSelector 完全重复
      （同一屏里同一个主机名出现三次：顶栏、这里、面包屑）。改为分组标题后，
      「我在哪台机器」由顶栏负责，本区只回答「这台机器上有哪些页面」。
    -->
    <div
      v-else
      class="flex items-center gap-1.5 px-3 pb-1.5 text-[11px] font-semibold uppercase tracking-wider text-content-faint"
    >
      <span class="i-carbon-data-center text-xs" aria-hidden="true" />
      <span class="app-truncate">{{ currentHost.name }}</span>
    </div>

    <!--
      主机概览页（host-dashboard）不占用路由表里的侧边栏条目（内容与资源页有重叠），
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

    <button
      class="app-nav-item app-nav-idle mb-1 text-xs"
      :class="itemPadding"
      :title="t('nav.backToOverview')"
      @click="goOverview"
    >
      <span class="i-carbon-arrow-left text-base shrink-0" />
      <span v-if="!collapsed" class="app-truncate">{{ t('nav.backToOverview') }}</span>
    </button>

    <div v-if="!collapsed" class="my-3 border-t border-line-subtle" />
  </template>

  <template v-for="(item, index) in navItems" :key="item.name">
    <!-- 全局作用域的分组标题（文案与图标按分组取，未登记的分组不显示标题） -->
    <div
      v-if="startsGroup(index) && !collapsed && groupMeta(item.group)"
      class="flex items-center gap-1.5 px-3 pt-5 pb-1.5 text-[11px] font-semibold uppercase tracking-wider text-content-faint"
    >
      <span class="text-xs shrink-0" :class="groupMeta(item.group)?.icon" aria-hidden="true" />
      {{ t(groupMeta(item.group)?.labelKey ?? '') }}
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
