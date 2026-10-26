<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { getHostDiskUsage } from '@/api/host'
import { pruneImages } from '@/api/image'
import { pruneVolumes } from '@/api/volume'
import { buildPrunePreview, pruneCandidateLabel } from '@/utils/prune-preview'
import { formatBytes } from '@/utils/container-status'
import type { PruneResult } from '@/types/api'
import type { PrunePreview } from '@/utils/prune-preview'

/**
 * 资源清理对话框（0.7.0）：镜像与数据卷共用。
 *
 * ## 为什么必须先看清单再确认
 * 清理端点的请求里**没有「要删哪些」**——选哪些由守护进程按
 * 「没有被任何容器使用 / 引用」判断。因此本对话框唯一的安全阀就是
 * **在动手之前把候选清单摆出来**。
 *
 * ## 为什么预览与实际结果可能不一致（而且要如实说）
 * - 预览来自 `GET /hosts/{hostId}/system/df`，是**策略级**的候选；
 * - 守护进程在真正删除时还会**跳过带挂载选项的卷**（那些其实不是本地卷），
 *   并按自己的引用计数判断。
 *
 * 因此结果区展示的是**实际删掉了什么**（后端回传的 `deleted`），
 * 而不是把预览原样再念一遍。
 */
const props = defineProps<{
  hostId: string
  /** 要清理的资源类别 */
  resource: 'images' | 'volumes'
  /**
   * 镜像专用：是否只清悬空镜像。
   *
   * `true`（默认）= 只清一个标签都不剩的镜像；
   * `false` = **不止悬空**（等价 `docker image prune -a`），
   * 任何未被容器使用的镜像都是候选——这是不可逆的大动作。
   */
  danglingOnly?: boolean
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ done: [] }>()

const { t } = useI18n()

const loading = ref(false)
const submitting = ref(false)
const errorMessage = ref<string | null>(null)
const preview = ref<PrunePreview>({ candidates: [], unknownCount: 0, reclaimable: null, totalCount: null })

/** 「高级」折叠区默认收起：标签过滤是低频用法。 */
const advancedOpen = ref(false)
/** 标签过滤表达式（`key` 或 `key=value`）；空串 = 不过滤。 */
const label = ref('')

/** 清理完成后的实际结果；非 `null` 时对话框切到「结果」视图。 */
const result = ref<PruneResult | null>(null)

const isImages = computed(() => props.resource === 'images')

/** 只清悬空镜像时，悬空的判据是「一个标签都不剩」。 */
const effectiveDanglingOnly = computed(() => props.danglingOnly ?? true)

const byteUnits = computed(() => ({
  b: t('unit.byte'),
  kib: t('unit.kib'),
  mib: t('unit.mib'),
  gib: t('unit.gib')
}))

/**
 * 填了标签过滤时，预览**不再**等于实际清理范围。
 *
 * `system/df` 不支持按标签过滤，因此预览永远是全量候选；
 * 而实际清理只动带该标签的资源。不说清楚就成了隐性谎报。
 */
const hasLabelFilter = computed(() => label.value.trim().length > 0)

/** 候选为空时不该让用户点「清理」——那是一次没有任何效果的不可逆操作。 */
const canSubmit = computed(
  () => !submitting.value && !loading.value && preview.value.candidates.length > 0
)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null

  try {
    const usage = await getHostDiskUsage(
      props.hostId,
      isImages.value ? 'image' : 'volume'
    )

    preview.value = buildPrunePreview(isImages.value ? usage.images : usage.volumes, {
      danglingOnly: isImages.value ? effectiveDanglingOnly.value : undefined
    })
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : String(e)
    preview.value = { candidates: [], unknownCount: 0, reclaimable: null, totalCount: null }
  } finally {
    loading.value = false
  }
}

watch(visible, (open) => {
  if (!open) return

  label.value = ''
  advancedOpen.value = false
  result.value = null
  void load()
})

