<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { assignHostTags, listHostTags, revokeHostTags } from '@/api/host-tag'
import type { AdminHostSummary, HostTag } from '@/types/api'

/**
 * 给单台主机打标签 / 清标签（0.6.0）。
 *
 * 语义要点：
 * - **幂等**：重复打同一个标签不报错（服务端 `INSERT OR IGNORE`）；
 * - **前端只算差集**：`el-select` 多选给出的是「最终应有」，而接口是「加 / 减」，
 *   因此这里分别调用 assign 与 revoke，而不是「先全清再全打」——后者会把别人刚打的标签
 *   也一并清掉。
 * - **请求体字段是主机名**（`hostIds: [host.name]`），不是主键——与路径里的 `hostId` 同口径。
 */
const props = defineProps<{
  modelValue: boolean
  /** 目标主机（管理端点的主机对象；`name` 才是接口要的主机名） */
  host: AdminHostSummary | null
  /** 该主机当前挂着的标签主键 */
  tagIds: string[]
}>()

const emit = defineEmits<{ 'update:modelValue': [boolean]; saved: [] }>()

const { t } = useI18n()

const tags = ref<HostTag[]>([])
const selected = ref<string[]>([])
const saving = ref(false)
const loading = ref(false)

const close = () => emit('update:modelValue', false)

async function load(): Promise<void> {
  loading.value = true
  try {
    tags.value = await listHostTags()
  } finally {
    loading.value = false
  }
}

watch(
  () => props.modelValue,
  async (open) => {
    if (!open) return

    selected.value = [...props.tagIds]
    await load()
  }
)

async function save(): Promise<void> {
  const host = props.host
  if (!host) return

  const before = new Set(props.tagIds)
  const after = new Set(selected.value)

  const added = selected.value.filter((id) => !before.has(id))
  const removed = props.tagIds.filter((id) => !after.has(id))

  saving.value = true
  try {
    if (added.length > 0) {
      await assignHostTags({ hostIds: [host.name], tagIds: added })
    }

    if (removed.length > 0) {
      await revokeHostTags({ hostIds: [host.name], tagIds: removed })
    }

    ElMessage.success(t('host.tag.assigned'))
    emit('saved')
    close()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="`${t('host.tag.assign')} · ${host?.name ?? ''}`"
    width="420px"
    @update:model-value="emit('update:modelValue', $event)"
  >
    <el-select
      v-model="selected"
      multiple
      filterable
      class="w-full"
      :loading="loading"
      :placeholder="t('host.tag.title')"
    >
      <el-option v-for="tag in tags" :key="tag.id" :label="tag.name" :value="tag.id" />
    </el-select>

    <p v-if="tags.length === 0" class="mt-2 text-xs text-content-faint">
      {{ t('host.tag.empty') }}
    </p>

    <template #footer>
      <el-button @click="close">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="saving" @click="save">{{ t('common.save') }}</el-button>
    </template>
  </el-dialog>
</template>
