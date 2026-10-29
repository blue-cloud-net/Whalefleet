<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import { ApiRequestError } from '@/api'
import { listHostEvents } from '@/api/audit'
import {
  AUDIT_OUTCOMES,
  auditActionLabel,
  auditOutcomeKey,
  auditOutcomeTone
} from '@/utils/audit-display'
import { formatDateTime } from '@/utils/datetime'
import type { AuditLogEntry, AuditLogQuery, AuditOutcome } from '@/types/api'

/**
 * 主机侧「事件」页 = 按当前主机过滤的**活动日志**（0.9.0 转正）。
 *
 * ## 为什么这页不是 Docker Events 流
 * 它回答的是「谁在这台主机上做了什么」，而不是守护进程自己发出的容器事件。
 * 两者都会出现「容器 foo 停止」这种字样，但前者带操作人、后者带退出码——
 * 混在一起会让人以为「没人动过它却停了」这件事能在这里查到原因。
 *
 * ## 为什么只有活动日志，没有认证日志
 * 登录 / 登出不绑定主机，硬塞进这里只会让「这台主机上发生过什么」变得不可信。
 * 认证日志在管理端的「审计日志」页。
 *
 * ## 为什么筛选维度比管理端少
 * 目标类型与主机都已被路径固定，再给一组筛选框等于让人重复选同一个值。
 * 保留的只有真正能区分记录的三个：关键字、操作人、结果。
 */
const { t } = useI18n()
const route = useRoute()

/** URL 上的 hostId（主机名）。以它为准而不是 store——深链进入时 store 可能还没写入。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

const items = ref<AuditLogEntry[]>([])
const total = ref(0)
const pageIndex = ref(0)
const pageSize = ref(20)
const loading = ref(false)

const search = ref('')
const actor = ref('')
const outcome = ref<AuditOutcome | ''>('')

/** 页脚文案。 */
const rangeText = computed(() => {
  if (total.value === 0) return t('audit.empty')

  const from = pageIndex.value * pageSize.value + 1
  const to = Math.min(total.value, from + items.value.length - 1)

  return t('audit.range', { from, to, total: total.value })
})

/** 组装查询条件（空串一律不发）。 */
function buildQuery(): AuditLogQuery {
  const query: AuditLogQuery = {
    pageIndex: pageIndex.value,
    pageSize: pageSize.value
  }

  if (search.value.trim().length > 0) query.search = search.value.trim()
  if (actor.value.trim().length > 0) query.actor = actor.value.trim()
  if (outcome.value !== '') query.outcome = outcome.value

  return query
}

/** 加载当前页。 */
async function load(): Promise<void> {
  if (!hostId.value) return

  loading.value = true

  try {
    const result = await listHostEvents(hostId.value, buildQuery())

    items.value = result.items
    total.value = result.total
    pageIndex.value = result.pageIndex
    pageSize.value = result.pageSize
  } catch (error) {
    items.value = []
    total.value = 0
    ElMessage.error(error instanceof ApiRequestError ? error.message : t('audit.loadFailed'))
  } finally {
    loading.value = false
  }
}

/** 筛选变化：回到第一页。 */
function onFilterChange(): void {
  pageIndex.value = 0
  void load()
}

/** 翻页。 */
function onPageChange(next: number): void {
  pageIndex.value = next
  void load()
}

/** 结果药丸的样式类。 */
function toneClass(entry: AuditLogEntry): string {
  switch (auditOutcomeTone(entry.outcome)) {
    case 'ok':
      return 'text-status-ok'
    case 'warn':
      return 'text-status-warn'
    default:
      return 'text-status-down'
  }
}

/**
 * 切换主机：清空筛选并回到第一页。
 *
 * 不清筛的话，上一台主机上敲的操作人会一直生效，界面表现为「这台主机没有任何操作」——
 * 而记录其实一直有，只是被一个看不见的条件挡住了。
 */
watch(hostId, () => {
  items.value = []
  total.value = 0
  pageIndex.value = 0
  search.value = ''
  actor.value = ''
  outcome.value = ''
  void load()
})

onMounted(() => {
  void load()
})
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-events"
      :title="t('nav.events')"
      :subtitle="t('page.events.title')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('audit.refresh') }}</el-button>
      </template>
    </PageHeader>

    <div class="app-section flex flex-col gap-3">
      <div class="flex flex-wrap items-center gap-2">
        <el-input
          v-model="search"
          class="!w-56"
          clearable
          :placeholder="t('audit.filter.search')"
          @change="onFilterChange"
        />
        <el-input
          v-model="actor"
          class="!w-40"
          clearable
          :placeholder="t('audit.filter.actor')"
          @change="onFilterChange"
        />
        <el-select
          v-model="outcome"
          class="!w-36"
          :placeholder="t('audit.filter.outcome')"
          @change="onFilterChange"
        >
          <el-option :label="t('audit.filter.anyOutcome')" value="" />
          <el-option
            v-for="item in AUDIT_OUTCOMES"
            :key="item"
            :label="t(auditOutcomeKey(item))"
            :value="item"
          />
        </el-select>
        <span class="text-xs text-content-faint">{{ t('audit.windowHint') }}</span>
      </div>

      <div class="app-card overflow-hidden">
        <el-table v-loading="loading" :data="items" size="small" row-key="id">
          <el-table-column :label="t('audit.col.time')" width="170">
            <template #default="{ row }">{{ formatDateTime(row.occurredAt) }}</template>
          </el-table-column>
          <el-table-column :label="t('audit.col.actor')" width="140" prop="actorName" />
          <el-table-column :label="t('audit.col.action')" min-width="180">
            <template #default="{ row }">
              <span :title="row.action">{{ auditActionLabel(row.action, (key) => t(key)) }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="t('audit.col.target')" min-width="180">
            <template #default="{ row }">
              <span class="font-mono text-xs">{{ row.targetName ?? '—' }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="t('audit.col.outcome')" width="110">
            <template #default="{ row }">
              <span :class="toneClass(row)">{{ t(auditOutcomeKey(row.outcome)) }}</span>
              <span v-if="row.errorCode !== null" class="ml-1 text-xs text-content-faint">
                {{ row.errorCode }}
              </span>
            </template>
          </el-table-column>
          <el-table-column :label="t('audit.col.clientIp')" width="140">
            <template #default="{ row }">
              <span class="font-mono text-xs">{{ row.clientIp ?? '—' }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="t('audit.col.detail')" min-width="200">
            <template #default="{ row }">
              <span class="text-xs text-content-muted app-truncate">{{ row.detail ?? '—' }}</span>
            </template>
          </el-table-column>
        </el-table>

        <div class="flex items-center justify-between px-4 py-3">
          <span class="text-xs text-content-faint">{{ rangeText }}</span>
          <el-pagination
            layout="prev, pager, next"
            :current-page="pageIndex + 1"
            :page-size="pageSize"
            :total="total"
            @current-change="onPageChange"
          />
        </div>
      </div>
    </div>
  </div>
</template>