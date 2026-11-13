<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'
import { formatDateTime } from '@/utils/datetime'
import { listUsers } from '@/api/user'
import { listRoles } from '@/api/role'
import {
  addTeamMembers,
  assignTeamRole,
  createTeam,
  getTeamDetail,
  listTeams,
  removeTeam,
  removeTeamMembers,
  revokeTeamRole,
  syncTeam,
  updateTeam
} from '@/api/team'
import type {
  AdminUserSummary,
  RoleSummary,
  TeamDetail,
  TeamRoleAssignmentSummary,
  TeamSummary
} from '@/types/api'

/**
 * 团队管理页（0.13.0）。
 *
 * ## 最重要的产品语义：团队只收窄、不授予
 * 团队角色是**权限上限**，不是授权来源。加成员**不会**让他获得任何权限；
 * 只有「同步到成员」才会把团队角色写成成员的个人授权基线。
 * 页头那句提示不是装饰——不写清这一点，用户会以为「加进团队就有权限了」。
 */
const { t } = useI18n()
const auth = useAuthStore()
const hostStore = useHostStore()

const canWrite = computed(() => auth.hasPermission('team.write'))

const teams = ref<TeamSummary[]>([])
const total = ref(0)
const pageIndex = ref(0)
const pageSize = ref(20)
const loading = ref(false)
const error = ref<string | null>(null)

const roles = ref<RoleSummary[]>([])
const allUsers = ref<AdminUserSummary[]>([])

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
const isEmpty = computed(() => !loading.value && error.value === null && teams.value.length === 0)

// ===== 创建 / 编辑 =====
const formVisible = ref(false)
const formSaving = ref(false)
/** null = 新建；非 null = 编辑（只允许改可读名称与备注） */
const editing = ref<TeamSummary | null>(null)
const form = ref({ name: '', displayName: '', description: '' })

// ===== 成员 =====
const membersVisible = ref(false)
const membersLoading = ref(false)
const membersTarget = ref<TeamSummary | null>(null)
const detail = ref<TeamDetail | null>(null)
const selectedUserIds = ref<string[]>([])

// ===== 角色 =====
const rolesVisible = ref(false)
const rolesTarget = ref<TeamSummary | null>(null)
const roleForm = ref({ role: '', host: '', containerId: '' })

