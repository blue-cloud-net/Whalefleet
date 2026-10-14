<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import UserFormDialog from '@/components/business/UserFormDialog.vue'
import ResetPasswordDialog from '@/components/business/ResetPasswordDialog.vue'
import { deleteUser, listUsers } from '@/api/user'
import { ApiRequestError } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { roleLabelKey } from '@/utils/roles'
import { formatDateTime } from '@/utils/datetime'
import type { AdminUserSummary } from '@/types/api'

/**
 * 用户管理页（0.0.13，admin 专用）。
 *
 * ## 三处刻意的产品决定
 * 1. **自己那一行的删除按钮直接禁用**：后端有「不能删除当前登录账号」的护栏，
 *    但让用户点下去才报错是糟糕的交互——那个按钮对他永远是错的。
 * 2. **重置密码要明确说「会把对方踢下线」**：改密会撤销该用户全部刷新令牌。
 *    不说清楚，管理员会以为重置没生效，而对方会以为系统坏了。
 * 3. **角色按名字展示，不做白名单**：后端支持自定义角色（`roles` 是表不是枚举），
 *    前端拿内置名单当白名单会把「支持自定义角色」当场废掉。
 *    内置角色走 i18n，自定义角色原样显示。
 */
const { t } = useI18n()
const auth = useAuthStore()

const users = ref<AdminUserSummary[]>([])
const total = ref(0)
const pageIndex = ref(0)
const pageSize = ref(20)
const loading = ref(false)
const error = ref<string | null>(null)
const busyIds = ref<Set<string>>(new Set())
const createDialogVisible = ref(false)
const resetDialogVisible = ref(false)
/** 待重置密码的目标用户 */
const resetTarget = ref<AdminUserSummary | null>(null)

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
const isEmpty = computed(() => !loading.value && error.value === null && users.value.length === 0)

async function load(): Promise<void> {
  loading.value = true
  try {
    const result = await listUsers({ pageIndex: pageIndex.value, pageSize: pageSize.value })

    users.value = result.items
    total.value = result.total
    // ⚠️ 以服务端回显的页码 / 页数为准（pageSize 会被夹紧到 100）
    pageIndex.value = result.pageIndex
    pageSize.value = result.pageSize
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function roleText(role: string): string {
  const key = roleLabelKey(role)

  // 自定义角色直接显示原值——它没有对应文案，硬凑一个只会更难懂
  return key ? t(key) : role
}

/** 是否是当前登录用户自己（按用户名比对：会话里带的是用户名与角色，不带主键）。 */
function isSelf(user: AdminUserSummary): boolean {
  return auth.user?.username === user.username
}

function markBusy(id: string, busy: boolean): void {
  const next = new Set(busyIds.value)
  if (busy) next.add(id)
  else next.delete(id)
  busyIds.value = next
}

async function onDelete(user: AdminUserSummary): Promise<void> {
  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('user.confirm.delete.message', { name: user.username })),
        h('span', { class: 'text-status-warn text-xs' }, t('user.confirm.delete.sessions')),
        h('span', { class: 'text-content-muted text-xs' }, t('user.confirm.delete.soft'))
      ]),
      t('user.confirm.delete.title'),
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

  markBusy(user.id, true)
  try {
    await deleteUser(user.id)
    ElMessage({ type: 'success', message: t('user.delete.done', { name: user.username }) })
  } finally {
    await load()
    markBusy(user.id, false)
  }
}

function openReset(user: AdminUserSummary): void {
  resetTarget.value = user
  resetDialogVisible.value = true
}

async function onCreate(): Promise<void> {
  createDialogVisible.value = false
  pageIndex.value = 0
  await load()
}

async function goToPage(next: number): Promise<void> {
  if (next < 0 || next >= pageCount.value) return

  pageIndex.value = next
  await load()
}

watch(() => auth.user?.username, load, { immediate: true })
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-user-multiple"
      :title="t('nav.users')"
      :subtitle="t('page.users.subtitle')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <el-button type="primary" @click="createDialogVisible = true">
          {{ t('user.action.create') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <span class="text-xs text-content-muted">{{ t('user.total', { total }) }}</span>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <el-table
        v-else
        v-loading="loading"
        :data="users"
        :row-key="(row: AdminUserSummary) => row.id"
        empty-text=""
      >
        <el-table-column :label="t('user.col.username')" min-width="240">
          <template #default="{ row }">
            <div class="flex items-center gap-2">
              <span class="font-medium text-content-strong">{{ row.username }}</span>
              <span v-if="isSelf(row)" class="text-xs text-brand-500">
                {{ t('user.badge.self') }}
              </span>
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="t('user.col.role')" width="180">
          <template #default="{ row }">
            <div class="min-w-0">
              <div class="text-sm">{{ roleText(row.role) }}</div>
              <div class="font-mono text-xs text-content-faint">{{ row.role }}</div>
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="t('user.col.created')" width="180">
          <template #default="{ row }">
            <span class="text-xs text-content-muted">{{ formatDateTime(row.creationTime) }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('container.col.actions')" width="64">
          <template #default="{ row }">
            <div class="flex items-center gap-0.5">
              <IconActionButton
                icon="i-carbon-password"
                :label="t('user.action.resetPassword')"
                @click="openReset(row)"
              />
              <!-- 自己那一行禁用删除：后端有同样的护栏，让按钮命中一次错误不如直接挡住。
                   hint 把「为什么点不了」说清楚，否则只剩一个没有解释的灰色图标。 -->
              <IconActionButton
                icon="i-carbon-trash-can"
                :label="t('common.delete')"
                :hint="t('user.delete.selfHint')"
                tone="danger"
                :disabled="isSelf(row)"
                :loading="busyIds.has(row.id)"
                @click="onDelete(row)"
              />
            </div>
          </template>
        </el-table-column>
      </el-table>

      <EmptyState v-if="isEmpty" icon="i-carbon-user-multiple" :description="t('user.empty')">
        <el-button type="primary" @click="createDialogVisible = true">
          {{ t('user.action.create') }}
        </el-button>
      </EmptyState>

      <div v-if="!error && pageCount > 1" class="flex items-center justify-end gap-2 px-4 py-3">
        <el-button size="small" :disabled="pageIndex === 0" @click="goToPage(pageIndex - 1)">
          {{ t('common.prev') }}
        </el-button>
        <span class="text-xs text-content-muted">{{ pageIndex + 1 }} / {{ pageCount }}</span>
        <el-button
          size="small"
          :disabled="pageIndex + 1 >= pageCount"
          @click="goToPage(pageIndex + 1)"
        >
          {{ t('common.next') }}
        </el-button>
      </div>
    </SectionCard>

    <p class="mt-3 text-xs text-content-faint">{{ t('user.footnote') }}</p>

    <UserFormDialog v-model="createDialogVisible" @created="onCreate" />
    <ResetPasswordDialog v-model="resetDialogVisible" :user="resetTarget" @updated="load" />
  </div>
</template>
