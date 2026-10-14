<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatusBadge from '@/components/common/StatusBadge.vue'
import { CONTAINER_TONE_LABEL_KEY, containerTone, type ContainerTone } from '@/utils/container-status'

/**
 * 容器状态徽标（0.0.14）。
 *
 * 语义映射（哪个状态算运行中 / 算异常）收口在 `utils/container-status.ts`，
 * 这里只负责渲染——把映射表复制到组件里必然出现「一处改了另一处没改」。
 */
const props = withDefaults(
  defineProps<{
    /** Docker 原始状态文本，如 `running` */
    status?: string | null
    /** 是否带前置圆点 */
    dot?: boolean
    /** 尺寸 */
    size?: 'sm' | 'md'
  }>(),
  { status: null, dot: true, size: 'sm' }
)

const { t } = useI18n()

const tone = computed<ContainerTone>(() => containerTone(props.status))

const label = computed(() => t(CONTAINER_TONE_LABEL_KEY[tone.value]))
</script>

<template>
  <StatusBadge :tone="tone" :dot="dot" :size="size">{{ label }}</StatusBadge>
</template>