async function load(): Promise<void> {
  loading.value = true
  try {
    const result = await listTeams({ pageIndex: pageIndex.value, pageSize: pageSize.value })
    teams.value = result.items
    total.value = result.total
    pageIndex.value = result.pageIndex
    pageSize.value = result.pageSize
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

async function ensureLookups(): Promise<void> {
  // 主机下拉要的是**全部可见主机**（团队角色可以做主机级作用域），
  // 这里复用全局 store；它自带并发去重，重复调用不会多打请求。
  await hostStore.ensureLoaded()

  if (roles.value.length === 0) {
    roles.value = await listRoles()
  }

  if (allUsers.value.length === 0) {
    allUsers.value = (await listUsers({ pageIndex: 0, pageSize: 100 })).items
  }
}

function openCreate(): void {
  editing.value = null
  form.value = { name: '', displayName: '', description: '' }
  formVisible.value = true
}

function openEdit(team: TeamSummary): void {
  editing.value = team
  form.value = {
    name: team.name,
    displayName: team.displayName ?? '',
    description: team.description ?? ''
  }
  formVisible.value = true
}

async function submitForm(): Promise<void> {
  formSaving.value = true
  try {
    if (editing.value === null) {
      await createTeam({
        name: form.value.name.trim(),
        displayName: form.value.displayName.trim() || undefined,
        description: form.value.description.trim() || undefined
      })
      ElMessage.success(t('team.create.done', { name: form.value.name.trim() }))
    } else {
      await updateTeam({
        teamId: editing.value.id,
        displayName: form.value.displayName.trim() || undefined,
        description: form.value.description.trim() || undefined
      })
      ElMessage.success(t('common.saved'))
    }

    formVisible.value = false
    await load()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    formSaving.value = false
  }
}

async function onRemove(team: TeamSummary): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('team.deleteConfirm', { name: team.displayName ?? team.name }),
      t('team.action.remove'),
      { confirmButtonText: t('common.delete'), cancelButtonText: t('common.cancel'), type: 'warning' }
    )
  } catch {
    return
  }

  try {
    await removeTeam(team.id)
    ElMessage.success(t('common.saved'))
    await load()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

async function openMembers(team: TeamSummary): Promise<void> {
  membersTarget.value = team
  membersVisible.value = true
  selectedUserIds.value = []
  await Promise.all([loadDetail(team.id), ensureLookups()])
}

async function loadDetail(teamId: string): Promise<void> {
  membersLoading.value = true
  try {
    detail.value = await getTeamDetail(teamId)
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
    detail.value = null
  } finally {
    membersLoading.value = false
  }
}

async function addMembers(): Promise<void> {
  if (!membersTarget.value || selectedUserIds.value.length === 0) return

  membersLoading.value = true
  try {
    await addTeamMembers(membersTarget.value.id, selectedUserIds.value)
    selectedUserIds.value = []
    await Promise.all([loadDetail(membersTarget.value.id), load()])
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    membersLoading.value = false
  }
}

async function removeMember(userId: string): Promise<void> {
  if (!membersTarget.value) return

  membersLoading.value = true
  try {
    await removeTeamMembers(membersTarget.value.id, [userId])
    await Promise.all([loadDetail(membersTarget.value.id), load()])
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    membersLoading.value = false
  }
}

async function openRoles(team: TeamSummary): Promise<void> {
  rolesTarget.value = team
  rolesVisible.value = true
  roleForm.value = { role: '', host: '', containerId: '' }
  await Promise.all([loadDetail(team.id), ensureLookups()])
}

async function assignRole(): Promise<void> {
  if (!rolesTarget.value || roleForm.value.role.length === 0) return

  membersLoading.value = true
  try {
    await assignTeamRole(rolesTarget.value.id, {
      role: roleForm.value.role,
      host: roleForm.value.host.trim() || undefined,
      containerId: roleForm.value.containerId.trim() || undefined
    })
    roleForm.value = { role: '', host: '', containerId: '' }
    await Promise.all([loadDetail(rolesTarget.value.id), load()])
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    membersLoading.value = false
  }
}

async function revokeRole(assignment: TeamRoleAssignmentSummary): Promise<void> {
  if (!rolesTarget.value) return

  membersLoading.value = true
  try {
    await revokeTeamRole(rolesTarget.value.id, {
      role: assignment.role,
      host: assignment.hostName ?? undefined,
      containerId: assignment.containerId ?? undefined
    })
    await Promise.all([loadDetail(rolesTarget.value.id), load()])
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    membersLoading.value = false
  }
}

async function onSync(team: TeamSummary): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('team.hint'),
      t('team.action.sync'),
      { confirmButtonText: t('team.action.sync'), cancelButtonText: t('common.cancel'), type: 'info' }
    )
  } catch {
    return
  }

  try {
    const result = await syncTeam(team.id)
    ElMessage.success(
      t('team.sync.done', {
        assigned: result.assigned,
        skipped: result.skipped,
        members: result.members
      })
    )
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

function candidateUsers(): AdminUserSummary[] {
  const existing = new Set((detail.value?.members ?? []).map((member) => member.userId))
  return allUsers.value.filter((user) => !existing.has(user.id))
}

watch(() => auth.user?.username, load, { immediate: true })
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-group"
      :title="t('nav.teams')"
      :subtitle="t('team.hint')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <el-button v-if="canWrite" type="primary" @click="openCreate">
          {{ t('team.action.create') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <span class="text-xs text-content-muted">{{ total }}</span>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <el-table v-else v-loading="loading" :data="teams" :row-key="(row: TeamSummary) => row.id" empty-text="">
        <el-table-column :label="t('team.col.name')" min-width="220">
          <template #default="{ row }">
            <div class="min-w-0">
              <div class="font-medium text-content-strong app-truncate">
                {{ row.displayName || row.name }}
              </div>
              <div class="text-xs text-content-muted">{{ row.name }}</div>
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="t('team.col.members')" width="100">
          <template #default="{ row }">
            <span class="text-sm">{{ row.memberCount }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('team.col.roles')" width="100">
          <template #default="{ row }">
            <span class="text-sm">{{ row.roleAssignmentCount }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('user.col.created')" width="170">
          <template #default="{ row }">
            <span class="text-xs text-content-muted">{{ formatDateTime(row.creationTime) }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('container.col.actions')" width="180">
          <template #default="{ row }">
            <div class="flex items-center gap-0.5">
              <IconActionButton
                icon="i-carbon-user-multiple"
                :label="t('team.action.addMembers')"
                @click="openMembers(row)"
              />
              <IconActionButton
                icon="i-carbon-user-role"
                :label="t('team.action.assignRole')"
                @click="openRoles(row)"
              />
              <IconActionButton
                icon="i-carbon-renew"
                :label="t('team.action.sync')"
                :disabled="!canWrite"
                @click="onSync(row)"
              />
              <template v-if="canWrite">
                <IconActionButton icon="i-carbon-edit" :label="t('common.edit')" @click="openEdit(row)" />
                <IconActionButton
                  icon="i-carbon-trash-can"
                  :label="t('common.delete')"
                  tone="danger"
                  @click="onRemove(row)"
                />
              </template>
            </div>
          </template>
        </el-table-column>
      </el-table>

      <EmptyState v-if="isEmpty" icon="i-carbon-group" :description="t('team.empty')">
        <el-button v-if="canWrite" type="primary" @click="openCreate">
          {{ t('team.action.create') }}
        </el-button>
      </EmptyState>

      <div v-if="!error && pageCount > 1" class="flex items-center justify-end gap-2 px-4 py-3">
        <el-button size="small" :disabled="pageIndex === 0" @click="pageIndex -= 1; load()">
          {{ t('common.prev') }}
        </el-button>
        <span class="text-xs text-content-muted">{{ pageIndex + 1 }} / {{ pageCount }}</span>
        <el-button size="small" :disabled="pageIndex + 1 >= pageCount" @click="pageIndex += 1; load()">
          {{ t('common.next') }}
        </el-button>
      </div>
    </SectionCard>

    <!-- 创建 / 编辑 -->
    <el-dialog v-model="formVisible" :title="editing ? t('common.edit') : t('team.create.title')" width="min(520px, 92vw)">
      <el-form label-position="top" class="space-y-3">
        <el-form-item :label="t('team.form.name')">
          <el-input v-model="form.name" :disabled="editing !== null" />
        </el-form-item>
        <el-form-item :label="t('team.form.displayName')">
          <el-input v-model="form.displayName" />
        </el-form-item>
        <el-form-item :label="t('team.form.description')">
          <el-input v-model="form.description" type="textarea" :rows="3" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="formVisible = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="formSaving" @click="submitForm">
          {{ editing ? t('common.save') : t('team.create.submit') }}
        </el-button>
      </template>
    </el-dialog>

    <!-- 成员 -->
    <el-dialog
      v-model="membersVisible"
      :title="t('team.action.addMembers')"
      width="min(760px, 94vw)"
    >
      <p class="mb-3 text-xs text-content-warn">{{ t('team.hint') }}</p>

      <div v-if="canWrite" class="flex items-end gap-2 mb-3 flex-wrap">
        <el-select v-model="selectedUserIds" multiple filterable class="!w-80" :placeholder="t('team.member.addHint')">
          <el-option v-for="user in candidateUsers()" :key="user.id" :value="user.id" :label="user.username" />
        </el-select>
        <el-button type="primary" :disabled="selectedUserIds.length === 0" :loading="membersLoading" @click="addMembers">
          {{ t('team.member.join') }}
        </el-button>
      </div>

      <el-table v-loading="membersLoading" :data="detail?.members ?? []" size="small" empty-text="">
        <el-table-column label="User" prop="username" min-width="180" />
        <el-table-column :label="t('team.col.members')" width="110">
          <template #default="{ row }">
            <el-tag v-if="row.isDisabled" size="small" type="danger">{{ t('user.status.disabled') }}</el-tag>
            <el-tag v-else-if="row.mustChangePassword" size="small" type="warning">{{ t('user.status.mustChange') }}</el-tag>
            <el-tag v-else size="small" type="success">{{ t('user.status.enabled') }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="t('container.col.actions')" width="110">
          <template #default="{ row }">
            <el-button v-if="canWrite" size="small" type="danger" plain @click="removeMember(row.userId)">
              {{ t('team.action.removeMembers') }}
            </el-button>
          </template>
        </el-table-column>
        <template #empty>
          <span class="text-sm text-content-muted">{{ t('team.member.empty') }}</span>
        </template>
      </el-table>

      <template #footer>
        <el-button @click="membersVisible = false">{{ t('common.close') }}</el-button>
      </template>
    </el-dialog>

    <!-- 团队角色（权限上限） -->
    <el-dialog v-model="rolesVisible" :title="t('team.action.assignRole')" width="min(820px, 94vw)">
      <p class="mb-3 text-xs text-content-muted">{{ t('team.role.assignHint') }}</p>

      <div v-if="canWrite" class="flex items-end gap-2 mb-3 flex-wrap">
        <el-select v-model="roleForm.role" class="!w-44" :placeholder="t('user.form.role')">
          <el-option v-for="role in roles" :key="role.id" :value="role.name" :label="role.displayName || role.name" />
        </el-select>
        <el-select v-model="roleForm.host" class="!w-44" clearable :placeholder="t('team.role.global')">
          <el-option :value="''" :label="t('team.role.global')" />
          <el-option v-for="host in hostStore.hosts" :key="host.id" :value="host.name" :label="host.name" />
        </el-select>
        <el-input v-model="roleForm.containerId" class="!w-56" :placeholder="t('team.role.container')" />
        <el-button type="primary" :disabled="roleForm.role.length === 0" :loading="membersLoading" @click="assignRole">
          {{ t('common.create') }}
        </el-button>
      </div>

      <el-table v-loading="membersLoading" :data="detail?.roleAssignments ?? []" size="small" empty-text="">
        <el-table-column :label="t('user.form.role')" min-width="150">
          <template #default="{ row }">{{ row.roleDisplayName || row.role }}</template>
        </el-table-column>
        <el-table-column :label="t('host.col.name')" min-width="140">
          <template #default="{ row }">{{ row.hostName || t('team.role.global') }}</template>
        </el-table-column>
        <el-table-column :label="t('team.role.container')" min-width="160">
          <template #default="{ row }">
            <code class="text-xs">{{ row.containerId || '—' }}</code>
          </template>
        </el-table-column>
        <el-table-column :label="t('container.col.actions')" width="110">
          <template #default="{ row }">
            <el-button v-if="canWrite" size="small" type="danger" plain @click="revokeRole(row)">
              {{ t('common.delete') }}
            </el-button>
          </template>
        </el-table-column>
        <template #empty>
          <span class="text-sm text-content-muted">{{ t('team.role.empty') }}</span>
        </template>
      </el-table>

      <template #footer>
        <el-button @click="rolesVisible = false">{{ t('common.close') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>
