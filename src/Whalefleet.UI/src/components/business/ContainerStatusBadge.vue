<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatusBadge from '@/components/common/StatusBadge.vue'
import {
  CONTAINER_DISPLAY_LABEL_KEY,
  CONTAINER_DISPLAY_TONE,
  containerDisplayState,
  type ContainerHealthState
} from '@/utils/container-status'

/**
 * 容器状态徽标（0.0.14）。
 *
 * ## 状态与健康是**合成**的
 * 列表里「运行中」与「健康」回答的是同一个问题——「这个服务还好吗」。
 * 分成两列时一行里会出现「运行中 / 不健康」两个词，读者得自己判断哪个才是结论；
 * 合成一列后只有一个结论：健康 / 不健康（黄）/ 运行中 / 已停止。
 *
 * 合成逻辑收口在 `utils/container-status.ts`（`containerDisplayState`），
 * 这里只负责渲染——把映射表复制到组件里必然出现「一处改了另一处没改」。
 *
 * ⚠️ **不健康是黄色（warn）不是红色**：容器还在跑，红色会让人以为它已经挂了。
 * 「不健康」是「有问题但还活着」，红色会把「重启它」变成「立刻处理」。
 */
const props = withDefaults(
  defineProps<{
    /** Docker 原始状态文本，如 `running` */
    status?: string | null
    /** 健康检查结果；`null` 表示还没查到（列表页的批量 inspect 尚未返回） */
    health?: ContainerHealthState | null
    /** 是否带前置圆点 */
    dot?: boolean
    /** 尺寸 */
    size?: 'sm' | 'md'
  }>(),
  { status: null, health: null, dot: true, size: 'sm' }
)

const { t } = useI18n()

const display = computed(() => containerDisplayState(props.status, props.health))

const tone = computed(() => CONTAINER_DISPLAY_TONE[display.value])

const label = computed(() => t(CONTAINER_DISPLAY_LABEL_KEY[display.value]))
</script>

<template>
  <StatusBadge :tone="tone" :dot="dot" :size="size">{{ label }}</StatusBadge>
</template>