<script setup lang="ts">
import { useAuthStore } from '@/stores/auth'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessageBox } from 'element-plus'
import { assignUserRole, listRoles, listUserRoleAssignments, revokeUserRole } from '@/api/role'
import type { RoleAssignmentSummary, RoleSummary } from '@/types/api'
const props = defineProps<{
  userId?: string
  host?: string
  containerId?: string
}>()
const { t } = useI18n()
const auth = useAuthStore()
const user = ref(props.userId ?? '')
const host = ref(props.host ?? '')
const container = ref(props.containerId ?? '')
const role = ref('')
const roles = ref<RoleSummary[]>([])
const assignments = ref<RoleAssignmentSummary[]>([])
const allAssignments = ref<RoleAssignmentSummary[]>([])
function permissionsFor(rows: RoleAssignmentSummary[]): Set<string> {
  return new Set(
    rows
      .flatMap((row) => roles.value.find((item) => item.id === row.roleId)?.permissions ?? [])
      .filter((name) => name.startsWith('container.'))
  )
}
const upperPermissions = computed(() =>
  permissionsFor(
    allAssignments.value.filter(
      (row) => !row.containerId && (row.hostId === null || row.hostName === host.value)
    )
  )
)
const effectivePermissions = computed(() => {
  const rows = assignments.value.filter(
    (row) => row.hostName === host.value && row.containerId === container.value
  )
  const admin = allAssignments.value.some(
    (row) =>
      row.hostId === null &&
      roles.value.some((role) => role.id === row.roleId && role.isBuiltIn && role.name === 'admin')
  )
  if (admin) return [...upperPermissions.value]
  if (!rows.length)
    return allAssignments.value[0]?.containerPermissionMode === 'requireAssignment'
      ? []
      : [...upperPermissions.value]
  const granted = permissionsFor(rows)
  return [...upperPermissions.value].filter((name) => granted.has(name))
})
const busy = ref(false)
const error = ref('')
watch(
  () => props.userId,
  (value) => {
    user.value = value ?? ''
    assignments.value = []
    allAssignments.value = []
  }
)
watch(() => [props.host, props.containerId] as const, ([nextHost, nextContainer]) => {
  host.value = nextHost ?? ''
  container.value = nextContainer ?? ''
  assignments.value = []
  allAssignments.value = []
})
async function load() {
  if (!user.value) return
  busy.value = true
  error.value = ''
  try {
    const [allRoles, rows] = await Promise.all([listRoles(), listUserRoleAssignments(user.value)])
    roles.value = allRoles
    allAssignments.value = rows
    assignments.value = rows.filter(
      (row) =>
        row.containerId &&
        (!props.containerId || row.containerId === props.containerId) &&
        (!props.host || row.hostName === props.host)
    )
  } catch (e) {
    error.value = String(e)
  } finally {
    busy.value = false
  }
}
async function assign() {
  if (!user.value || !host.value || !container.value || !role.value) return
  busy.value = true
  error.value = ''
  try {
    const assigned = await assignUserRole(user.value, {
      host: host.value,
      containerId: container.value,
      roleId: role.value
    })
    container.value = assigned.containerId ?? container.value
    await load()
  } catch (e) {
    error.value = String(e)
  } finally {
    busy.value = false
  }
}
async function revoke(row: RoleAssignmentSummary) {
  try {
    await ElMessageBox.confirm(
      t(
        row.containerPermissionMode === 'requireAssignment'
          ? 'containerRbac.revokeStrict'
          : 'containerRbac.revokeInherited'
      ),
      t(
        row.containerPermissionMode === 'requireAssignment'
          ? 'containerRbac.revokeStrictTitle'
          : 'containerRbac.revokeInheritedTitle'
      ),
      {
        type: 'warning'
      }
    )
  } catch {
    return
  }
  busy.value = true
  error.value = ''
  try {
    await revokeUserRole(user.value, {
      host: row.hostName,
      containerId: row.containerId,
      roleId: row.roleId
    })
    await load()
  } catch (e) {
    error.value = String(e)
  } finally {
    busy.value = false
  }
}
</script>
<template>
  <section class="mt-5 space-y-3" :aria-label="t('containerRbac.title')">
    <h3 class="font-semibold">{{ t('containerRbac.title') }}</h3>
    <p class="text-sm text-content-body">
      {{ t('containerRbac.explanation') }}
    </p>
    <dl v-if="allAssignments.length" class="text-sm space-y-2">
      <div>
        <dt class="font-medium">{{ t('containerRbac.hostUpper') }}</dt>
        <dd>{{ [...upperPermissions].join(', ') || '—' }}</dd>
      </div>
      <div>
        <dt class="font-medium">{{ t('containerRbac.effective') }}</dt>
        <dd>{{ effectivePermissions.join(', ') || '—' }}</dd>
      </div>
    </dl>
    <el-form label-position="top" @submit.prevent="assign">
      <el-form-item v-if="!props.userId" :label="t('containerRbac.user')">
        <el-input v-model="user" />
      </el-form-item>
      <el-form-item v-if="!props.host" :label="t('containerRbac.host')">
        <el-input v-model="host" />
      </el-form-item>
      <el-form-item v-if="!props.containerId" :label="t('containerRbac.container')">
        <el-input v-model="container" />
      </el-form-item>
      <el-button :loading="busy" :disabled="!user" @click="load">
        {{ t('containerRbac.load') }}
      </el-button>
      <el-form-item :label="t('containerRbac.role')">
        <el-select v-model="role" class="!w-full">
          <el-option
            v-for="item in roles"
            :key="item.id"
            :value="item.id"
            :label="item.displayName || item.name"
          />
        </el-select>
      </el-form-item>
      <el-button
        type="primary"
        :loading="busy"
        :disabled="!auth.hasPermission('role.write') || !user || !host || !container || !role"
        @click="assign"
      >
        {{ t('containerRbac.assign') }}
      </el-button>
    </el-form>
    <p v-if="error" role="alert">{{ error }}</p>
    <ul class="space-y-2">
      <li v-for="row in assignments" :key="row.id" class="flex items-center justify-between gap-3">
        <span class="break-all">
          {{ row.hostName }} · {{ row.containerId }} · {{ row.roleDisplayName || row.role }}
        </span>
        <el-button :disabled="busy || !auth.hasPermission('role.write')" @click="revoke(row)">
          {{ t('containerRbac.revoke') }}
        </el-button>
      </li>
    </ul>
  </section>
</template>
