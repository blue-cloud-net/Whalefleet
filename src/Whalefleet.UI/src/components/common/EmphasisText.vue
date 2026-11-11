<script setup lang="ts">
import { computed } from 'vue'
import { parseEmphasis } from '@/utils/emphasis'

/**
 * 把词条里的 `**强调**` 渲染成真正的粗体。
 *
 * ## 为什么需要它
 * 词条用 `**…**` 表达「这一句要加粗」，但 `{{ t(key) }}` 是纯文本插值——
 * **没有 markdown 渲染器**，星号会原样显示给用户。
 *
 * ## 为什么不用 v-html
 * `v-html` 能一次渲染 `<strong>`，但它把词条变成了 HTML 输入：
 * 词条里一个落单的 `<` 就可能截断标签结构，界面直接崩掉。
 * 这里逐段渲染纯文本，只有「要不要加粗」这一个布尔值，不存在这个问题。
 */
const props = defineProps<{
  /** 词条正文（可含 `**强调**` 标记） */
  text: string
}>()

const segments = computed(() => parseEmphasis(props.text))
</script>

<template>
  <span>
    <template v-for="(segment, index) in segments" :key="index">
      <strong v-if="segment.bold" class="font-medium text-content-strong">{{ segment.text }}</strong>
      <template v-else>{{ segment.text }}</template>
    </template>
  </span>
</template>
