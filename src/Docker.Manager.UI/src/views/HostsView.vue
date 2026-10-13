<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import HostFormDialog from '@/components/business/HostFormDialog.vue'
import GroupFormDialog from '@/components/business/GroupFormDialog.vue'
import { deleteHost, listAdminHosts, updateHost } from '@/api/host'
import { deleteHostGroup, listAdminHostGroups } from '@/api/host-group'
import { ApiRequestError } from '@/api'
import { groupDescription, groupLabel } from '@/utils/group-display'
import type { AdminHostSummary, HostGroup } from '@/types/api'

/**
 * 主机管理页（0.0.13，admin 专用）。
 *
 * ## 三个刻意的设计决定
 * 1. **连通状态显示三态**：`reachable` 是可空布尔——`null` = **未探测**（创建 / 更新不探活）。
 *    把它当 `false` 会把「没测」显示成「离线」，让人去查网络而病因根本不在这儿。
 * 2. **「测试连通性」按钮在表单里，不在列表行上**：后端 test 端点收的是**一份配置**
 *    （type + endpoint + 凭据），不是 hostId——它测的正是「还没保存的那份配置」。
 *    列表里没有凭据内容（只有 `authKind`），拿不到凭据就没法忠实复现连接条件。
 * 3. **分组在这里管而不是另开一页**：分组的唯一用途就是给主机归类，
 *    把它放在别处会让「新建主机时选分组」需要先跳走一趟。
 */
const { t } = useI18n()

const hosts = ref<AdminHostSummary[]>([])
const groups = ref<HostGroup[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
/** 正在执行行内操作的 hostId（启用开关等） */
const busyNames = ref<Set<string>>(new Set())

const hostDialogVisible = ref(false)
/** 编辑目标；null = 新建 */
const editingHost = ref<AdminHostSummary | null>(null)

const groupDialogVisible = ref(false)
const editingGroup = ref<HostGroup | null>(null)

const isEmpty = computed(() => !loading.value && error.value === null && hosts.value.length === 0)

/** 分组下拉的选项（后端已排好序）。 */
const groupOptions = computed(() => groups.value.map((group) => ({ id: group.id, name: group.name })))

/** 分组主键 → 展示名（内置分组取 i18n，自定义分组用管理员输入的名字）。 */
const groupNameById = computed(() => {
  const map = new Map<string, string>()

  for (const group of groups.value) {
    map.set(group.id, groupLabel(group, t))
  }

  return map
})

async function load(): Promise<void> {
  loading.value = true
  try {
    // 并行取两份：主机与分组互不依赖，串行只会白等一个 RTT
    const [hostList, groupList] = await Promise.all([listAdminHosts(), listAdminHostGroups()])

    hosts.value = hostList
    groups.value = groupList
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function markBusy(name: string, busy: boolean): void {
  const next = new Set(busyNames.value)
  if (busy) next.add(name)
  else next.delete(name)
  busyNames.value = next
}

/** 连通状态的三态文案与色调。 */
function reachableLabel(host: AdminHostSummary): string {
  if (host.reachable === null) return t('host.reachable.unknown')
  return host.reachable ? t('host.reachable.online') : t('host.reachable.offline')
}

function reachableClass(host: AdminHostSummary): string {
  if (host.reachable === null) return 'text-content-faint'
  return host.reachable ? 'text-status-ok' : 'text-status-down'
}

/**
 * 认证方式的文案。
 *
 * 必须显式映射，**不能**在模板里拼 `t(\`host.auth.${kind}\`)`：后端哪天加了新的
 * 认证方式，拼出来的 key 不存在，界面上会直接显示 `host.auth.xxx` 这种原始 key 路径，
 * 而 vue-i18n 找不到 key 时只告警不报错——只有真看到界面才会发现。
 */
const AUTH_LABEL_KEYS: Record<string, string> = {
  none: 'host.auth.none',
  tls: 'host.auth.tls',
  basic: 'host.auth.basic'
}

function authLabel(host: AdminHostSummary): string {
  const key = AUTH_LABEL_KEYS[host.authKind]

  return key ? t(key) : String(host.authKind ?? '—')
}

function openCreate(): void {
  editingHost.value = null
  hostDialogVisible.value = true
}

function openEdit(host: AdminHostSummary): void {
  editingHost.value = host
  hostDialogVisible.value = true
}

async function onHostSaved(): Promise<void> {
  hostDialogVisible.value = false
  await load()
}

/**
 * 启用 / 停用。
 *
 * 停用是个有后果的动作（指向该主机的资源请求会被拒），因此**停用时二次确认**，
 * 启用时直接生效——启用只是恢复到正常状态，没有可撤销的损失。
 */
async function toggleEnabled(host: AdminHostSummary): Promise<void> {
  if (!host.enabled) {
    markBusy(host.name, true)
    try {
      await updateHost(host.name, { enabled: true })
      ElMessage({ type: 'success', message: t('host.enable.done', { name: host.name }) })
    } finally {
      await load()
      markBusy(host.name, false)
    }

    return
  }

  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('host.disable.message', { name: host.name })),
        h('span', { class: 'text-status-warn text-xs' }, t('host.disable.impact'))
      ]),
      t('host.disable.title'),
      {
        confirmButtonText: t('host.action.disable'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    // 用户取消：开关维持原样（它是 :model-value 绑定的，不是双向绑定）
    return
  }

  markBusy(host.name, true)
  try {
    await updateHost(host.name, { enabled: false })
    ElMessage({ type: 'success', message: t('host.disable.done', { name: host.name }) })
  } finally {
    await load()
    markBusy(host.name, false)
  }
}

async function onDelete(host: AdminHostSummary): Promise<void> {
  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('host.confirm.delete.message', { name: host.name })),
        h('span', { class: 'text-content-muted text-xs' }, t('host.confirm.delete.soft'))
      ]),
      t('host.confirm.delete.title'),
      {
        confirmButtonText: t('common.delete'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    return
  }

  markBusy(host.name, true)
  try {
    await deleteHost(host.name)
    ElMessage({ type: 'success', message: t('host.delete.done', { name: host.name }) })
  } finally {
    await load()
    markBusy(host.name, false)
  }
}

