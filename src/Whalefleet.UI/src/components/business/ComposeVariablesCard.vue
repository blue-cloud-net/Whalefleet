<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  createComposeVariable,
  listComposeVariables,
  removeComposeVariable,
  updateComposeVariable
} from '@/api/variables'
import { ApiRequestError } from '@/api'
import { useAuthStore } from '@/stores/auth'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import EmphasisText from '@/components/common/EmphasisText.vue'
import type { EnvVariableDto } from '@/types/api'

/**
 * Compose 项目变量卡片（0.15.0）。
 *
 * ## 三个清单一起展示，因为它们是同一个问题的三个侧面
 * 「这个项目部署时会拿到什么」= 项目自己的变量 + 从全局继承的变量 +
 * **引用了但没定义的变量**。拆成三张卡片会让用户以为它们是三件事。
 *
 * ## 继承来的变量在这里**只读**
 * 它们的家在「全局设置」。在项目页改一条全局变量，会同时改掉所有其它项目的
 * 部署结果——这种「改一处、炸一片」的操作不该藏在某个项目的详情页里。
 *
 * ## 未定义变量是部署前的最后一道提醒
 * 后端遇到 `${VAR}` 而变量不存在时**不会替换成空串**，而是直接报 `1908`——
 * 空串是最糟的结果（容器起来了，配置却是空的）。因此这里必须在点「部署」之前
 * 就把缺的变量名列出来。
 */
const props = defineProps<{
  /** 当前主机标识。 */
  hostId: string
  /** Compose 项目名。 */
  composeName: string
}>()

const emit = defineEmits<{ changed: [] }>()

const { t, te } = useI18n()
const auth = useAuthStore()

/** 项目自己的变量。 */
const project = ref<EnvVariableDto[]>([])

/** 从全局继承的变量。 */
const inherited = ref<EnvVariableDto[]>([])

/** YAML 引用了但没定义（且全局也没有）的变量名。 */
const undefinedNames = ref<string[]>([])

/** 正在加载。 */
const loading = ref(false)

/** 正在提交。 */
const saving = ref(false)

/** 对话框是否可见。 */
const dialogVisible = ref(false)

/** 正在编辑的变量名；`null` 表示新增。 */
const editingName = ref<string | null>(null)

/** 表单。 */
const form = reactive({
  name: '',
  value: '',
  isSecret: false,
  webhookOverridable: false,
  description: ''
})

/** 是否可写（`compose.write` 是这个端点的门槛）。 */
const canWrite = computed(() => auth.hasPermission('compose.write'))

/** 对话框标题。 */
const dialogTitle = computed(() =>
  editingName.value === null ? t('variables.project.add') : t('variables.project.edit')
)

/**
 * 读取错误码对应的提示；未登记的错误码回退到后端消息。
 *
 * @param error 捕获到的异常。
 * @returns 提示文案。
 */
function describeError(error: unknown): string {
  if (error instanceof ApiRequestError) {
    const key = `variables.error.${error.code}`

    if (te(key)) {
      return t(key)
    }

    return error.message
  }

  return t('common.operationFailed')
}

/** 加载三个清单。 */
async function load(): Promise<void> {
  if (!props.hostId || !props.composeName) return

  loading.value = true

  try {
    const data = await listComposeVariables(props.hostId, props.composeName)
    project.value = data.project
    inherited.value = data.inherited
    undefinedNames.value = data.undefined
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    loading.value = false
  }
}

/** 打开新增对话框。 */
function openCreate(): void {
  editingName.value = null
  form.name = ''
  form.value = ''
  form.isSecret = false
  form.webhookOverridable = false
  form.description = ''
  dialogVisible.value = true
}

/**
 * 打开编辑对话框。
 *
 * @param item 待编辑的变量。
 */
function openEdit(item: EnvVariableDto): void {
  editingName.value = item.name
  form.name = item.name
  // 值不回显：留空 = 不改值。
  form.value = ''
  form.isSecret = item.isSecret
  form.webhookOverridable = item.webhookOverridable
  form.description = item.description ?? ''
  dialogVisible.value = true
}

/** 提交表单。 */
async function submit(): Promise<void> {
  const name = form.name.trim()

  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name) || name.length > 64) {
    ElMessage.error(t('variables.error.nameInvalid'))

    return
  }

  saving.value = true

  try {
    const payload = {
      name,
      value: form.value === '' ? null : form.value,
      isSecret: form.isSecret,
      webhookOverridable: form.isSecret ? false : form.webhookOverridable,
      description: form.description.trim() === '' ? null : form.description.trim()
    }

    if (editingName.value === null) {
      await createComposeVariable(props.hostId, props.composeName, payload)
    } else {
      await updateComposeVariable(props.hostId, props.composeName, editingName.value, payload)
    }

    ElMessage.success(t('common.saved'))
    dialogVisible.value = false
    await load()
    emit('changed')
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    saving.value = false
  }
}

/**
 * 删除项目级变量。
 *
 * @param item 待删除的变量。
 */
async function remove(item: EnvVariableDto): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('variables.project.removeConfirm', { name: item.name }),
      t('common.confirmTitle'),
      { type: 'warning' }
    )
  } catch {
    return
  }

  try {
    await removeComposeVariable(props.hostId, props.composeName, item.name)
    ElMessage.success(t('common.deleted'))
    await load()
    emit('changed')
  } catch (error) {
    ElMessage.error(describeError(error))
  }
}

watch(() => [props.hostId, props.composeName], load, { immediate: true })
</script>

