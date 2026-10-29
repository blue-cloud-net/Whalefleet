<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import { ApiRequestError } from '@/api'
import { exportAuditLogs, listAuditLogs, type AuditCategory } from '@/api/audit'
import {
  AUDIT_OUTCOMES,
  AUDIT_TARGET_TYPES,
  auditActionLabel,
  auditOutcomeKey,
  auditOutcomeTone
} from '@/utils/audit-display'
import { formatDateTime } from '@/utils/datetime'
import type { AuditLogEntry, AuditLogQuery, AuditOutcome } from '@/types/api'

/**
 * 操作审计（0.9.0）：认证日志 + 活动日志两页。
 *
 * ## 为什么两页共用一个组件
 * 两者的差别只有「有没有目标 / 主机」与「筛选维度少两个」——表格、分页、导出全都一样。
 * 拆成两个组件意味着两套分页逻辑，而分页夹紧（pageSize 上限 100、页码回显）一旦分叉，
 * 就会出现「这边翻到 5 页、那边只给 3 页」这种只能靠对比才发现的偏差。
 *
 * ## 为什么不复用 ListToolbar
 * 那个组件是给「资源列表」用的（搜索 + 状态筛选 + 批量动作 + 列设置）。
 * 审计是**只读**的，套进去会带出一排永远禁用的批量按钮。
 *
 * ## 为什么时间默认不填
 * 服务端在时间省略时取**最近 7 天**——审计表只增不减，不设窗口的查询会随运行时间变慢。
 * 界面把这个口径写在提示里，而不是偷偷替用户填一个日期。
 */
const { t } = useI18n()

type Category = AuditCategory

const category = ref<Category>('authentication')
const items = ref<AuditLogEntry[]>([])
const total = ref(0)
const pageIndex = ref(0)
const pageSize = ref(20)
const loading = ref(false)
const exporting = ref(false)

const search = ref('')
const outcome = ref<AuditOutcome | ''>('')
const targetType = ref('')
const actor = ref('')

/** 当前页签是否为活动日志（只有它才有目标 / 主机列与这两项筛选）。 */
const isActivity = computed(() => category.value === 'activity')

/** 页脚文案。 */
const rangeText = computed(() => {
  if (total.value === 0) return t('audit.empty')

  const from = pageIndex.value * pageSize.value + 1
  const to = Math.min(total.value, from + items.value.length - 1)

  return t('audit.range', { from, to, total: total.value })
})

/**
 * 组装查询条件。
 *
 * 空串一律不发：后端把「空串」与「不筛选」当同一件事，但发送空串会让
 * 浏览器地址栏的复制分享变得更嘈杂，也让日志里的请求体更难读。
 */
function buildQuery(): AuditLogQuery {
  const query: AuditLogQuery = {
    pageIndex: pageIndex.value,
    pageSize: pageSize.value
  }

  if (search.value.trim().length > 0) query.search = search.value.trim()
  if (actor.value.trim().length > 0) query.actor = actor.value.trim()
  if (outcome.value !== '') query.outcome = outcome.value
  if (isActivity.value && targetType.value !== '') query.targetType = targetType.value

  return query
}

/** 加载当前页。 */
async function load(): Promise<void> {
  loading.value = true

  try {
    const result = await listAuditLogs(category.value, buildQuery())

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

/** 切换页签：页码归零并重新加载（否则会停在上一个页签的第 N 页）。 */
function onCategoryChange(name: string | number): void {
  category.value = name === 'activity' ? 'activity' : 'authentication'
  pageIndex.value = 0
  void load()
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

/**
 * 导出当前筛选结果。
 *
 * ⚠️ 导出走**独立请求**（不复用当前页数据）：它要的是「全部符合条件」的记录，
 * 而不是当前 20 条。服务端会在超过 5 万行时拒绝并给出提示。
 */
async function onExport(): Promise<void> {
  exporting.value = true

  try {
    const blob = await exportAuditLogs(category.value, buildQuery())

    // 失败时后端返回的是 JSON（ApiResult），而 responseType=blob 会把它也当二进制收下。
    if (blob.type.includes('json')) {
      const body = JSON.parse(await blob.text()) as { message?: string }

      ElMessage.error(body.message ?? t('audit.exportFailed'))

      return
    }

    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `audit-${category.value}-${new Date().toISOString().slice(0, 16).replace(/[:T]/g, '')}.csv`
    link.click()
    URL.revokeObjectURL(url)

    ElMessage.success(t('audit.exported'))
  } catch (error) {
    ElMessage.error(error instanceof ApiRequestError ? error.message : t('audit.exportFailed'))
  } finally {
    exporting.value = false
  }
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

onMounted(() => {
  void load()
})
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-catalog"
      :title="t('audit.title')"
      :subtitle="t('audit.subtitle')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('audit.refresh') }}</el-button>
        <el-button type="primary" :loading="exporting" @click="onExport">
          {{ t('audit.export') }}
        </el-button>
      </template>
    </PageHeader>

    <div class="app-section flex flex-col gap-3">
      <el-tabs :model-value="category" @tab-change="onCategoryChange">
        <el-tab-pane :label="t('audit.tab.authentication')" name="authentication" />
        <el-tab-pane :label="t('audit.tab.activity')" name="activity" />
      </el-tabs>

      <!--
        筛选区：用 el-tabs 的 v-model 需要额外处理类型，这里改用 tab-change 事件，
        model-value 只做单向展示，避免 Category 与 string 的类型来回转换。
      -->
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
        <el-select
          v-if="isActivity"
          v-model="targetType"
          class="!w-40"
          :placeholder="t('audit.filter.targetType')"
          @change="onFilterChange"
        >
          <el-option :label="t('audit.filter.anyTarget')" value="" />
          <el-option v-for="item in AUDIT_TARGET_TYPES" :key="item" :label="item" :value="item" />
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
          <el-table-column v-if="isActivity" :label="t('audit.col.target')" min-width="180">
            <template #default="{ row }">
              <span class="font-mono text-xs">{{ row.targetName ?? '—' }}</span>
            </template>
          </el-table-column>
          <el-table-column v-if="isActivity" :label="t('audit.col.host')" width="120">
            <template #default="{ row }">{{ row.hostName ?? '—' }}</template>
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
