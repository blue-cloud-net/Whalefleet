<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import {
  createRole,
  listPermissionDefinitions,
  listRoles,
  removeRole,
  setDefaultRole,
  setRolePermissions,
  updateRole
} from '@/api/role'
import { ApiRequestError } from '@/api'
import { domainLabelKey, permissionLabelKey, roleText, scopeLabelKey } from '@/utils/roles'
import type { PermissionDefinition, PermissionScope, RoleSummary } from '@/types/api'

/**
 * 角色与权限管理页（0.1.0，需 `role.read` / `role.write`）。
 *
 * ## 权限矩阵的列来自**后端目录**，不是前端写死的一份
 * 页面进入时拉一次 `/admin/roles/permissions`，遍历它渲染每一行。
 * 在前端留一份副本的话，后端新增一个权限时界面会**静默地少一行**——
 * 没有任何异常，只是某个能力永远勾不上。这是这一页唯一不能妥协的地方。
 *
 * ## 内置角色为什么只读
 * 它们的权限集随迁移固定（`Role.SetPermissions` 对内置角色抛异常，接口返回 `1513`）。
 * 因此矩阵里内置角色那一列整列禁用，并给出说明——
 * 让管理员点了才报错，比直接告诉他「这一列本来就不能改」糟得多。
 *
 * ## 为什么「设为默认」是单向按钮
 * 后端 `set-default` 的请求体只有一个角色定位字段，**没有 `isDefault`**：
 * 「取消默认」的方式是把别的角色设为默认。因此界面上不提供「取消默认」按钮——
 * 它没有对应的请求，做出来只会变成一个点了没反应的开关。
 */
const { t } = useI18n()

