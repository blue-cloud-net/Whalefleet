<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { recreateContainer } from '@/api/container'

/**
 * 重建容器对话框（0.7.0）。
 *
 * ## ⚠️ 这个对话框会重建容器，提示必须放在最显眼处
 *
 * 重建与「追加挂载」是同一类操作的后果：容器 ID 一定变。
 * 用户如果只看到「重建成功」，回到列表会发现原来的行不见了、冒出一个陌生容器，
 * 而他并没有被告知过任何东西被替换。因此：
 * - 标题下方第一行就说「会重建」；
 * - 成功后提示里带上**新容器名**与「旧容器已改名保留、可自行删除」这句。
 *
 * ## 与「编辑并重建」的区别
 *
 * 本对话框**不传配置**（按原配置原样重建），而 `ContainerEditDialog` 传的是
 * 「用户改过的字段」。两者后端走的是同一个端点，差别只在请求里有没有 `config`。
 */
const { t } = useI18n()

const props = defineProps<{
  hostId: string
  containerId: string | null
  /** 容器当前名字（重建后会沿用这个名字） */
  currentName: string
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ recreated: [result: { containerId: string; replacedContainerId: string | null }] }>()

const submitting = ref(false)

/**
 * 是否随重建一起启动。
 *
 * ⚠️ 刻意**不给默认值**：省略时后端沿用原容器的运行状态，
 * 而擅自启动一个本来就没运行的容器是用户没要求的副作用。
 */
const start = ref<boolean | null>(null)

const canSubmit = computed(() => !submitting.value && !!props.containerId)

watch(visible, (open) => {
  if (open) {
    start.value = null
  }
})

async function submit(): Promise<void> {
  submitting.value = true

  try {
    const result = await recreateContainer(
      props.hostId,
      props.containerId ?? '',
      undefined,
      start.value ?? undefined
    )

    visible.value = false
    ElMessage({
      type: 'success',
      message: t('container.recreate.done', { name: result.name }),
      showClose: true
    })
    emit('recreated', { containerId: result.containerId, replacedContainerId: result.replacedContainerId })
  } catch {
    // 错误由全局拦截器提示；对话框保持打开。
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('container.recreate.title')"
    :width="560"
    @close="visible = false"
  >
    <div class="mb-3 rounded-md border border-status-warn/40 bg-status-warn/10 px-3 py-2">
      <p class="text-sm font-medium text-status-warn">
        {{ t('container.recreate.warning') }}
      </p>
      <p class="mt-1 text-xs text-content-muted">
        {{ t('container.recreate.warningHint', { name: currentName }) }}
      </p>
    </div>

    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item>
        <el-checkbox v-model="start">
          {{ t('container.recreate.start') }}
        </el-checkbox>
      </el-form-item>

      <p class="text-xs text-content-muted">
        {{ t('container.recreate.startHint') }}
      </p>
    </el-form>

    <template #footer>
      <el-button @click="visible = false">
        {{ t('common.cancel') }}
      </el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ t('container.recreate.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