<template>
  <SectionCard :title="t('variables.project.title')" :description="t('variables.project.hint')">
    <template #actions>
      <el-button
        v-if="canWrite"
        type="primary"
        size="small"
        icon="i-carbon-add"
        @click="openCreate"
      >
        {{ t('variables.project.add') }}
      </el-button>
    </template>

    <div v-loading="loading" class="space-y-4">
      <!--
        未定义变量必须排在最上面：它是「现在部署会失败」的唯一预告，
        排在两张表下面会被折叠区之外的内容挤到看不见。
      -->
      <div
        v-if="undefinedNames.length > 0"
        class="rounded-lg px-3 py-2 text-xs bg-status-warn-soft/50 text-status-warn ring-1 ring-status-warn/25"
      >
        <EmphasisText :text="t('variables.project.undefined', { names: undefinedNames.join(', ') })" />
      </div>

      <div v-if="project.length === 0 && inherited.length === 0 && undefinedNames.length === 0">
        <EmptyState
          icon="i-carbon-value-variable"
          :description="t('variables.project.empty')"
          compact
        />
      </div>

      <template v-else>
        <div v-if="project.length > 0">
          <h3 class="mb-2 text-xs font-semibold text-content-strong">
            {{ t('variables.project.own') }}
          </h3>
          <el-table :data="project" size="small">
            <el-table-column prop="name" :label="t('variables.name')" min-width="150">
              <template #default="{ row }">
                <span class="font-mono text-xs">{{ row.name }}</span>
              </template>
            </el-table-column>
            <el-table-column :label="t('variables.value')" min-width="110">
              <template #default="{ row }">
                <span v-if="row.hasValue" class="font-mono text-xs">{{ row.valueMasked }}</span>
                <span v-else class="text-content-faint">—</span>
              </template>
            </el-table-column>
            <el-table-column :label="t('variables.flags')" min-width="140">
              <template #default="{ row }">
                <div class="flex flex-wrap items-center gap-1">
                  <el-tag v-if="row.isSecret" size="small" type="danger">
                    {{ t('variables.secret') }}
                  </el-tag>
                  <el-tag v-if="row.webhookOverridable" size="small" type="warning">
                    {{ t('variables.webhookOverridable') }}
                  </el-tag>
                </div>
              </template>
            </el-table-column>
            <el-table-column v-if="canWrite" :label="t('common.actions')" width="110" align="right">
              <template #default="{ row }">
                <div class="flex items-center justify-end gap-1">
                  <IconActionButton
                    icon="i-carbon-edit"
                    :label="t('common.edit')"
                    @click="openEdit(row)"
                  />
                  <IconActionButton
                    icon="i-carbon-trash-can"
                    :label="t('common.delete')"
                    @click="remove(row)"
                  />
                </div>
              </template>
            </el-table-column>
          </el-table>
        </div>

        <div v-if="inherited.length > 0">
          <h3 class="mb-2 text-xs font-semibold text-content-strong">
            {{ t('variables.project.inherited') }}
          </h3>
          <el-table :data="inherited" size="small">
            <el-table-column prop="name" :label="t('variables.name')" min-width="150">
              <template #default="{ row }">
                <span class="font-mono text-xs">{{ row.name }}</span>
              </template>
            </el-table-column>
            <el-table-column :label="t('variables.value')" min-width="110">
              <template #default="{ row }">
                <span v-if="row.hasValue" class="font-mono text-xs">{{ row.valueMasked }}</span>
                <span v-else class="text-content-faint">—</span>
              </template>
            </el-table-column>
            <el-table-column :label="t('variables.flags')" min-width="140">
              <template #default="{ row }">
                <div class="flex flex-wrap items-center gap-1">
                  <el-tag v-if="row.isSecret" size="small" type="danger">
                    {{ t('variables.secret') }}
                  </el-tag>
                  <el-tag v-if="row.webhookOverridable" size="small" type="warning">
                    {{ t('variables.webhookOverridable') }}
                  </el-tag>
                </div>
              </template>
            </el-table-column>
          </el-table>
          <p class="mt-2 text-xs text-content-faint">{{ t('variables.project.inheritedHint') }}</p>
        </div>
      </template>
    </div>

    <el-dialog v-model="dialogVisible" :title="dialogTitle" width="32rem" append-to-body>
      <el-form label-position="top">
        <el-form-item :label="t('variables.name')">
          <el-input v-model="form.name" class="!w-full" :disabled="editingName !== null" />
          <p class="mt-1 text-xs text-content-faint">{{ t('variables.nameHint') }}</p>
        </el-form-item>

        <el-form-item :label="t('variables.value')">
          <el-input
            v-model="form.value"
            class="!w-full"
            type="password"
            show-password
            :placeholder="editingName === null ? '' : t('variables.valueKeepHint')"
          />
        </el-form-item>

        <el-form-item>
          <div class="space-y-2">
            <el-checkbox v-model="form.isSecret">{{ t('variables.secretHint') }}</el-checkbox>
            <el-checkbox v-model="form.webhookOverridable" :disabled="form.isSecret">
              {{ t('variables.webhookOverridableHint') }}
            </el-checkbox>
          </div>
        </el-form-item>

        <el-form-item :label="t('variables.description')">
          <el-input v-model="form.description" class="!w-full" maxlength="200" show-word-limit />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button size="small" @click="dialogVisible = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" size="small" :loading="saving" @click="submit">
          {{ t('common.save') }}
        </el-button>
      </template>
    </el-dialog>
  </SectionCard>
</template>
