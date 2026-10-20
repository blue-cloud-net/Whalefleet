<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { tagImage } from '@/api/image'
import { validateImageTag, validateRepository } from '@/utils/advanced-input'

/**
 * 镜像打标签对话框（0.3.0）。
 *
 * ## 仓库名与标签**必须分成两个输入框**
 * Docker 的 API 要的是「纯仓库名 + 标签」两半，而用户脑子里的「镜像」是一个
 * `nginx:1.27` 整体。合成一个输入框的话，最自然的用法就是把现有的引用粘进去，
 * 于是后端收到 `repository = "nginx:1.27"` + `tag = "alpine"`——
 * 而拼接逻辑会打出一个谁都没要求过的 `nginx:1.27:alpine`。
 *
 * 因此本对话框：
 * - 仓库名输入框**预先去掉标签部分**，并把原标签填进标签框；
 * - 提交前用 `validateRepository` 再拦一道（用户仍可能手动补上冒号）。
 *
 * ## 提示语要说明「tag 不复制镜像」
 * 新标签与源标签指向**同一个镜像 ID**，磁盘占用不变。不说清楚的话，
 * 用户会以为多了一份副本，进而在删「副本」时把本体一起删掉。
 */
const { t } = useI18n()

const props = defineProps<{
  hostId: string
  imageId: string | null
  /** 源镜像引用（如 `nginx:latest`），用于预填与提示 */
  currentReference: string
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ tagged: [imageId: string] }>()

const repository = ref('')
const tag = ref('')
const submitting = ref(false)

/** 字段级错误：错误 i18n key，由模板 `t()` 取文案。 */
const repositoryError = ref<string | null>(null)
const tagError = ref<string | null>(null)

const canSubmit = computed(
  () => repository.value.trim().length > 0 && !submitting.value
)

watch(visible, (open) => {
  if (!open) return

  repositoryError.value = null
  tagError.value = null

  // 预填：从源引用里把「仓库」与「标签」拆开，各自落到对应输入框。
  // 不预填的话用户得先想清楚仓库长什么样，而界面上没有任何线索指向它。
  const reference = props.currentReference
  const colon = reference.lastIndexOf(':')
  const slash = reference.lastIndexOf('/')

  if (colon > slash) {
    repository.value = reference.slice(0, colon)
    tag.value = reference.slice(colon + 1)
  } else {
    repository.value = reference
    tag.value = 'latest'
  }
})

function validate(): boolean {
  repositoryError.value = validateRepository(repository.value.trim())
  tagError.value = validateImageTag(tag.value.trim())

  return repositoryError.value === null && tagError.value === null
}

async function submit(): Promise<void> {
  if (!validate()) return

  submitting.value = true

  try {
    const result = await tagImage(
      props.hostId,
      props.imageId ?? '',
      repository.value.trim(),
      tag.value.trim() || undefined
    )

    visible.value = false
    ElMessage({ type: 'success', message: t('image.tag.done', { reference: result.reference }) })
    emit('tagged', result.imageId)
  } catch {
    // 错误由全局拦截器提示（镜像不存在是 1401，标签格式是 1000）；对话框保持打开。
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('image.tag.title')"
    :width="480"
    @close="visible = false"
  >
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="t('image.tag.repository')" :error="repositoryError ? t(repositoryError) : undefined">
        <el-input v-model="repository" :placeholder="t('image.tag.repositoryPlaceholder')" autofocus />
      </el-form-item>

      <el-form-item :label="t('image.tag.tag')" :error="tagError ? t(tagError) : undefined">
        <el-input v-model="tag" :placeholder="t('image.tag.tagPlaceholder')" />
      </el-form-item>

      <p class="text-xs text-content-muted">
        {{ t('image.tag.hint', { reference: currentReference }) }}
      </p>
    </el-form>

    <template #footer>
      <el-button @click="visible = false">
        {{ t('common.cancel') }}
      </el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ t('image.tag.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
