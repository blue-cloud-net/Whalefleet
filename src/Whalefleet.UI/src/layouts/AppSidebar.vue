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
  <!--
    ⚠️ `sticky top-16` + `h-[calc(100vh-4rem)]` 是**功能必需**，不是装饰：

    根布局是 `min-h-screen flex flex-col`——高度是「至少一屏」，不是「恰好一屏」。
    内容比一屏高时，整条高度链就变成内容高度，于是滚的是**文档**而不是 `main`；
    侧栏作为普通 flex 子项跟着文档一起滚上去，`nav` 里那句 `overflow-y-auto`
    根本轮不到生效（它的高度也是内容撑的）。

    钉住它需要三件一起：
      ① `sticky top-16`——16 = 顶栏的 `h-16`，正好贴在顶栏下面；
      ② `h-[calc(100vh-4rem)]`——给出**确定**高度，`nav` 的 `flex-1 overflow-y-auto`
         才有意义（菜单比视口长时自己滚，而不是把整个文档撑高）；
      ③ `self-start`——不写它的话，flex 的 `align-items: stretch` 会先把交叉轴尺寸
         拉满，显式高度在某些情况下被压掉。

    ⚠️ 这里敢用 `100vh` 而不必提防移动端地址栏：本元素是 `hidden lg:flex`，
    只在小屏以上出现，而桌面浏览器的地址栏不会伸缩。
  -->
  <aside
    class="hidden lg:flex flex-col shrink-0 self-start sticky top-16 h-[calc(100vh-4rem)] border-r border-line-subtle bg-surface-card transition-[width] duration-200 ease-out"
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