function openCreateGroup(): void {
  editingGroup.value = null
  groupDialogVisible.value = true
}

function openEditGroup(group: HostGroup): void {
  editingGroup.value = group
  groupDialogVisible.value = true
}

async function onGroupSaved(): Promise<void> {
  groupDialogVisible.value = false
  await load()
}

/**
 * 删除分组。
 *
 * 确认框里必须写清**主机不会被删**——「删除」这个词在管理界面里默认让人想到级联删除，
 * 不说明的话，管理员会以为主机也跟着没了（而它们其实只是回到了未分组）。
 */
async function onDeleteGroup(group: HostGroup): Promise<void> {
  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('host.group.confirm.delete.message', { name: groupLabel(group, t) })),
        h('span', { class: 'text-content-muted text-xs' }, t('host.group.confirm.delete.keepHosts'))
      ]),
      t('host.group.confirm.delete.title'),
      {
        confirmButtonText: t('common.delete'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    return
  }

  const moved = await deleteHostGroup(group.id)

  ElMessage({
    type: 'success',
    message: moved > 0 ? t('host.group.delete.moved', { count: moved }) : t('host.group.delete.done'),
    showClose: moved > 0
  })
  await load()
}

watch(() => t, load, { immediate: true })
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-data-center"
      :title="t('nav.hosts')"
      :subtitle="t('page.hosts.subtitle')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <el-button type="primary" @click="openCreate">{{ t('host.action.create') }}</el-button>
      </template>
    </PageHeader>

    <SectionCard :title="t('host.list.title')" density="flush">
      <template #actions>
        <span class="text-xs text-content-muted">{{ t('host.total', { total: hosts.length }) }}</span>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <el-table
        v-else
        v-loading="loading"
        :data="hosts"
        :row-key="(row: AdminHostSummary) => row.id"
        empty-text=""
      >
        <el-table-column :label="t('host.col.name')" min-width="180">
          <template #default="{ row }">
            <div class="min-w-0">
              <div class="font-medium text-content-strong app-truncate">{{ row.name }}</div>
              <div class="text-xs text-content-faint">
                {{ row.type === 'socket' ? t('host.type.socket') : t('host.type.tcp') }}
              </div>
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="t('host.col.endpoint')" min-width="220">
          <template #default="{ row }">
            <span class="font-mono text-xs break-all">{{ row.endpoint }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('host.col.auth')" width="110">
          <template #default="{ row }">
            <span class="text-xs">{{ authLabel(row) }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('host.col.group')" width="140">
          <template #default="{ row }">
            <span v-if="row.groupId" class="text-xs">
              {{ groupNameById.get(row.groupId) ?? '—' }}
            </span>
            <span v-else class="text-xs text-content-faint">{{ t('host.group.ungrouped') }}</span>
          </template>
        </el-table-column>

        <!-- 三态：未探测（null）与离线（false）必须能区分，否则会让人去查网络而病因在别处 -->
        <el-table-column :label="t('host.col.reachable')" width="110">
          <template #default="{ row }">
            <span class="text-xs" :class="reachableClass(row)">{{ reachableLabel(row) }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('host.col.enabled')" width="90">
          <template #default="{ row }">
            <el-switch
              :model-value="row.enabled"
              :loading="busyNames.has(row.name)"
              @change="toggleEnabled(row)"
            />
          </template>
        </el-table-column>

        <el-table-column :label="t('container.col.actions')" width="100" align="right">
          <template #default="{ row }">
            <div class="flex items-center justify-end gap-0.5">
              <IconActionButton icon="i-carbon-edit" :label="t('common.edit')" @click="openEdit(row)" />
              <IconActionButton
                icon="i-carbon-trash-can"
                :label="t('common.delete')"
                tone="danger"
                :loading="busyNames.has(row.name)"
                @click="onDelete(row)"
              />
            </div>
          </template>
        </el-table-column>
      </el-table>

      <EmptyState v-if="isEmpty" icon="i-carbon-data-center" :description="t('host.empty')">
        <el-button type="primary" @click="openCreate">{{ t('host.action.create') }}</el-button>
      </EmptyState>
    </SectionCard>

    <SectionCard class="app-section" :title="t('host.group.listTitle')" :description="t('host.group.listHint')" density="flush">
      <template #actions>
        <el-button @click="openCreateGroup">{{ t('host.group.action.create') }}</el-button>
      </template>

      <div v-if="groups.length === 0" class="p-6">
        <EmptyState compact icon="i-carbon-folder" :description="t('host.group.empty')" />
      </div>

      <div v-else class="app-table-wrap">
        <table class="app-table">
          <thead>
            <tr>
              <th class="px-4">{{ t('host.group.name') }}</th>
              <th>{{ t('host.group.description') }}</th>
              <th class="text-right">{{ t('host.group.hostCount') }}</th>
              <th class="text-right pr-4">{{ t('container.col.actions') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="group in groups" :key="group.id">
              <td class="px-4">
                <div class="text-sm text-content-strong">{{ groupLabel(group, t) }}</div>
                <!-- 内置分组的名字是英文标识，副行给出原始值，方便与 API / 日志对上 -->
                <div v-if="group.name !== groupLabel(group, t)" class="font-mono text-xs text-content-faint">
                  {{ group.name }}
                </div>
              </td>
              <td class="text-xs text-content-muted">
                {{ groupDescription(group, t) ?? '—' }}
              </td>
              <td class="text-right text-sm tabular-nums">{{ group.hostCount }}</td>
              <td class="text-right pr-4">
                <div class="flex items-center justify-end gap-0.5">
                  <IconActionButton
                    icon="i-carbon-edit"
                    :label="t('common.edit')"
                    @click="openEditGroup(group)"
                  />
                  <IconActionButton
                    icon="i-carbon-trash-can"
                    :label="t('common.delete')"
                    tone="danger"
                    @click="onDeleteGroup(group)"
                  />
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </SectionCard>

    <HostFormDialog
      v-model="hostDialogVisible"
      :host="editingHost"
      :groups="groupOptions"
      @saved="onHostSaved"
    />
    <GroupFormDialog v-model="groupDialogVisible" :group="editingGroup" @saved="onGroupSaved" />
  </div>
</template>
