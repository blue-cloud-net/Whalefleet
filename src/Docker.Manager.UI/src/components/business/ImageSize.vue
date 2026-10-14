<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatBytes, type ByteUnits } from '@/utils/container-status'

/**
 * 镜像 / 卷的体积展示（0.0.14）。
 *
 * ## 为什么体积要标出「含共享层」
 * 镜像的 `size` 是**逻辑大小**：`nginx:latest` 报 187 MB，其中 187 MB 的层
 * 与别的镜像共享。用户据此判断「删掉这个能腾出多少空间」会严重高估——
 * 真正释放的空间取决于有多少独占层。
 *
 * 因此这里同时给出 `virtualSize`（含共享）与 `size`（独占层之和）的对比提示，
 * 让用户知道两者不等。数据里只有 `size` 时退化为单值展示。
 */
const props = withDefaults(
  defineProps<{
    /** 体积（字节）；容器详情里对应 `SizeRw` / `SizeRootFs` */
    size?: number | null
    /** 虚拟体积（字节）；与 size 相等或缺失时不显示对比 */
    virtualSize?: number | null
    /** 是否显示明细 tooltip */
    tooltip?: boolean
  }>(),
  { size: null, virtualSize: null, tooltip: true }
)

const { t } = useI18n()

const units = computed<ByteUnits>(() => ({
  b: t('unit.byte'),
  kib: t('unit.kib'),
  mib: t('unit.mib'),
  gib: t('unit.gib')
}))

const formatted = computed(() => formatBytes(props.size, units.value))

/**
 * 是否存在「共享层占比」值得一提的差异。
 *
 * 阈值 10% 是刻意的：差异小于 10% 时两个数字看起来几乎一样，
 * 同时显示只会让人在两个相近的数字之间犹豫该信哪个。
 */
const hasShared = computed(() => {
  if (props.virtualSize == null || props.size == null) return false
  if (props.virtualSize <= 0 || props.size <= 0) return false

  return (props.virtualSize - props.size) / props.virtualSize > 0.1
})

const virtualFormatted = computed(() => formatBytes(props.virtualSize, units.value))
</script>

<template>
  <el-tooltip
    v-if="hasShared && tooltip"
    :content="t('common.sizeSharedHint', { virtual: virtualFormatted })"
    placement="top"
  >
    <span class="app-tabular cursor-help">
      {{ formatted }}
      <span class="text-content-faint">/ {{ virtualFormatted }}</span>
    </span>
  </el-tooltip>

  <span v-else class="app-tabular">{{ formatted }}</span>
</template>