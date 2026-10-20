<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { addContainerVolumes } from '@/api/container'
import VolumeMountEditor from '@/components/business/VolumeMountEditor.vue'
import { toVolumePayload, type VolumeMount } from '@/utils/container-input'

/**
 * 为已存在容器追加挂载（0.3.0）。
 *
 * ## ⚠️ 这个对话框会重建容器，提示必须放在最显眼处
 * Docker 没有「给已存在容器加挂载」的接口——只能重建，因此**容器 ID 一定变**。
 * 用户如果只看到「追加挂载成功」，回到列表会发现原来的行消失了、冒出一个陌生容器，
 * 而他并没有被告知过任何东西被替换。因此：
 * - 标题下方第一行就说「会重建」；
 * - 成功后提示里带上**新容器名**与「旧容器已改名保留、可自行删除」这句。
 *
 * ## 为什么文案要强调「旧容器保留」
 * 后端把旧容器**改名保留**（`whalefleet-replaced-*`）而不是删除——那是重建失败时的后手。
 * 成功后它会以一个陌生名字留在列表里，不说清楚的话用户既不敢删、也不知道是什么。
 */
const { t } = useI18n()

const props = defineProps<{
  hostId: string
  containerId: string | null
  /** 容器当前名字（重建后会沿用这个名字） */
  currentName: string
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ recreated: [replacedContainerId: string | null] }>()

const volumeEntries = ref<VolumeMount[]>([])
/** 出错的行号（`null` = 全部合法）；与 `CreateContainerDialog` 共用同一个编辑器契约。 */
const volumeError = ref<number | null>(null)
const submitting = ref(false)

/**
 * 是否随重建一起启动。
 *
 * ⚠️ 刻意**不给默认值**：省略时后端沿用原容器的运行状态，
 * 而擅自启动一个本来就没运行的容器是用户没要求的副作用。
 */
const start = ref<boolean | null>(null)

const hasInputError = computed(() => volumeError.value !== null)

const canSubmit = computed(
  () => volumeEntries.value.length > 0 && !hasInputError.value && !submitting.value
)

watch(visible, (open) => {
  if (!open) return

  volumeEntries.value = []
  volumeError.value = null
  start.value = null
})

async function submit(): Promise<void> {
  const volumes = toVolumePayload(volumeEntries.value)

  submitting.value = true

  try {
    const result = await addContainerVolumes(
      props.hostId,
      props.containerId ?? '',
      volumes,
      start.value ?? undefined
    )

    visible.value = false

    // ⚠️ `replacedContainerId` 非空即「发生了重建」，这正是要告诉用户的那件事。
    ElMessage({
      type: 'success',
      message: result.replacedContainerId
        ? t('container.addVolumes.doneRecreated', { name: result.name })
        : t('container.addVolumes.done'),
      showClose: true
    })

    emit('recreated', result.replacedContainerId)
  } catch {
    // 错误由全局拦截器提示（例如目标路径重复 → 1000，它会指明是哪个路径）。
    // 对话框保持打开，让用户改完再提交。
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('container.addVolumes.title')"
    :width="560"
    @close="visible = false"
  >
    <div class="mb-3 rounded-md border border-status-warn/40 bg-status-warn/10 px-3 py-2">
      <p class="text-sm font-medium text-status-warn">
        {{ t('container.addVolumes.warning') }}
      </p>
      <p class="mt-1 text-xs text-content-muted">
        {{ t('container.addVolumes.warningHint', { name: currentName }) }}
      </p>
    </div>

    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="t('container.addVolumes.volumes')">
        <VolumeMountEditor
          v-model="volumeEntries"
          v-model:error-line="volumeError"
          :host-id="hostId"
        />
      </el-form-item>

      <el-form-item>
        <el-checkbox v-model="start" :true-value="true" :false-value="false">
          {{ t('container.addVolumes.start') }}
        </el-checkbox>
        <p class="mt-1 text-xs text-content-muted">
          {{ t('container.addVolumes.startHint') }}
        </p>
      </el-form-item>
    </el-form>

    <template #footer>
      <el-button @click="visible = false">
        {{ t('common.cancel') }}
      </el-button>
      <el-button type="warning" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ t('container.addVolumes.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
