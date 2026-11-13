<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  createGlobalVariable,
  listGlobalVariables,
  removeGlobalVariable,
  updateGlobalVariable
} from '@/api/variables'
import { ApiRequestError } from '@/api'
import { useAuthStore } from '@/stores/auth'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import type { EnvVariableDto } from '@/types/api'

/**
 * 全局环境变量卡片（0.15.0）。
 *
 * ## 为什么在「全局设置」而不是主机详情里
 * 全局变量对所有 Compose 项目生效，包括还没建出来的项目。
 * 把它挂在某台主机下，等于让「新增一台主机」变成「再填一遍全部变量」——
 * 而 N 份副本迟早会漂移（改了其中一份，其它主机开始认不上）。
 *
 * ## 值永不回显
 * 表格里只显示后端给的遮罩串（`hasValue` 为假时显示「—」）。编辑时值输入框
 * **默认为空**：留空表示「不改值」，要清空值得点「清空值」按钮。
 * 让输入框预填成 `••••••` 会诱导用户把它当成真值提交，一保存就把口令写成了圆点。
 *
 * ## 密钥与 Webhook 覆盖是联动不可变量
 * 勾上「密钥」后「允许 Webhook 覆盖」自动取消并禁用。后端也确实会强制置否
 * （`is_secret = 0 OR webhook_overridable = 0` 是数据库 CHECK）——
 * Webhook 是匿名入口，凭据就是 URL 里的令牌，而 URL 会进反代的 access log。
 */
const { t, te } = useI18n()
const auth = useAuthStore()

/** 变量列表。 */
const items = ref<EnvVariableDto[]>([])

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

/** 是否可写。 */
const canWrite = computed(() => auth.hasPermission('variable.write'))

/** 表单标题。 */
const dialogTitle = computed(() =>
  editingName.value === null ? t('variables.global.add') : t('variables.global.edit')
)

/**
 * 变量名是否合法（与后端 `EnvVariableName` 同一规则）。
 *
 * 前端先拦一道不是「重复校验」，而是为了让错误**落在输入框上**：
 * 后端只回一句 `1909`，用户还得自己猜是哪个字符不合法。
 */
function isNameValid(name: string): boolean {
  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(name) && name.length <= 64
}

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

/** 加载列表。 */
async function load(): Promise<void> {
  loading.value = true

  try {
    items.value = await listGlobalVariables()
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

  if (!isNameValid(name)) {
    ElMessage.error(t('variables.error.nameInvalid'))

    return
  }

  saving.value = true

  try {
    const payload = {
      name,
      // 空串 = 「不改值」：不要把它当成本地值提交，否则一次编辑就把口令写成空串。
      value: form.value === '' ? null : form.value,
      isSecret: form.isSecret,
      // 密钥恒不可覆盖：后端也会强制，这里只是别让界面显示出矛盾状态。
      webhookOverridable: form.isSecret ? false : form.webhookOverridable,
      description: form.description.trim() === '' ? null : form.description.trim()
    }

    if (editingName.value === null) {
      await createGlobalVariable(payload)
    } else {
      await updateGlobalVariable(editingName.value, payload)
    }

    ElMessage.success(t('common.saved'))
    dialogVisible.value = false
    await load()
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    saving.value = false
  }
}

/**
 * 删除变量。
 *
 * @param item 待删除的变量。
 */
async function remove(item: EnvVariableDto): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('variables.global.removeConfirm', { name: item.name }),
      t('common.confirmTitle'),
      { type: 'warning' }
    )
  } catch {
    return
  }

  try {
    await removeGlobalVariable(item.name)
    ElMessage.success(t('common.deleted'))
    await load()
  } catch (error) {
    ElMessage.error(describeError(error))
  }
}

onMounted(load)
</script>

<template>
  <SectionCard :title="t('variables.global.title')" :description="t('variables.global.hint')">
    <template #actions>
      <el-button
        v-if="canWrite"
        type="primary"
        size="small"
        icon="i-carbon-add"
        @click="openCreate"
      >
        {{ t('variables.global.add') }}
      </el-button>
    </template>

    <div v-loading="loading">
      <EmptyState
        v-if="items.length === 0"
        icon="i-carbon-value-variable"
        :description="t('variables.global.empty')"
        compact
      />

      <el-table v-else :data="items" size="small">
        <el-table-column prop="name" :label="t('variables.name')" min-width="160">
          <template #default="{ row }">
            <span class="font-mono text-xs">{{ row.name }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('variables.value')" min-width="120">
          <template #default="{ row }">
            <!-- 值永不回显：要么是遮罩串，要么是「—」（尚未设置值） -->
            <span v-if="row.hasValue" class="font-mono text-xs">{{ row.valueMasked }}</span>
            <span v-else class="text-content-faint">—</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('variables.flags')" min-width="150">
          <template #default="{ row }">
            <div class="flex flex-wrap items-center gap-1">
              <el-tag v-if="row.isSecret" size="small" type="danger">{{ t('variables.secret') }}</el-tag>
              <!--
                普通变量不显示「非密钥」标签（默认状态不值得占位），
                但「可被 Webhook 覆盖」必须显示——它是**授予了一条远程改值通道**。
              -->
              <el-tag v-if="row.webhookOverridable" size="small" type="warning">
                {{ t('variables.webhookOverridable') }}
              </el-tag>
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="t('variables.description')" min-width="180">
          <template #default="{ row }">
            <span class="text-xs text-content-body">{{ row.description ?? '—' }}</span>
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

    <el-dialog v-model="dialogVisible" :title="dialogTitle" width="32rem" append-to-body>
      <el-form label-position="top">
        <el-form-item :label="t('variables.name')">
          <el-input
            v-model="form.name"
            class="!w-full"
            :disabled="editingName !== null"
            placeholder="DATABASE_PASSWORD"
          />
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
            <!--
              密钥不可被 Webhook 覆盖：勾上密钥后这里自动取消并禁用。
              后端也确实会强制（数据库 CHECK），界面上的禁用只是让矛盾状态不可见。
            -->
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
