<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { createHostTag, deleteHostTag, listHostTags } from '@/api/host-tag'
import type { HostTag } from '@/types/api'

/**
 * 标签管理（0.6.0）：列出 / 新建 / 删除。
 *
 * 语义要点：
 * - **删除标签不删主机**，但会解除它与全部主机的关联（后端先清关联再软删）；
 * - 名称重复 → `1307`，颜色只收 `#RRGGBB`（后端与这里同一口径）；
 * - 标签**不参与授权**：它只是过滤与批量选择用的自由标记。
 */
const props = defineProps<{ modelValue: boolean }>()

const emit = defineEmits<{ 'update:modelValue': [boolean]; changed: [] }>()

const { t } = useI18n()

const tags = ref<HostTag[]>([])
const loading = ref(false)
const saving = ref(false)

const name = ref('')
const color = ref('#22C55E')

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
  (open) => {
    if (open) void load()
  }
)

async function create(): Promise<void> {
  if (name.value.trim() === '') return

  saving.value = true
  try {
    await createHostTag({ name: name.value.trim(), color: color.value || null })
    name.value = ''
    await load()
    emit('changed')
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    saving.value = false
  }
}

async function remove(tag: HostTag): Promise<void> {
  try {
    await ElMessageBox.confirm(t('host.tag.deleteConfirm'), t('host.tag.title'), { type: 'warning' })
  } catch {
    return
  }

  try {
    const affected = await deleteHostTag(tag.id)

    // 回带被解除关联的主机数：否则管理员删完标签会以为主机也跟着没了
    ElMessage.success(t('host.tag.removed', { count: affected }))
    await load()
    emit('changed')
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="t('host.tag.title')"
    width="520px"
    @update:model-value="emit('update:modelValue', $event)"
  >
    <div class="mb-3 flex items-center gap-2">
      <el-input v-model="name" size="small" :placeholder="t('host.tag.name')" class="flex-1" />
      <el-color-picker v-model="color" size="small" />
      <el-button size="small" type="primary" :loading="saving" @click="create">
        {{ t('host.tag.create') }}
      </el-button>
    </div>

    <el-table v-loading="loading" :data="tags" size="small" max-height="320">
      <el-table-column :label="t('host.tag.name')" min-width="150">
        <template #default="{ row }">
          <span class="inline-flex items-center gap-1.5">
            <span
              class="h-2 w-2 rounded-full shrink-0"
              :style="{ backgroundColor: row.color ?? 'var(--brand-500)' }"
              aria-hidden="true"
            />
            {{ row.name }}
          </span>
        </template>
      </el-table-column>

      <el-table-column :label="t('host.tag.hostCount')" width="90">
        <template #default="{ row }">
          <span class="app-tabular text-xs text-content-muted">{{ row.hostCount }}</span>
        </template>
      </el-table-column>

      <el-table-column width="60">
        <template #default="{ row }">
          <el-button text size="small" @click="remove(row)">
            <span class="i-carbon-trash-can text-sm" />
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <p v-if="tags.length === 0 && !loading" class="mt-3 text-xs text-content-faint">
      {{ t('host.tag.empty') }}
    </p>

    <template #footer>
      <el-button @click="close">{{ t('common.close') }}</el-button>
    </template>
  </el-dialog>
</template>
