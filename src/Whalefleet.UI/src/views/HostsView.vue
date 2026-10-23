<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox, type TableInstance } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import HostFormDialog from '@/components/business/HostFormDialog.vue'
import GroupFormDialog from '@/components/business/GroupFormDialog.vue'
import TagManagerDialog from '@/components/business/TagManagerDialog.vue'
import HostTagAssignDialog from '@/components/business/HostTagAssignDialog.vue'
import HostTagBatchDialog from '@/components/business/HostTagBatchDialog.vue'
import BatchActionBar from '@/components/common/BatchActionBar.vue'
import { deleteHost, listAdminHosts, listHosts, updateHost } from '@/api/host'
import { deleteHostGroup, listAdminHostGroups } from '@/api/host-group'
import { listHostTags } from '@/api/host-tag'
import { ApiRequestError } from '@/api'
import { groupDescription, groupLabel } from '@/utils/group-display'
import type { AdminHostSummary, HostGroup, HostTag } from '@/types/api'

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

/** 标签（0.6.0）：管理弹窗 + 单主机打标弹窗。 */
const tagManagerVisible = ref(false)
const tagAssignVisible = ref(false)
const tagTarget = ref<AdminHostSummary | null>(null)

/**
 * 批量打标（0.7.0）。
 *
 * ⚠️ **批量范围是「主机」，不是「容器」**：分组是**主机**的分组，
 * 而容器列表是单主机视图（路由 `/hosts/{hostId}/containers`），
 * 因此「按分组批量启停容器」在本项目里没有承载界面——
 * 批量打标是对这个分组能力**唯一说得通**的用法。
 */
const selectedHosts = ref<AdminHostSummary[]>([])
const tagBatchVisible = ref(false)
const tagBatchBusy = ref(false)

/**
 * 表格实例：只有 `clearSelection()` 用得到，但它是实例方法，
 * 模板 ref 是唯一拿得到它的方式（`BatchActionBar` 本身不该知道 el-table 的存在）。
 */
const tableRef = ref<TableInstance | null>(null)

/** 打开批量打标弹窗（选中了才允许）。 */
function openTagBatch(): void {
  if (selectedHosts.value.length === 0) return

  tagBatchVisible.value = true
}

/** 表格选区变化。 */
function onSelectionChange(rows: AdminHostSummary[]): void {
  selectedHosts.value = rows
}

/** 清空选区。 */
function clearSelection(table: { clearSelection: () => void } | null): void {
  table?.clearSelection()
  selectedHosts.value = []
}

/** 全量标签（下拉用）。 */
const tags = ref<HostTag[]>([])

/**
 * 主机名 → 标签主键。
 *
 * 管理端的主机列表（`AdminHostSummary`）**不带标签**（它是对外标识为 Guid 的另一种形状），
 * 而打标接口收的是主机名。因此这里额外拉一份展示用列表，按主机名对齐。
 */
const hostTagIds = ref<Record<string, string[]>>({})

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
    // 并行取三份：主机 / 分组 / 标签互不依赖，串行只会白等两个 RTT
    const [hostList, groupList, tagList, overview] = await Promise.all([
      listAdminHosts(),
      listAdminHostGroups(),
      listHostTagsLoose(),
      listHosts()
    ])

    hosts.value = hostList
    groups.value = groupList
    tags.value = tagList

    const map: Record<string, string[]> = {}

    for (const item of overview) {
      map[item.id] = (item.tags ?? []).map((tag) => tag.id)
    }

    hostTagIds.value = map
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

/** 标签列表拿不到时退化成空（不让整个管理页报错）。 */
async function listHostTagsLoose(): Promise<HostTag[]> {
  try {
    return await listHostTags()
  } catch {
    return []
  }
}

/** 打开打标弹窗。 */
function openTagAssign(host: AdminHostSummary): void {
  tagTarget.value = host
  tagAssignVisible.value = true
}

/** 打标完成后重拉（标签集合与计数都会变）。 */
async function onTagsChanged(): Promise<void> {
  await load()
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
        <el-button @click="tagManagerVisible = true">
          <span class="i-carbon-tag text-base" />
          {{ t('host.tag.manage') }}
        </el-button>
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

      <!--
        批量操作条与表格一起包在 <template v-else> 里——
        v-else 必须**紧跟** v-if 的兄弟节点，中间插任何元素都会让 Vue 报
        「v-else 没有相邻的 v-if」。
      -->
      <template v-else>
        <BatchActionBar :count="selectedHosts.length" :busy="tagBatchBusy" @clear="clearSelection(tableRef)">
          <el-button size="small" :disabled="tagBatchBusy" @click="openTagBatch">
            {{ t('host.tag.batch.action') }}
          </el-button>
        </BatchActionBar>

        <el-table
          v-loading="loading"
          :data="hosts"
          :row-key="(row: AdminHostSummary) => row.id"
          empty-text=""
          ref="tableRef"
          @selection-change="onSelectionChange"
        >
          <el-table-column type="selection" width="44" reserve-selection />
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

          <!--
            标签（0.6.0）：点「打标」打开多选弹窗。
            ⚠️ 标签与分组是两种组织方式：分组是单一归属，标签是多对多自由打标；两者都不参与授权。
          -->
          <el-table-column :label="t('host.tag.title')" min-width="200">
            <template #default="{ row }">
              <div class="flex flex-wrap items-center gap-1">
                <span
                  v-for="tag in tags.filter((item) => (hostTagIds[row.name] ?? []).includes(item.id))"
                  :key="tag.id"
                  class="app-chip bg-surface-sunken text-content-muted ring-line-subtle"
                >
                  <span
                    class="h-1.5 w-1.5 rounded-full shrink-0"
                    :style="{ backgroundColor: tag.color ?? 'var(--brand-500)' }"
                    aria-hidden="true"
                  />
                  {{ tag.name }}
                </span>

                <el-button text size="small" class="!px-1.5" @click="openTagAssign(row)">
                  <span class="i-carbon-add-alt text-sm" />
                </el-button>
              </div>
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

          <!--
            与容器 / 镜像 / 用户页共用 IconActionButton：图标 + tooltip 文字标签。
            列宽 64 = 两个 24px 圆形按钮；省略 `align` 即左对齐，表头与图标同侧才不看着错位。
          -->
          <el-table-column :label="t('container.col.actions')" width="64">
            <template #default="{ row }">
              <div class="flex items-center">
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
      </template>

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
              <th class="pr-4">{{ t('container.col.actions') }}</th>
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
              <td class="pr-4">
                <div class="flex items-center">
                  <IconActionButton icon="i-carbon-edit" :label="t('common.edit')" @click="openEditGroup(group)" />
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

    <TagManagerDialog v-model="tagManagerVisible" @changed="onTagsChanged" />

    <HostTagAssignDialog
      v-model="tagAssignVisible"
      :host="tagTarget"
      :tag-ids="tagTarget ? (hostTagIds[tagTarget.name] ?? []) : []"
      @saved="onTagsChanged"
    />

    <HostTagBatchDialog
      v-model="tagBatchVisible"
      :host-names="selectedHosts.map((host) => host.name)"
      @saved="onTagsChanged"
    />
  </div>
</template>
