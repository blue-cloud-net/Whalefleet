<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { assignHostTags, listHostTags, revokeHostTags } from '@/api/host-tag'
import type { HostTag } from '@/types/api'

/**
 * 给**多台**主机批量打标签 / 清标签（0.7.0）。
 *
 * ## 为什么只有「追加」与「移除」两个动作，没有「设为」
 * 各台主机当前挂着的标签**可能互不相同**。如果做成「设为某个集合」，
 * 界面就得展示这些主机的标签并集——而并集会让用户误以为
 * 「它们现在的标签是一样的」，然后一次操作就把别人的标签抹掉。
 *
 * 因此这里只提供两个**增量**动作：
 * - 追加：给选中的每一台都加上这些标签（幂等）；
 * - 移除：从选中的每一台上摘掉这些标签（幂等）。
 *
 * 两者都不依赖「这些主机现在是什么状态」，因此不需要展示现状。
 */
const props = defineProps<{
  modelValue: boolean
  /** 目标主机名列表（接口收的是**主机名**，不是主键） */
  hostNames: string[]
}>()

const emit = defineEmits<{ 'update:modelValue': [boolean]; saved: [] }>()

const { t } = useI18n()

const tags = ref<HostTag[]>([])
const added = ref<string[]>([])
const removed = ref<string[]>([])
const saving = ref(false)
const loading = ref(false)

const close = () => emit('update:modelValue', false)

/** 没有任何改动时不发请求：空操作也会产生一次「成功」的假象。 */
const hasChange = computed(() => added.value.length > 0 || removed.value.length > 0)

const canSave = computed(() => hasChange.value && !saving.value)

async function load(): Promise<void> {
  loading.value = true
  try {
    tags.value = await listHostTags()
  } catch {
    tags.value = []
  } finally {
    loading.value = false
  }
}

watch(
  () => props.modelValue,
  (open) => {
    if (!open) return

    added.value = []
    removed.value = []
    void load()
  }
)

async function save(): Promise<void> {
  if (!canSave.value) return

  saving.value = true
  try {
    if (added.value.length > 0) {
      await assignHostTags({ hostIds: props.hostNames, tagIds: added.value })
    }

    if (removed.value.length > 0) {
      await revokeHostTags({ hostIds: props.hostNames, tagIds: removed.value })
    }

    ElMessage.success(t('host.tag.batch.done', { count: props.hostNames.length }))
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
    :title="t('host.tag.batch.title', { count: hostNames.length })"
    width="440px"
    @update:model-value="emit('update:modelValue', $event)"
  >
    <p class="mb-3 text-xs text-content-muted">{{ t('host.tag.batch.hint') }}</p>

    <label class="text-xs text-content-muted" for="host-tag-batch-add">
      {{ t('host.tag.batch.add') }}
    </label>
    <el-select
      id="host-tag-batch-add"
      v-model="added"
      multiple
      filterable
      class="mt-1 w-full"
      :loading="loading"
      :placeholder="t('host.tag.title')"
    >
      <el-option v-for="tag in tags" :key="tag.id" :label="tag.name" :value="tag.id" />
    </el-select>

    <label class="mt-4 block text-xs text-content-muted" for="host-tag-batch-remove">
      {{ t('host.tag.batch.remove') }}
    </label>
    <el-select
      id="host-tag-batch-remove"
      v-model="removed"
      multiple
      filterable
      class="mt-1 w-full"
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
      <el-button type="primary" :loading="saving" :disabled="!canSave" @click="save">
        {{ t('common.save') }}
      </el-button>
    </template>
  </el-dialog>
</template>
