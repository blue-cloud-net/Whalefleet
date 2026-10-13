<script setup lang="ts">
/**
 * 通用状态药丸（0.0.14）。
 *
 * ## 与既有 `StatusDot` / 各业务徽标的分工
 * - `StatusDot`：只有圆点 + 文案，靠**位置**表达含义（列表里的主机状态）
 * - 本组件：**带底色与描边的药丸**，需要在长列表里一眼定位时用
 *
 * 容器 / 镜像等业务语义色收口在各自的 `utils/*.ts`，本组件只接受 `tone`，
 * **不**认识任何业务状态——它提供的是排版与配色基座，不是业务知识。
 * 业务侧应继续用 `CONTAINER_TONE_CHIP` 那类已算好的类名，
 * 或通过 `tone` 传入映射结果。
 */
withDefaults(
  defineProps<{
    /** 语义色调 */
    tone?: 'ok' | 'down' | 'warn' | 'idle'
    /** 前置圆点 */
    dot?: boolean
    /** 尺寸 */
    size?: 'sm' | 'md'
  }>(),
  { tone: 'idle', dot: false, size: 'sm' }
)

/** 药丸配色：柔和底 + 同色系文字 + 同色系描边。 */
const TONE_CHIP: Record<string, string> = {
  ok: 'bg-status-ok-soft/60 text-status-ok ring-status-ok/25',
  down: 'bg-status-down-soft/60 text-status-down ring-status-down/25',
  warn: 'bg-status-warn-soft/60 text-status-warn ring-status-warn/25',
  idle: 'bg-status-idle-soft/60 text-status-idle ring-status-idle/30'
}

const TONE_DOT: Record<string, string> = {
  ok: 'bg-status-ok',
  down: 'bg-status-down',
  warn: 'bg-status-warn',
  idle: 'bg-status-idle'
}
</script>

<template>
  <span
    class="app-chip"
    :class="[TONE_CHIP[tone], size === 'sm' ? 'text-xs' : 'text-sm px-2.5 py-1']"
  >
    <span v-if="dot" class="w-1.5 h-1.5 rounded-full shrink-0" :class="TONE_DOT[tone]" />
    <slot />
  </span>
</template>