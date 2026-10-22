<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useTheme } from '@/composables/useTheme'
import { themeModeIcon, nextThemeMode } from '@/utils/theme-mode'

/**
 * 顶栏主题切换：**只管明暗**，单按钮轮转（亮色 → 暗色 → 跟随系统）。
 *
 * **为什么不是下拉**：早先它是个下拉（开菜单 → 看三个图标 → 点一个 → 关菜单），
 * 而顶栏控件的实际用法是「我想快速翻一下明暗」——轮转把四步压成一步。
 * 真正需要精确指定的人走设置页，那里是单选按钮组（`SettingsView`）。
 *
 * **为什么主题色（蓝/绿/紫）从顶栏移除**：品牌色是产品级决定，不该让每个用户在顶栏随手改。
 * 主题色本身没有删 —— 设置页与登录页仍可调，这里只是不再重复入口。
 *
 * 图标表达的是**当前**模式，不是「下一个会变成什么」：后者会让用户在切换之后
 * 才发现自己的预期错了（点了太阳结果变月亮，图标早就在告诉他「点了变暗」）。
 * i18n key 同样按当前模式渲染（鼠标悬停看到的是「暗色」而不是「跟随系统」）。
 * 轮转逻辑放在 `utils/theme-mode.ts`（纯函数，可单测），本文件只管渲染与点击。
 */

const { t } = useI18n()
const { mode, setMode } = useTheme()

function cycle() {
  setMode(nextThemeMode(mode.value))
}
</script>

<template>
  <!--
    方形图标按钮：显式给 `w-8 h-8` 而不是只写 `text`。
    `el-button text` 靠自身 padding 撑开，图标元素多宽按钮就多宽（这里会成一个偏方的胖块），
    而顶栏里它左右紧挨着主机选择器与用户头像 —— 宽度不齐时这一排控件的视觉节奏会被打乱。
    `rounded-md` 与相邻的 HostSelector / UserMenu 按钮一致，悬停底色才对得上。
  -->
  <el-tooltip :content="t(`theme.mode.${mode}`)" placement="bottom">
    <el-button
      text
      class="!w-8 !h-8 !p-0 rounded-md"
      :aria-label="t('theme.mode.title')"
      @click="cycle"
    >
      <span class="text-lg leading-none" :class="themeModeIcon(mode)" />
    </el-button>
  </el-tooltip>
</template>