async function submit(): Promise<void> {
  if (!canSubmit.value) return

  submitting.value = true
  errorMessage.value = null

  try {
    const trimmed = label.value.trim()
    const payload = trimmed.length > 0 ? { label: trimmed } : {}

    result.value = isImages.value
      ? await pruneImages(props.hostId, {
          ...payload,
          danglingOnly: effectiveDanglingOnly.value
        })
      : await pruneVolumes(props.hostId, payload)

    ElMessage.success(t('prune.done', { count: result.value.deleted.length }))

    // 结果要在对话框里显示（实际删了什么），因此不立刻关闭
    emit('done')
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}

function close(): void {
  visible.value = false
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t(isImages ? 'prune.title.images' : 'prune.title.volumes')"
    width="560px"
  >
    <!-- 结果视图：展示「实际删掉了什么」 -->
    <div v-if="result !== null">
      <p class="text-sm text-content-strong">
        {{ t('prune.result.count', { count: result.deleted.length }) }}
      </p>
      <p class="mt-1 text-xs text-content-muted">
        {{ t('prune.result.reclaimed', { size: formatBytes(result.reclaimedBytes, byteUnits) }) }}
      </p>

      <ul v-if="result.deleted.length > 0" class="mt-3 max-h-48 space-y-1 overflow-y-auto">
        <li
          v-for="item in result.deleted"
          :key="item"
          class="break-all font-mono text-xs text-content-muted"
        >
          {{ item }}
        </li>
      </ul>
    </div>

    <!-- 预览视图 -->
    <div v-else v-loading="loading">
      <p class="text-sm text-content-muted">
        {{ t(isImages ? 'prune.subtitle.images' : 'prune.subtitle.volumes') }}
      </p>

      <p v-if="errorMessage !== null" class="mt-3 text-sm text-status-down">
        {{ errorMessage }}
      </p>

      <!--
        「高级」折叠项：标签过滤。
        ⚠️ 默认收起——它是低频用法，而默认展开会让常规清理看起来更复杂。
      -->
      <el-collapse v-model="advancedOpen" class="mt-3">
        <el-collapse-item :title="t('prune.advanced')" name="advanced">
          <label class="text-xs text-content-muted" for="prune-label">{{ t('prune.label.title') }}</label>
          <el-input
            id="prune-label"
            v-model="label"
            class="mt-1"
            size="small"
            :placeholder="t('prune.label.placeholder')"
          />
          <p class="mt-1 text-xs text-content-faint">{{ t('prune.label.hint') }}</p>
        </el-collapse-item>
      </el-collapse>

      <!--
        填了标签过滤时的必读提示：预览是全量候选，实际只清理带该标签的资源。
        不说清楚就是隐性谎报——用户会以为清单里那些都会被删。
      -->
      <p v-if="hasLabelFilter" class="mt-3 text-xs text-status-warn">
        {{ t('prune.label.previewNote') }}
      </p>

      <div class="mt-4 flex items-baseline justify-between">
        <span class="text-sm font-medium text-content-strong">
          {{ t('prune.candidates', { count: preview.candidates.length }) }}
        </span>
        <span class="text-xs text-content-muted">
          {{ t('prune.reclaimable', { size: formatBytes(preview.reclaimable, byteUnits) }) }}
        </span>
      </div>

      <p v-if="preview.candidates.length === 0 && !loading" class="mt-2 text-sm text-content-faint">
        {{ t('prune.empty') }}
      </p>

      <ul v-else class="mt-2 max-h-56 space-y-1 overflow-y-auto">
        <li
          v-for="item in preview.candidates"
          :key="item.id"
          class="flex items-baseline justify-between gap-3 text-xs"
        >
          <span class="min-w-0 break-all font-mono text-content-muted">
            {{ pruneCandidateLabel(item) }}
          </span>
          <span class="shrink-0 text-content-faint">
            {{ formatBytes(item.size, byteUnits) }}
          </span>
        </li>
      </ul>

      <!-- 用量未知的条目：不进候选，但必须让人知道有它们存在 -->
      <p v-if="preview.unknownCount > 0" class="mt-2 text-xs text-status-warn">
        {{ t('prune.unknown', { count: preview.unknownCount }) }}
      </p>

      <!-- 卷特有：带挂载选项的卷不会被清理 -->
      <p v-if="!isImages" class="mt-2 text-xs text-content-faint">
        {{ t('prune.bindMount') }}
      </p>
    </div>

    <template #footer>
      <template v-if="result !== null">
        <el-button @click="close">{{ t('prune.close') }}</el-button>
      </template>
      <template v-else>
        <el-button :disabled="submitting" @click="close">{{ t('common.cancel') }}</el-button>
        <el-button
          type="danger"
          :loading="submitting"
          :disabled="!canSubmit"
          @click="submit"
        >
          {{ t('prune.confirm') }}
        </el-button>
      </template>
    </template>
  </el-dialog>
</template>
