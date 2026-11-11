<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import { listNotifications, markNotificationsRead, removeNotifications } from '@/api/notification'
import { describeNotification } from '@/stores/notifications'
import { formatDateTime } from '@/utils/datetime'
import type { AlertSeverity, NotificationDto, NotificationSourceKind } from '@/types/api'

const { t } = useI18n()

const items = ref<NotificationDto[]>([])
const total = ref(0)
const unread = ref(0)
const pageIndex = ref(0)
const pageSize = ref(20)
const loading = ref(false)

const unreadOnly = ref(false)
const severity = ref<AlertSeverity | ''>('')
const sourceKind = ref<NotificationSourceKind | ''>('')
const search = ref('')

const isEmpty = computed(() => !loading.value && items.value.length === 0)

const severityOptions: AlertSeverity[] = ['info', 'warning', 'critical']
const sourceOptions: NotificationSourceKind[] = ['alert-fired', 'container-died', 'compose-task-failed']

/** 严重度 → el-tag 类型。 */
function severityType(value: AlertSeverity): 'info' | 'warning' | 'danger' {
  if (value === 'critical') return 'danger'
  if (value === 'warning') return 'warning'
  return 'info'
}

/** 渲染一条通知的文案。 */
function textOf(item: NotificationDto): string {
  const description = describeNotification(item)
  return t(description.key, description.params)
}

async function load(): Promise<void> {
  loading.value = true
  try {
    const result = await listNotifications({
      unreadOnly: unreadOnly.value || null,
      severity: severity.value || null,
      sourceKind: sourceKind.value || null,
      search: search.value.trim() || null,
      pageIndex: pageIndex.value,
      pageSize: pageSize.value
    })

    items.value = result.items
    total.value = result.total
    unread.value = result.unread
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    loading.value = false
  }
}

async function onMarkAllRead(): Promise<void> {
  await markNotificationsRead(undefined, true)
  ElMessage.success(t('notification.markedAllRead'))
  await load()
}

async function onRemove(item: NotificationDto): Promise<void> {
  await removeNotifications([item.id])
  await load()
}

function onSearch(): void {
  pageIndex.value = 0
  void load()
}

function onReset(): void {
  unreadOnly.value = false
  severity.value = ''
  sourceKind.value = ''
  search.value = ''
  pageIndex.value = 0
  void load()
}

onMounted(load)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-notification"
      :title="t('notification.center.title')"
      :subtitle="t('notification.center.subtitle')"
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <el-button type="primary" :disabled="unread === 0" @click="onMarkAllRead">
          {{ t('notification.markAllRead') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard>
      <div class="flex flex-wrap items-center gap-3 mb-4">
        <el-input
          v-model="search"
          class="!w-56"
          clearable
          :placeholder="t('common.search')"
          @keyup.enter="onSearch"
        />
        <el-select v-model="severity" class="!w-36" clearable :placeholder="t('notification.severity')">
          <el-option v-for="value in severityOptions" :key="value" :label="t(`notification.severityValue.${value}`)" :value="value" />
        </el-select>
        <el-select v-model="sourceKind" class="!w-44" clearable :placeholder="t('notification.source')">
          <el-option v-for="value in sourceOptions" :key="value" :label="t(`notification.sourceValue.${value}`)" :value="value" />
        </el-select>
        <el-checkbox v-model="unreadOnly">{{ t('notification.unreadOnly') }}</el-checkbox>
        <el-button @click="onSearch">{{ t('common.search') }}</el-button>
        <el-button text @click="onReset">{{ t('common.reset') }}</el-button>
        <span class="text-xs text-content-muted ml-auto">
          {{ t('notification.unreadSummary', { count: unread }) }}
        </span>
      </div>

      <EmptyState v-if="isEmpty" icon="i-carbon-notification" :title="t('notification.empty')" />

      <el-table v-else v-loading="loading" :data="items" row-key="id">
        <el-table-column :label="t('notification.occurredAt')" width="180">
          <template #default="{ row }">{{ formatDateTime(row.occurredAt) }}</template>
        </el-table-column>
        <el-table-column :label="t('notification.severity')" width="120">
          <template #default="{ row }">
            <el-tag :type="severityType(row.severity)" size="small">
              {{ t(`notification.severityValue.${row.severity}`) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="t('notification.message')" min-width="320">
          <template #default="{ row }">
            <span :class="row.read ? 'text-content-muted' : 'text-content font-medium'">
              {{ textOf(row) }}
            </span>
          </template>
        </el-table-column>
        <el-table-column :label="t('notification.host')" width="180">
          <template #default="{ row }">{{ row.hostName || '—' }}</template>
        </el-table-column>
        <el-table-column :label="t('notification.source')" width="160">
          <template #default="{ row }">{{ t(`notification.sourceValue.${row.sourceKind}`) }}</template>
        </el-table-column>
        <el-table-column :label="t('common.actions')" width="140" align="right">
          <template #default="{ row }">
            <el-button text type="danger" size="small" @click="onRemove(row)">
              {{ t('common.delete') }}
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-if="total > pageSize"
        class="mt-4 justify-end"
        layout="prev, pager, next, total"
        :total="total"
        :page-size="pageSize"
        :current-page="pageIndex + 1"
        @current-change="(page: number) => { pageIndex = page - 1; void load() }"
      />
    </SectionCard>
  </div>
</template>
