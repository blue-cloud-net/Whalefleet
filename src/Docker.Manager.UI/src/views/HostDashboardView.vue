<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useHostStore } from '@/stores/host'
import type { HostOverview } from '@/types/api'
import { formatDateTime } from '@/utils/datetime'
import HostHero from '@/components/business/HostHero.vue'
import ResourceTile from '@/components/business/ResourceTile.vue'
import SectionCard from '@/components/common/SectionCard.vue'

/**
 * 主机落地页（`/hosts/:hostId/dashboard`）。
 *
 * 职责刻意收窄为**这台机器的身份与技术信息**：连接端点、运行时长、Docker 版本，
 * 以及四类资源计数作为通往各资源页的入口。实时指标与事件归「状态」页，
 * 两页不再重复同一块内容（0.0.7 时它们几乎一模一样）。
 *
 * 这一页此前的问题不是样式，而是**缺失态没有被设计**：`counts` / `version` 缺失时
 * 信息卡整块不渲染、计数位只剩一个裸「—」，下半屏全白，看起来像加载失败。
 * 现在信息卡恒显、缺失字段显示弱化的占位符并在末尾给出统一说明。
 */
const route = useRoute()
const router = useRouter()
const { t } = useI18n()
const host = useHostStore()

const hostId = computed(() => String(route.params.hostId ?? ''))
const currentHost = computed<HostOverview | null>(
  () => host.hosts.find((h) => h.id === hostId.value) ?? null
)

type ResourceKey = 'containers' | 'images' | 'volumes' | 'networks'

/** 四类资源磁贴的图标与文案；顺序即页面顺序 */
const resources = computed<{ key: ResourceKey; label: string; icon: string }[]>(() => [
  { key: 'containers', label: t('nav.containers'), icon: 'i-carbon-container-software' },
  { key: 'images', label: t('nav.images'), icon: 'i-carbon-image' },
  { key: 'volumes', label: t('nav.volumes'), icon: 'i-carbon-data-volume' },
  { key: 'networks', label: t('nav.networks'), icon: 'i-carbon-network-3' }
])

/** 计数缺失（离线或聚合接口未就绪）时给磁贴一句原因，避免「—」被误读成「零」 */
const missingHint = computed(() =>
  currentHost.value?.status === 'offline'
    ? t('host.counts.unavailable')
    : t('host.info.pending')
)

function countOf(key: ResourceKey): number | null {
  return currentHost.value?.counts?.[key] ?? null
}

function goResource(key: ResourceKey) {
  router.push({ name: `host-${key}`, params: { hostId: hostId.value } })
}

/**
 * 刷新：主机列表目前来自本地种子（0.0.8 的列表接口就绪后才真正拉取），
 * 这里沿用与「编辑主机」一致的处理方式——明确告知未就绪，而不是点一下没反应。
 */
function onRefresh() {
  ElMessage.info(t('host.hero.refreshSoon'))
}

/** 缺失值的统一占位符，避免各处各写一个长得不一样的破折号 */
const MISSING = '—'

/**
 * 主机信息行：**恒显**，缺字段用占位符而非跳过整行。
 *
 * 早先的写法是「字段缺失就整行不 push」，于是离线主机这块直接消失、
 * 页面只剩四个破折号。现在缺失以弱化占位呈现，并在卡片末尾统一说明原因。
 */
const infoRows = computed<{ label: string; value: string }[]>(() => {
  const h = currentHost.value
  if (!h) return []

  const rows: { label: string; value: string }[] = []
  const v = h.version

  rows.push({ label: t('host.info.version'), value: v?.version ?? MISSING })
  rows.push({ label: t('host.info.apiVersion'), value: v?.apiVersion ?? MISSING })
  rows.push({
    label: t('host.info.os'),
    value: v?.os ? (v.arch ? `${v.os}/${v.arch}` : v.os) : MISSING
  })
  rows.push({ label: t('host.info.goVersion'), value: v?.goVersion ?? MISSING })
  rows.push({
    label: t('host.info.startedAt'),
    value: h.startedAt ? formatDateTime(h.startedAt) : MISSING
  })
  // 预留：Agent 模式排在 1.0.0 之后，届时后端才会返回 agentVersion
  if (h.agentVersion) rows.push({ label: t('host.info.agentVersion'), value: h.agentVersion })

  return rows
})

/** 只要有任意字段缺失，就在卡片末尾给出统一说明 */
const hasMissingInfo = computed(() => infoRows.value.some((r) => r.value === MISSING))
</script>

<template>
  <div class="app-stack">
    <HostHero :host="currentHost" :host-id="hostId" @refresh="onRefresh" />

    <!-- 四类资源：点击直达对应资源页 -->
    <div class="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-3">
      <ResourceTile
        v-for="r in resources"
        :key="r.key"
        :icon="r.icon"
        :label="r.label"
        :value="countOf(r.key)"
        :missing-hint="missingHint"
        @click="goResource(r.key)"
      />
    </div>

    <!-- 技术信息 -->
    <SectionCard v-if="currentHost" :title="t('host.info.title')" density="dense">
      <dl class="grid grid-cols-1 sm:grid-cols-2 gap-x-10">
        <!--
          标签定宽、值紧随其后（而非 justify-between 右对齐）：
          两列布局下每列约 520px，右对齐会让「标签」到「值」隔出四百多像素，
          眼睛必须横跨整列才能把两者关联起来。这里按定义列表的常规形态排布。
        -->
        <div
          v-for="row in infoRows"
          :key="row.label"
          class="flex items-baseline gap-4 py-2.5 border-b border-dashed border-line-subtle last:border-0"
        >
          <dt class="w-24 sm:w-28 shrink-0 text-xs text-content-muted">{{ row.label }}</dt>
          <dd
            class="min-w-0 text-[13px] font-mono app-truncate"
            :class="row.value === MISSING ? 'text-content-faint' : 'text-content-body'"
            :title="row.value === MISSING ? t('host.info.pending') : row.value"
          >
            {{ row.value }}
          </dd>
        </div>
      </dl>

      <p v-if="hasMissingInfo" class="mt-3 flex items-start gap-1.5 text-xs text-content-faint">
        <span class="i-carbon-information shrink-0 mt-px" aria-hidden="true" />
        <span>{{ t('host.info.pending') }}</span>
      </p>
    </SectionCard>
  </div>
</template>