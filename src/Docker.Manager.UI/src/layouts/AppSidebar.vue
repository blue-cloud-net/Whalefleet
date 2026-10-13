<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import SidebarNav from '@/layouts/SidebarNav.vue'

/**
 * 侧边栏容器：桌面端固定侧栏 + 移动端抽屉。
 *
 * 同一套菜单（`SidebarNav`）在两处复用，容器只负责断点、宽度与抽屉开关——
 * 菜单内容与"用哪套菜单"的判断都留在 `SidebarNav` 里。
 */

withDefaults(defineProps<{ collapsed?: boolean }>(), { collapsed: false })

/** 移动端抽屉开关（由顶栏的菜单按钮控制） */
const drawerOpen = defineModel<boolean>('drawerOpen', { default: false })

const { t } = useI18n()
</script>

<template>
  <!-- ===== 桌面端侧边栏（lg 及以上） ===== -->
  <aside
    class="hidden lg:flex flex-col shrink-0 border-r border-line-subtle bg-surface-card transition-[width] duration-200 ease-out"
    :class="collapsed ? 'w-16' : 'w-50'"
  >
    <nav class="flex-1 p-3 space-y-1 overflow-y-auto">
      <SidebarNav :collapsed="collapsed" variant="desktop" />
    </nav>

    <!-- 折叠态底部的展开提示 -->
    <div v-if="collapsed" class="p-3 border-t border-line-subtle text-center">
      <span class="text-[10px] text-content-faint">{{ t('common.sidebar') }}</span>
    </div>
  </aside>

  <!-- ===== 移动端抽屉（lg 以下，永不折叠） ===== -->
  <el-drawer v-model="drawerOpen" direction="ltr" :size="260" :with-header="false">
    <nav class="flex flex-col gap-1">
      <SidebarNav variant="drawer" />
    </nav>
  </el-drawer>
</template>
