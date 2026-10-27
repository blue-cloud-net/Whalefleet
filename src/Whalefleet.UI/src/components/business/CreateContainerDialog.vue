<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import {
  createContainer,
  startContainer,
  type CreateContainerPayload
} from '@/api/container'
import ContainerConfigForm from '@/components/business/ContainerConfigForm.vue'

/**
 * 新建容器对话框（0.0.11，0.0.14 重排输入区，0.7.0 抽出表单组件）。
 *
 * ## 为什么表单被抽走了
 *
 * 0.7.0 的「编辑并重建」需要**同一份表单**——只是初值不同、提交目标不同。
 * 两处各写一份的话，改一处忘了另一处，症状是「新建表单多了个字段、编辑表单没有」，
 * 而用户看到的只是「编辑保存后那个字段没生效」。
 *
 * 因此表单搬到了 `ContainerConfigForm`，本组件退化成「对话框外壳」：
 * 负责成功后那一下「要不要顺手启动」的二次状态、以及底部按钮。
 *
 * ## 端口 / 挂载 / 环境变量为什么用行式输入
 *
 * 真实的 `docker run` 命令就是这种行式输入，而嵌套表单控件（端口要两行、协议要下拉、
 * 还要能加第二个端口）在一行里塞不下、读起来还比命令行难懂。**降低输入成本**在这里
 * 比「结构化表单好看」更重要：用户绝大多数情况是把一条现成的命令行粘进来。
 *
 * 0.0.14 把这三个字段从「对话框内部的一组文本框」换成三个**双模编辑器**
 * （`EnvVarEditor` / `PortMappingEditor` / `VolumeMountEditor`）：
 * 批量粘贴为主、逐行编辑为辅，并且解析逻辑下沉到 `utils/container-input.ts`，
 * 两条路径共用同一份校验——否则「粘贴能建、手填建不了」的不一致迟早出现。
 *
 * 因此表单组件不再自带任何行解析函数，只负责把结构化结果映射成请求体。
 *
 * ## 关于「高级选项」折叠
 *
 * 后端 `create` 只暴露精选字段（见 `CreateContainerPayload`），表单与它**一一对应**，
 * 不做「前端支持但后端忽略」的开关——那会让用户以为某项生效了。
 */
const { t } = useI18n()

const props = defineProps<{
  /** 主机标识（主机名） */
  hostId: string
}>()

/**
 * 开关状态用**默认**的 `modelValue`（即 `v-model`），而不是 `v-model:visible`。
 *
 * 刻意如此：`v-model:visible` 要求调用方写全命名参数，而组件只有一个开关，
 * 命名收益为零、写错的风险（漏写导致 `visible` 缺失警告）却是实在的。
 */
const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{
  /** 创建成功（`containerId` 用于「立即启动」） */
  created: [containerId: string]
}>()

const submitting = ref(false)
const createdContainerId = ref<string | null>(null)
const warnings = ref<string[]>([])

/** 表单组件实例：提交动作由它自己校验，这里只调它的 `submit()`。 */
const formRef = ref<InstanceType<typeof ContainerConfigForm> | null>(null)

/** 表单是否可提交（表单自己算，这里只用来禁用底部按钮）。 */
const formValid = ref(false)

// 每次打开都清空上一次的残留：否则用户上次填到一半关掉，下次打开会看到一堆他不想要的值。
watch(visible, (open) => {
  if (open) {
    formRef.value?.reset()
    createdContainerId.value = null
    warnings.value = []
  }
})

async function submit(payload: CreateContainerPayload): Promise<void> {
  submitting.value = true
  try {
    const result = await createContainer(props.hostId, payload)

    createdContainerId.value = result.containerId
    // ⚠️ warnings **不是错误**：创建已经成功。这些提示（例如「端口未映射」）恰恰是
    // 「容器起来了但访问不通」的原因，必须让用户看见，但绝不能因此把表单当成失败。
    warnings.value = result.warnings ?? []
    ElMessage({ type: 'success', message: t('container.create.success') })
  } catch {
    // 错误提示已由 axios 拦截器统一弹出，这里保持对话框打开让用户能改。
    createdContainerId.value = null
  } finally {
    submitting.value = false
  }
}

/** 创建后接着启动：省掉「先创建、再去列表点启动」两步。 */
async function createAndStart(): Promise<void> {
  const containerId = createdContainerId.value
  if (!containerId) return

  submitting.value = true
  try {
    await startContainer(props.hostId, containerId)
    emit('created', containerId)
  } finally {
    submitting.value = false
  }
}

/** 只创建，稍后手动启动。 */
function createOnly(): void {
  const containerId = createdContainerId.value
  if (containerId) emit('created', containerId)
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('container.create.title')"
    width="min(720px, 94vw)"
    top="6vh"
    append-to-body
  >
    <!-- 创建成功后的二次状态：不再显示表单，避免「再点一次创建」重复创建。 -->
    <div v-if="createdContainerId" class="flex flex-col gap-4 py-2">
      <el-alert type="success" :closable="false" :title="t('container.create.success')" />

      <div v-if="warnings.length > 0">
        <p class="mb-1.5 text-xs font-medium text-content-muted">
          {{ t('container.create.warnings') }}
        </p>
        <ul class="app-warn-list">
          <li v-for="(warning, index) in warnings" :key="index" class="text-xs">{{ warning }}</li>
        </ul>
      </div>
    </div>

    <ContainerConfigForm
      v-else
      ref="formRef"
      :host-id="hostId"
      @submit="submit"
      @validity-change="formValid = $event"
    />

    <template #footer>
      <div v-if="createdContainerId" class="flex justify-end gap-2">
        <el-button @click="createOnly">{{ t('common.close') }}</el-button>
        <el-button type="primary" :loading="submitting" @click="createAndStart">
          {{ t('container.create.startNow') }}
        </el-button>
      </div>
      <div v-else class="flex justify-end gap-2">
        <el-button @click="visible = false">{{ t('common.cancel') }}</el-button>
        <el-button
          type="primary"
          :loading="submitting"
          :disabled="!formValid"
          @click="formRef?.submit()"
        >
          {{ submitting ? t('container.create.submitting') : t('container.create.submit') }}
        </el-button>
      </div>
    </template>
  </el-dialog>
</template>
