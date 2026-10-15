<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { pullImage } from '@/api/image'
import { splitImageReference } from '@/utils/image-display'
import type { ImagePullResult } from '@/types/api'

/**
 * 拉取镜像对话框（0.0.12）。
 *
 * ## 为什么把「仓库:标签」拆成两条参数在这里做
 * 后端规则是：`tag` 省略时按 `latest`；**若 `image` 已含标签又另传 `tag`，返回 `1000`/400**
 * （而不是拼成 `nginx:1.27:alpine`——那是 Docker 会接受但语义完全不是用户所想的写法）。
 * 因此本对话框只留**一个**输入框：用户怎么写都行，由 `splitImageReference()`
 * 负责拆成两条参数。分两个输入框会逼用户先理解后端的参数划分，
 * 而他们脑子里想的就是一条 `nginx:1.27`。
 *
 * ## 0.0.10 的拉取是一次性请求
 * 服务端聚合进度后一次性返回，期间 HTTP 一直挂着——所以这里只能给不确定进度
 * （转圈 + 「拉取中」）。SignalR 实时进度排 0.4.0，届时换成进度条即可，
 * 本对话框的对外契约（`pulled` 事件）不需要改。
 */
const props = defineProps<{
  /** 当前主机 id（等于主机名） */
  hostId: string
}>()

/** 与父组件的 `v-model` 对接；组件只有一个开关，用默认的 `modelValue`。 */
const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ pulled: [ImagePullResult] }>()

const { t } = useI18n()

const reference = ref('')
const platform = ref('')
const submitting = ref(false)
const errorMessage = ref<string | null>(null)

/** 实时预览拆解结果：让用户提交前就看到「会去拉什么」。 */
const preview = computed(() => {
  const { image, tag } = splitImageReference(reference.value)

  if (image.length === 0) return null

  return { image, tag: tag ?? 'latest' }
})

watch(visible, (open) => {
  if (!open) return

  errorMessage.value = null
  platform.value = ''
})

async function submit(): Promise<void> {
  const { image, tag } = splitImageReference(reference.value)

  if (image.length === 0) {
    errorMessage.value = t('image.pull.required')
    return
  }

  submitting.value = true
  errorMessage.value = null
  try {
    const result = await pullImage(props.hostId, {
      image,
      tag,
      platform: platform.value.trim() || undefined
    })

    ElMessage({
      type: 'success',
      message: t('image.pull.done', { reference: result.reference }),
      showClose: true
    })
    reference.value = ''
    emit('pulled', result)
  } catch (e) {
    // 两类失败都要能看懂：1401「镜像 / 仓库不存在」、1402「仓库拒绝或网络中断」。
    // 后者在 Docker 侧 HTTP 仍是 200，错误只在进度流里，由后端翻译——因此这里的
    // 文案要能容纳「被拒绝」这种非网络原因。
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
    :title="t('image.pull.title')"
    width="min(520px, 92vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent>
      <el-form-item :label="t('image.pull.reference')" required>
        <el-input
          v-model="reference"
          :placeholder="t('image.pull.referencePlaceholder')"
          :disabled="submitting"
          @keyup.enter="submit"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('image.pull.referenceHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('image.pull.platform')">
        <el-input
          v-model="platform"
          :placeholder="t('image.pull.platformPlaceholder')"
          :disabled="submitting"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('image.pull.platformHint') }}</p>
      </el-form-item>

      <!-- 提交前把拆解结果摆出来：否则用户看到「已拉取 nginx:latest」才发现自己写的标签被吃掉了 -->
      <div v-if="preview" class="flex items-center justify-between gap-2 text-xs">
        <span class="text-content-muted">{{ t('image.pull.preview') }}</span>
        <span class="font-mono text-content-body">{{ preview.image }}:{{ preview.tag }}</span>
      </div>

      <el-alert
        v-if="errorMessage"
        class="mt-3"
        type="error"
        :closable="false"
        :title="errorMessage"
      />

      <p v-if="submitting" class="mt-3 text-xs text-content-muted">
        {{ t('image.pull.waiting') }}
      </p>
    </el-form>

    <template #footer>
      <el-button :disabled="submitting" @click="close">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="submitting" @click="submit">
        {{ submitting ? t('image.pull.submitting') : t('image.action.pull') }}
      </el-button>
    </template>
  </el-dialog>
</template>
