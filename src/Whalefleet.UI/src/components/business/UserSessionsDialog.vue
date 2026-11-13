<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { listUserSessions, revokeAllUserSessions, revokeUserSession } from '@/api/user'
import { formatDateTime } from '@/utils/datetime'
import type { AdminUserSummary, UserSessionSummary } from '@/types/api'

/**
 * 会话管理对话框（0.13.0）。
 *
 * ## 为什么展示「指纹」与「浏览器」
 * 管理员要回答的问题是「哪一条是可疑的那一台」。
 * 只有时间戳时，多台设备看起来一模一样；UA 与指纹前缀是唯一能区分它们的线索。
 *
 * ⚠️ **指纹只用于展示**：它不参与任何校验，服务端也不会因为指纹变化而拒绝请求。
 * 因此界面上不要写「指纹不匹配」这种暗示它是安全措施的话。
 */
const props = defineProps<{
  modelValue: boolean
  user: AdminUserSummary | null
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  changed: []
}>()

const { t } = useI18n()

const sessions = ref<UserSessionSummary[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const busy = ref(false)

async function load(): Promise<void> {
  if (!props.user) return

  loading.value = true
  error.value = null
  try {
    sessions.value = await listUserSessions(props.user.id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
    sessions.value = []
  } finally {
    loading.value = false
  }
}

async function revoke(session: UserSessionSummary): Promise<void> {
  if (!props.user) return

  try {
    await ElMessageBox.confirm(
      t('user.session.revokeConfirm'),
      t('user.session.revoke'),
      { confirmButtonText: t('user.session.revoke'), cancelButtonText: t('common.cancel'), type: 'warning' }
    )
  } catch {
    return
  }

  busy.value = true
  try {
    await revokeUserSession(props.user.id, session.sessionId)
    ElMessage.success(t('user.session.revoked'))
    await load()
    emit('changed')
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    busy.value = false
  }
}

async function revokeAll(): Promise<void> {
  if (!props.user) return

  try {
    await ElMessageBox.confirm(
      t('user.session.revokeAllConfirm'),
      t('user.session.revokeAll'),
      { confirmButtonText: t('user.session.revokeAll'), cancelButtonText: t('common.cancel'), type: 'warning' }
    )
  } catch {
    return
  }

  busy.value = true
  try {
    const count = await revokeAllUserSessions(props.user.id)
    ElMessage.success(t('user.session.revokedAll', { count }))
    await load()
    emit('changed')
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    busy.value = false
  }
}

function close(): void {
  emit('update:modelValue', false)
}

watch(
  () => props.modelValue,
  (open) => {
    if (open) void load()
  }
)
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="t('user.session.title')"
    width="min(920px, 92vw)"
    @update:model-value="emit('update:modelValue', $event)"
  >
    <div class="flex items-center justify-between gap-3 flex-wrap mb-3">
      <span class="text-sm text-content-body">{{ user?.username }}</span>
      <el-button size="small" :loading="busy" @click="revokeAll">
        {{ t('user.session.revokeAll') }}
      </el-button>
    </div>

    <div v-if="error" class="py-6 flex flex-col items-center gap-3">
      <p class="text-sm text-content-muted">{{ error }}</p>
      <el-button size="small" @click="load">{{ t('common.retry') }}</el-button>
    </div>

    <el-table v-else v-loading="loading" :data="sessions" size="small" empty-text="">
      <el-table-column :label="t('user.session.col.created')" width="160">
        <template #default="{ row }">
          <span class="text-xs">{{ formatDateTime(row.createdAt) }}</span>
        </template>
      </el-table-column>

      <el-table-column :label="t('user.session.col.expires')" width="160">
        <template #default="{ row }">
          <span class="text-xs">{{ formatDateTime(row.expiresAt) }}</span>
        </template>
      </el-table-column>

      <el-table-column :label="t('user.session.col.ip')" width="130">
        <template #default="{ row }">
          <span class="text-xs">{{ row.createdIp ?? '—' }}</span>
        </template>
      </el-table-column>

      <el-table-column :label="t('user.session.col.userAgent')" min-width="220">
        <template #default="{ row }">
          <span class="text-xs app-truncate" :title="row.userAgent ?? ''">
            {{ row.userAgent ?? '—' }}
          </span>
        </template>
      </el-table-column>

      <el-table-column :label="t('user.session.col.fingerprint')" width="150">
        <template #default="{ row }">
          <code class="text-xs">{{ row.deviceFingerprint?.slice(0, 12) ?? '—' }}</code>
        </template>
      </el-table-column>

      <el-table-column :label="t('container.col.actions')" width="110">
        <template #default="{ row }">
          <el-button size="small" type="danger" plain :loading="busy" @click="revoke(row)">
            {{ t('user.session.revoke') }}
          </el-button>
        </template>
      </el-table-column>

      <template #empty>
        <span class="text-sm text-content-muted">{{ t('user.session.empty') }}</span>
      </template>
    </el-table>

    <template #footer>
      <el-button @click="close">{{ t('common.close') }}</el-button>
    </template>
  </el-dialog>
</template>
