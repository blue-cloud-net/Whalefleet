<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { inspectContainer, recreateContainer, type CreateContainerPayload } from '@/api/container'
import ContainerConfigForm from '@/components/business/ContainerConfigForm.vue'
import { detailToConfig } from '@/utils/container-config'

/**
 * 「编辑并重建」对话框（0.7.0）。
 *
 * ## 为什么它必然是「重建」而不是「热更新」
 *
 * Docker 只有 `update` 一个热改端点，而它**只接受资源限制与重启策略**——
 * 镜像、命令、端口、挂载、网络都改不了。要改那些字段只能重建，
 * 而重建会让容器 ID 变。因此这个对话框的标题与提示都必须写明「重建」，
 * 不能让用户以为「改完保存就行」。
 *
 * ## 为什么表单只发「改过的字段」
 *
 * 后端是「请求里出现的字段才覆盖」的语义。这样才能保住那些
 * **界面上根本没展示**的字段（capabilities、sysctls、ulimits、devices、
 * 日志驱动、健康检查）——否则用户「只是改了个环境变量」就把它们清空了。
 * 裁剪逻辑在 `ContainerConfigForm` 里（它比的是表单内部状态，
 * 因为 `cmd` 在表单里是一行文本，用组装结果去比会误判）。
 */
const { t } = useI18n()

const props = defineProps<{
  hostId: string
  containerId: string | null
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{
  recreated: [result: { containerId: string; replacedContainerId: string | null }]
}>()

const loading = ref(false)
const submitting = ref(false)
/** 表单初值（详情投影转成的配置）；`null` = 还没加载好。 */
const initial = ref<CreateContainerPayload | null>(null)
/** 表单是否可提交。 */
const formValid = ref(false)
/** 表单组件实例：提交动作由它自己校验。 */
const formRef = ref<InstanceType<typeof ContainerConfigForm> | null>(null)

const canSubmit = computed(() => !loading.value && !submitting.value && !!initial.value)

watch(visible, (open) => {
  if (open) {
    initial.value = null
    formValid.value = false
    void load()
  }
})

/**
 * 读取容器当前配置并填进表单。
 *
 * ⚠️ **失败时保持对话框打开**：用户是冲着「改点东西」来的，
 * 一读不到配置就默默关掉对话框，用户只会以为「这功能坏了」。
 */
async function load(): Promise<void> {
  if (!props.containerId) return

  loading.value = true
  try {
    const detail = await inspectContainer(props.hostId, props.containerId)
    initial.value = detailToConfig(detail)
  } catch {
    // 错误由全局拦截器提示
    initial.value = null
  } finally {
    loading.value = false
  }
}

async function submit(payload: CreateContainerPayload): Promise<void> {
  submitting.value = true

  try {
    const result = await recreateContainer(props.hostId, props.containerId ?? '', payload)

    visible.value = false
    ElMessage({
      type: 'success',
      message: t('container.recreate.done', { name: result.name }),
      showClose: true
    })
    emit('recreated', { containerId: result.containerId, replacedContainerId: result.replacedContainerId })
  } catch {
    // 错误由全局拦截器提示；对话框保持打开，让用户改完再提交。
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('container.edit.title')"
    width="min(720px, 94vw)"
    top="6vh"
    append-to-body
  >
    <div class="mb-3 rounded-md border border-status-warn/40 bg-status-warn/10 px-3 py-2">
      <p class="text-sm font-medium text-status-warn">
        {{ t('container.recreate.warning') }}
      </p>
      <p class="mt-1 text-xs text-content-muted">
        {{ t('container.edit.hint') }}
      </p>
    </div>

    <div v-loading="loading" class="min-h-40">
      <p v-if="!loading && !initial" class="py-6 text-center text-sm text-content-faint">
        {{ t('container.edit.loading') }}
      </p>

      <ContainerConfigForm
        v-else
        ref="formRef"
        :host-id="hostId"
        :initial="initial"
        :show-name="false"
        @submit="submit"
        @validity-change="formValid = $event"
      />
    </div>

    <template #footer>
      <el-button @click="visible = false">
        {{ t('common.cancel') }}
      </el-button>
      <el-button
        type="primary"
        :loading="submitting"
        :disabled="!canSubmit || !formValid"
        @click="formRef?.submit()"
      >
        {{ t('container.edit.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
