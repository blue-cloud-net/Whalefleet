<script setup lang="ts">
import { computed } from 'vue'

/**
 * 状态点：online / offline / unknown 三态。
 *
 * online 带脉冲扩散动画（尊重 prefers-reduced-motion，样式层已在 main.css 处理），
 * 让总览页的长列表里「哪台在线」一眼可辨。
 */
const props = withDefaults(
  defineProps<{
    status?: 'online' | 'offline' | 'unknown' | string
    /** 脉冲动画（总览列表用，密集场景可关掉降噪） */
    pulse?: boolean
    /** 尺寸 */
    size?: 'sm' | 'md' | 'lg'
  }>(),
  { status: 'unknown', pulse: true, size: 'md' }
)

const colorClass = computed(() => {
  switch (props.status) {
    case 'online':
      return 'bg-green-500 text-green-500'
    case 'offline':
      return 'bg-red-500 text-red-500'
    default:
      return 'bg-gray-400 dark:bg-gray-500 text-gray-400'
  }
})

const sizeClass = computed(() => {
  switch (props.size) {
    case 'sm':
      return 'w-1.5 h-1.5'
    case 'lg':
      return 'w-3 h-3'
    default:
      return 'w-2 h-2'
  }
})
</script>

<template>
  <span class="relative inline-flex shrink-0 align-middle">
    <span
      class="inline-block rounded-full align-middle"
      :class="[sizeClass, colorClass, { 'status-dot-pulse': pulse && status === 'online' }]"
    />
  </span>
</template>