const roles = ref<RoleSummary[]>([])
const permissions = ref<PermissionDefinition[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
/** 正在保存权限的角色主键；用于只锁住那一列。 */
const savingRoleId = ref<string | null>(null)

const createVisible = ref(false)
const editVisible = ref(false)
const formName = ref('')
const formDisplayName = ref('')
const formDescription = ref('')
const formPermissions = ref<string[]>([])
const formSubmitting = ref(false)
const formError = ref<string | null>(null)
/** 编辑目标；非 null 即「编辑」模式（角色标识不可改）。 */
const editTarget = ref<RoleSummary | null>(null)

const isEmpty = computed(() => !loading.value && error.value === null && roles.value.length === 0)

/** 权限矩阵的分组：按作用域 → 资源域，**保持后端目录的顺序**。 */
const groups = computed(() => {
  const result: { scope: PermissionScope; domain: string; items: PermissionDefinition[] }[] = []

  for (const permission of permissions.value) {
    const last = result.at(-1)

    if (last !== undefined && last.scope === permission.scope && last.domain === permission.domain) {
      last.items.push(permission)
      continue
    }

    result.push({ scope: permission.scope, domain: permission.domain, items: [permission] })
  }

  return result
})

const hasCustomRole = computed(() => roles.value.some((role) => !role.isBuiltIn))

onMounted(() => {
  void load()
})

async function load(): Promise<void> {
  loading.value = true

  try {
    const [roleList, permissionList] = await Promise.all([
      listRoles(),
      listPermissionDefinitions()
    ])

    roles.value = roleList
    permissions.value = permissionList
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function roleName(role: RoleSummary): string {
  return roleText(role, t)
}

function domainText(domain: string): string {
  const key = domainLabelKey(domain)

  return key === null ? domain : t(key)
}

function scopeText(scope: string): string {
  const key = scopeLabelKey(scope)

  return key === null ? scope : t(key)
}

/**
 * 权限的展示文案。
 *
 * 走 `utils/roles.ts` 的**显式映射表**而不是拼 key：权限名本身含点
 * （`host.read`），拼出来的 `permission.host.read` 一旦没登记，
 * vue-i18n 只告警不报错，界面上直接显示 key 路径本身。
 * 未登记的权限回退显示标识——可读、可搜，且不会被误认为界面出了问题。
 */
function permissionText(permission: PermissionDefinition): string {
  const key = permissionLabelKey(permission.name)

  return key === null ? permission.name : t(key)
}

/** 该角色是否具备某权限。 */
function hasPermission(role: RoleSummary, permission: string): boolean {
  return role.permissions.includes(permission)
}

/**
 * 勾选即**覆盖式**保存该角色的权限集。
 *
 * 不做「先攒改动再点保存」：后端 `set-permissions` 本来就是覆盖语义
 * （「这个角色勾成什么样」一次写入就够），攒改动多出的本地状态
 * 只会带来「界面显示 A、库里有 B」这种最难查的不一致。
 * 代价是每个角色需要它自己的保存中状态，否则一次点击会把整页锁住。
 */
async function togglePermission(
  role: RoleSummary,
  permission: string,
  on: boolean
): Promise<void> {
  if (role.isBuiltIn || savingRoleId.value !== null) return

  const next = on
    ? [...role.permissions, permission]
    : role.permissions.filter((item) => item !== permission)

  savingRoleId.value = role.id

  try {
    await setRolePermissions({ roleId: role.id, permissions: next })
    await load()
    ElMessage({ type: 'success', message: t('roles.matrix.saved', { name: roleName(role) }) })
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    savingRoleId.value = null
  }
}

function openCreate(): void {
  editTarget.value = null
  formName.value = ''
  formDisplayName.value = ''
  formDescription.value = ''
  formPermissions.value = []
  formError.value = null
  createVisible.value = true
}

function openEdit(role: RoleSummary): void {
  editTarget.value = role
  formName.value = role.name
  formDisplayName.value = role.displayName ?? ''
  formDescription.value = role.description ?? ''
  formError.value = null
  editVisible.value = true
}

/** 新建 / 编辑提交。两者共用一处错误显示与一个提交中状态。 */
async function submitForm(): Promise<void> {
  if (formSubmitting.value) return

  formSubmitting.value = true
  formError.value = null

  try {
    if (editTarget.value === null) {
      const created = await createRole({
        name: formName.value.trim(),
        displayName: formDisplayName.value.trim(),
        description: formDescription.value.trim(),
        permissions: formPermissions.value
      })

      createVisible.value = false
      ElMessage({ type: 'success', message: t('roles.create.done', { name: roleName(created) }) })
    } else {
      const updated = await updateRole({
        roleId: editTarget.value.id,
        displayName: formDisplayName.value.trim(),
        description: formDescription.value.trim()
      })

      editVisible.value = false
      ElMessage({ type: 'success', message: t('roles.edit.done', { name: roleName(updated) }) })
    }

    await load()
  } catch (e) {
    formError.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    formSubmitting.value = false
  }
}

async function onSetDefault(role: RoleSummary): Promise<void> {
  try {
    await setDefaultRole({ roleId: role.id })
    await load()
    ElMessage({ type: 'success', message: t('roles.setDefault.done', { name: roleName(role) }) })
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  }
}

/**
 * 删除角色。
 *
 * 界面对「仍有分配」的角色**不预先禁用删除按钮**，而是在确认框里把后果说清楚：
 * 还有谁在用是后端才知道的事（`assignmentCount` 是服务端聚合的），
 * 而这里显示的数字可能已经过期。让用户点一次并收到 `1512` 的明确解释，
 * 比一个点不动的灰按钮更容易理解。
 */
async function onDelete(role: RoleSummary): Promise<void> {
  try {
    await ElMessageBox.confirm(
      role.assignmentCount > 0
        ? t('roles.delete.confirm.inUse', { name: roleName(role), count: role.assignmentCount })
        : t('roles.delete.confirm.message', { name: roleName(role) }),
      t('roles.delete.confirm.title'),
      {
        confirmButtonText: t('common.delete'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    // 用户取消：不是错误
    return
  }

  try {
    await removeRole({ roleId: role.id })
    await load()
    ElMessage({ type: 'success', message: t('roles.delete.done', { name: roleName(role) }) })
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  }
}
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-user-role"
      :title="t('nav.roles')"
      :subtitle="t('page.roles.subtitle')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <el-button type="primary" @click="openCreate">{{ t('roles.action.create') }}</el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <span class="text-xs text-content-muted">{{ t('roles.total', { total: roles.length }) }}</span>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <el-table
        v-else
        v-loading="loading"
        :data="roles"
        :row-key="(row: RoleSummary) => row.id"
        empty-text=""
      >
        <el-table-column :label="t('roles.col.name')" min-width="220">
          <template #default="{ row }">
            <div class="flex items-center gap-2">
              <span class="font-medium text-content-strong">{{ roleName(row) }}</span>
              <el-tag v-if="row.isBuiltIn" size="small" type="info">
                {{ t('roles.badge.builtIn') }}
              </el-tag>
              <el-tag v-if="row.isDefault" size="small" type="success">
                {{ t('roles.badge.default') }}
              </el-tag>
            </div>
            <div class="font-mono text-xs text-content-faint">{{ row.name }}</div>
          </template>
        </el-table-column>

        <el-table-column :label="t('roles.col.description')" min-width="240">
          <template #default="{ row }">
            <span class="text-xs text-content-muted">{{ row.description ?? '—' }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('roles.col.permissions')" width="110" align="right">
          <template #default="{ row }">
            <span class="text-sm">{{ row.permissions.length }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('roles.col.assignments')" width="110" align="right">
          <template #default="{ row }">
            <span class="text-sm">{{ row.assignmentCount }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('container.col.actions')" width="140">
          <template #default="{ row }">
            <div class="flex items-center gap-0.5">
              <IconActionButton
                icon="i-carbon-star"
                :label="t('roles.action.setDefault')"
                :hint="t('roles.action.setDefaultHint')"
                :disabled="row.isDefault"
                @click="onSetDefault(row)"
              />
              <IconActionButton
                icon="i-carbon-edit"
                :label="t('common.edit')"
                @click="openEdit(row)"
              />
              <IconActionButton
                icon="i-carbon-trash-can"
                :label="t('common.delete')"
                :hint="t('roles.delete.hint')"
                tone="danger"
                :disabled="row.isBuiltIn"
                @click="onDelete(row)"
              />
            </div>
          </template>
        </el-table-column>
      </el-table>

      <EmptyState v-if="isEmpty" icon="i-carbon-user-role" :description="t('roles.empty')">
        <el-button type="primary" @click="openCreate">{{ t('roles.action.create') }}</el-button>
      </EmptyState>
    </SectionCard>

    <SectionCard :title="t('roles.matrix.title')" class="mt-4">
      <template #actions>
        <span class="text-xs text-content-muted">{{ t('roles.matrix.hint') }}</span>
      </template>

      <div v-if="!hasCustomRole && roles.length > 0" class="mb-3">
        <el-alert type="info" :closable="false" :title="t('roles.matrix.builtInOnly')" />
      </div>

      <div v-loading="loading" class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-line-subtle text-xs text-content-muted">
              <th class="sticky left-0 bg-surface-card py-2 pr-3 text-left font-medium">
                {{ t('roles.matrix.permission') }}
              </th>
              <th
                v-for="role in roles"
                :key="role.id"
                class="min-w-24 px-2 py-2 text-center font-medium"
              >
                <div class="app-truncate">{{ roleName(role) }}</div>
                <div class="font-mono text-[10px] font-normal text-content-faint">
                  {{ role.name }}
                </div>
              </th>
            </tr>
          </thead>
          <tbody v-for="group in groups" :key="`${group.scope}.${group.domain}`">
            <tr class="bg-surface-sunken">
              <td
                class="sticky left-0 bg-surface-sunken py-1 pr-3 text-xs font-medium text-content-muted"
                :colspan="roles.length + 1"
              >
                {{ scopeText(group.scope) }} · {{ domainText(group.domain) }}
              </td>
            </tr>
            <tr
              v-for="permission in group.items"
              :key="permission.name"
              class="border-b border-line-subtle last:border-b-0"
            >
              <td class="sticky left-0 bg-surface-card py-2 pr-3">
                <div class="text-sm text-content-body">{{ permissionText(permission) }}</div>
                <div class="font-mono text-xs text-content-faint">{{ permission.name }}</div>
              </td>
              <td
                v-for="role in roles"
                :key="`${role.id}-${permission.name}`"
                class="px-2 py-2 text-center"
              >
                <el-checkbox
                  :model-value="hasPermission(role, permission.name)"
                  :disabled="role.isBuiltIn || savingRoleId !== null"
                  @update:model-value="
                    (value: unknown) => togglePermission(role, permission.name, value === true)
                  "
                />
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <p class="mt-3 text-xs text-content-faint">{{ t('roles.matrix.footnote') }}</p>
    </SectionCard>

    <!-- 新建角色 -->
    <el-dialog
      v-model="createVisible"
      :title="t('roles.create.title')"
      width="min(560px, 92vw)"
      :close-on-click-modal="!formSubmitting"
      :close-on-press-escape="!formSubmitting"
    >
      <el-form label-position="top" @submit.prevent>
        <el-form-item :label="t('roles.form.name')" required>
          <el-input
            v-model="formName"
            :placeholder="t('roles.form.namePlaceholder')"
            :disabled="formSubmitting"
          />
          <p class="mt-1 text-xs text-content-muted">{{ t('roles.form.nameHint') }}</p>
        </el-form-item>

        <el-form-item :label="t('roles.form.displayName')">
          <el-input v-model="formDisplayName" :disabled="formSubmitting" />
        </el-form-item>

        <el-form-item :label="t('roles.form.description')">
          <el-input v-model="formDescription" type="textarea" :rows="2" :disabled="formSubmitting" />
        </el-form-item>

        <el-form-item :label="t('roles.form.permissions')">
          <el-select
            v-model="formPermissions"
            multiple
            clearable
            class="w-full"
            :disabled="formSubmitting"
            :placeholder="t('roles.form.permissionsPlaceholder')"
          >
            <el-option
              v-for="permission in permissions"
              :key="permission.name"
              :label="permissionText(permission)"
              :value="permission.name"
            />
          </el-select>
          <p class="mt-1 text-xs text-content-muted">{{ t('roles.form.permissionsHint') }}</p>
        </el-form-item>

        <el-alert v-if="formError" type="error" :closable="false" :title="formError" />
      </el-form>

      <template #footer>
        <el-button :disabled="formSubmitting" @click="createVisible = false">
          {{ t('common.cancel') }}
        </el-button>
        <el-button
          type="primary"
          :loading="formSubmitting"
          :disabled="formName.trim().length === 0"
          @click="submitForm"
        >
          {{ t('roles.form.submit') }}
        </el-button>
      </template>
    </el-dialog>

    <!-- 编辑角色（标识不可改） -->
    <el-dialog
      v-model="editVisible"
      :title="t('roles.edit.title')"
      width="min(520px, 92vw)"
      :close-on-click-modal="!formSubmitting"
      :close-on-press-escape="!formSubmitting"
    >
      <el-form label-position="top" @submit.prevent>
        <el-form-item :label="t('roles.form.name')">
          <el-input :model-value="formName" disabled />
          <p class="mt-1 text-xs text-content-muted">{{ t('roles.form.nameImmutable') }}</p>
        </el-form-item>

        <el-form-item :label="t('roles.form.displayName')">
          <el-input v-model="formDisplayName" :disabled="formSubmitting" />
        </el-form-item>

        <el-form-item :label="t('roles.form.description')">
          <el-input v-model="formDescription" type="textarea" :rows="2" :disabled="formSubmitting" />
        </el-form-item>

        <el-alert v-if="formError" type="error" :closable="false" :title="formError" />
      </el-form>

      <template #footer>
        <el-button :disabled="formSubmitting" @click="editVisible = false">
          {{ t('common.cancel') }}
        </el-button>
        <el-button type="primary" :loading="formSubmitting" @click="submitForm">
          {{ t('common.save') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>
