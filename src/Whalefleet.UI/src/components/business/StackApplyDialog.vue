<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { StackSummary } from '@/types/api'

/**
 * 编排「应用」对话框（0.10.0）。
 *
 * ## 为什么应用前要问两件事
 * 这两个选项的后果**不可逆且不可见**：
 *
 * - **拉取最新镜像**：勾上后如果拉取失败，**整次应用失败**（不静默回退到本地旧镜像）——
 *   用户点了「拉最新」而实际跑的仍是旧版本，是最糟的组合。
 * - **清理多余容器**：删掉「已经不在 YAML 里的」容器。
 *   它看起来只是清理，实际上会**停掉正在提供服务的容器**——
 *   而用户可能只是临时从 YAML 里删了一行。
 *
 * 因此它们是**显式的勾选项**，而不是隐藏在参数里的默认行为。
 *
 * ## 为什么默认「拉取最新」不勾、「清理多余」勾上
 * - 拉取最新会让每次应用都出网并花时间；不勾时的语义是「本地有就用本地的」，
 *   这与 `docker compose up -d` 的默认一致。
 * - 清理多余**勾上**：`docker compose up -d` 的默认行为就是清掉多余的容器，
 *   而「多出来的容器」正是用户点「应用」最常期待的结果。
 *   ⚠️ **数据卷不在清理范围内**——卷里是用户的数据，删它的后果不可逆。
 */
const props = defineProps<{
  /** 模式：部署（新建）或更新（重新应用）。 */
  mode: 'deploy' | 'update'
  /** 更新时对应的 Stack；部署时为 null。 */
  stack?: StackSummary | null
  /** 更新时预填的 YAML 原文。 */
  composeContent?: string | null
}>()

/** 与父组件的 `v-model:visible` 对接。 */
const visible = defineModel<boolean>('visible', { required: true })

const emit = defineEmits<{
  submit: [
    payload: {
      name: string
      composeContent: string
      pullAlways: boolean
      pruneOrphans: boolean
    }
  ]
}>()

const { t } = useI18n()

/** Stack 名称（Compose 项目名）。 */
const name = ref('')

/** Compose 文件原文。 */
const content = ref('')

/** 是否拉取最新镜像。 */
const pullAlways = ref(false)

/** 是否清理多余容器。 */
const pruneOrphans = ref(true)

/** 提交中。 */
const submitting = ref(false)

/** 错误消息。 */
const errorMessage = ref<string | null>(null)

/** 是否是更新模式。 */
const isUpdate = computed(() => props.mode === 'update')

/** 标题。 */
const title = computed(() =>
  isUpdate.value ? t('stack.apply.updateTitle') : t('stack.apply.deployTitle')
)

/** 提交按钮文案。 */
const submitLabel = computed(() =>
  isUpdate.value ? t('stack.action.update') : t('stack.action.deploy')
)

/** 名称是否合法（与后端的 Compose 项目名规则一致）。 */
const nameValid = computed(() => /^[a-z0-9][a-z0-9_-]*$/.test(name.value.trim()))

watch(visible, (open) => {
  if (!open) return

  errorMessage.value = null
  submitting.value = false

  if (isUpdate.value) {
    name.value = props.stack?.name ?? ''
    content.value = props.composeContent ?? ''
    // 更新时默认清理多余容器（与 docker compose up -d 一致）
    pruneOrphans.value = true
  } else {
    name.value = ''
    content.value = ''
    pruneOrphans.value = true
  }

  pullAlways.value = false
})

/**
 * 提交。
 *
 * ⚠️ **不自己发请求**：父组件拿任务标识去发（任务标识必须是同一个令牌，
 * 既用于订阅进度也用于查任务快照），因此这里只把内容交出去。
 */
function submit(): void {
  errorMessage.value = null

  const trimmedName = name.value.trim()
  const trimmedContent = content.value.trim()

  if (!nameValid.value) {
    errorMessage.value = t('stack.apply.nameInvalid')
    return
  }

  if (trimmedContent.length === 0) {
    errorMessage.value = t('stack.apply.contentRequired')
    return
  }

  submitting.value = true

  emit('submit', {
    name: trimmedName,
    composeContent: trimmedContent,
    pullAlways: pullAlways.value,
    pruneOrphans: pruneOrphans.value
  })
}

/** 关闭。 */
function close(): void {
  visible.value = false
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="title"
    width="min(760px, 94vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent>
      <el-form-item :label="t('stack.apply.name')" required>
        <el-input
          v-model="name"
          :placeholder="t('stack.apply.namePlaceholder')"
          :disabled="submitting || isUpdate"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('stack.apply.nameHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('stack.apply.content')" required>
        <el-input
          v-model="content"
          type="textarea"
          :rows="14"
          :placeholder="t('stack.apply.contentPlaceholder')"
          :disabled="submitting"
          class="font-mono text-xs"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('stack.apply.contentHint') }}</p>
      </el-form-item>

      <!--
        两个选项**必须显式摆在用户面前**：
        它们的副作用（拉最新可能失败、清理多余会停掉容器）都不直观，
        藏在参数默认值里等于让用户替自己做了不可逆的决定。
      -->
      <el-form-item :label="t('stack.apply.options')">
        <div class="flex flex-col gap-3">
          <el-checkbox v-model="pullAlways" :disabled="submitting">
            {{ t('stack.apply.pullAlways') }}
          </el-checkbox>
          <p class="-mt-2 pl-6 text-xs text-content-muted">
            {{ t('stack.apply.pullAlwaysHint') }}
          </p>

          <el-checkbox v-model="pruneOrphans" :disabled="submitting">
            {{ t('stack.apply.pruneOrphans') }}
          </el-checkbox>
          <p class="-mt-2 pl-6 text-xs text-content-muted">
            {{ t('stack.apply.pruneOrphansHint') }}
          </p>
        </div>
      </el-form-item>

      <el-alert
        v-if="errorMessage"
        class="mt-1"
        type="error"
        :closable="false"
        :title="errorMessage"
      />
    </el-form>

    <template #footer>
      <el-button @click="close">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="submitting" @click="submit">
        {{ submitLabel }}
      </el-button>
    </template>
  </el-dialog>
</template>
