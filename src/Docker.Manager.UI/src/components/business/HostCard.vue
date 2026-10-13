<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '@/stores/auth'
import type { HostOverview } from '@/types/api'
import { formatUptime, EMPTY, type DurationUnits } from '@/utils/datetime'
import StatusDot from '@/components/common/StatusDot.vue'

const props = defineProps<{
  host: HostOverview
}>()

const router = useRouter()
const { t } = useI18n()
const auth = useAuthStore()

/** 资源计数占位：后端聚合接口（0.0.8）就绪前显示「—」 */
const stats = computed(() => [
  { key: 'containers', label: t('nav.containers'), icon: 'i-carbon-container-software' },
  { key: 'images', label: t('nav.images'), icon: 'i-carbon-image' },
  { key: 'volumes', label: t('nav.volumes'), icon: 'i-carbon-data-volume' },
  { key: 'networks', label: t('nav.networks'), icon: 'i-carbon-network-3' }
])

const statusLabel = computed(() => {
  switch (props.host.status) {
    case 'online':
      return t('host.status.online')
    case 'offline':
      return t('host.status.offline')
    default:
      return t('host.status.unknown')
  }
})

/** 状态徽标配色：药丸形 chip，比纯文字更易扫视 */
const statusChip = computed(() => {
  switch (props.host.status) {
    case 'online':
      return 'bg-green-50 text-green-700 ring-green-200/70 dark:bg-green-500/15 dark:text-green-400 dark:ring-green-500/25'
    case 'offline':
      return 'bg-red-50 text-red-700 ring-red-200/70 dark:bg-red-500/15 dark:text-red-400 dark:ring-red-500/25'
    default:
      return 'bg-gray-50 text-gray-500 ring-gray-200/70 dark:bg-gray-700/60 dark:text-gray-400 dark:ring-gray-600/50'
  }
})

const typeIcon = computed(() =>
  props.host.type === 'socket' ? 'i-carbon-terminal' : 'i-carbon-cloud-satellite'
)

/** 进入该主机的概览页，切换为主机作用域（整行点击时触发） */
function enter() {
  router.push({ name: 'host-dashboard', params: { hostId: props.host.id } })
}

/** 跳转状态图页；总览卡片的「详情 / 状态图」按钮与该页面复用同一组件 */
function openStatus() {
  router.push({ name: 'host-status', params: { hostId: props.host.id } })
}

/**
 * 编辑功能待 0.0.13 的 `/api/v1/host/update` 就绪后接入，
 * 当前仅 admin 可见占位，避免普通用户看到点不动的按钮。
 */
function editHost() {
  ElMessage.info(t('host.actions.editSoon'))
}

/** 运行时长的单位文案走 i18n */
const durationUnits = computed<DurationUnits>(() => ({
  day: t('host.duration.day'),
  hour: t('host.duration.hour'),
  minute: t('host.duration.minute'),
  second: t('host.duration.second')
}))

const uptime = computed(() => formatUptime(props.host.startedAt, durationUnits.value))

/** 紧凑元信息：OS/Arch 与 Docker 版本；缺失时不渲染，避免塌版 */
const metaItems = computed(() => {
  const v = props.host.version
  const items: string[] = []
  if (v?.os) items.push(v.arch ? `${v.os}/${v.arch}` : v.os)
  if (v?.version) items.push(`Docker ${v.version}`)
  if (uptime.value !== EMPTY) items.push(t('status.uptime', { value: uptime.value }))
  return items
})

/** 键盘可达性：div 不能像 button 一样默认响应 Enter/Space */
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Enter' || e.key === ' ') {
    e.preventDefault()
    enter()
  }
}
</script>

<template>
  <!--
    长条形主机条目：整行可点进入主机。
    行内按钮用 @click.stop 阻止冒泡，避免触发整行导航。
  -->
  <div
    role="button"
    tabindex="0"
    :aria-label="t('host.actions.enterLabel', { name: host.name })"
    class="app-card app-card-interactive group relative overflow-hidden px-4 py-3.5 flex flex-wrap items-center gap-x-6 gap-y-3 cursor-pointer focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-500 focus-visible:ring-offset-2 focus-visible:ring-offset-gray-50 dark:focus-visible:ring-offset-gray-950"
    @click="enter"
    @keydown="onKeydown"
  >
    <!-- 左侧状态色条：整行的状态一目了然，hover 时加亮 -->
    <span
      class="absolute left-0 top-0 bottom-0 w-1 transition-all duration-300"
      :class="[
        host.status === 'online'
          ? 'bg-green-500'
          : host.status === 'offline'
            ? 'bg-red-500'
            : 'bg-gray-300 dark:bg-gray-600',
        'opacity-60 group-hover:opacity-100'
      ]"
      aria-hidden="true"
    />

    <!-- 主机标识 -->
    <div class="min-w-0 flex-1 basis-56 pl-2">
      <div class="flex items-center gap-2">
        <StatusDot :status="host.status" size="md" />
        <h3 class="text-sm font-semibold text-gray-900 dark:text-gray-100 app-truncate">
          {{ host.name }}
        </h3>
        <span
          class="app-chip shrink-0 text-gray-500 dark:text-gray-400 ring-gray-200/70 dark:ring-gray-600/50"
        >
          <span class="text-xs" :class="typeIcon" />
          {{ host.type === 'socket' ? t('host.type.socket') : t('host.type.tcp') }}
        </span>
      </div>

      <p class="mt-1 text-xs text-gray-500 dark:text-gray-400 app-truncate font-mono" :title="host.endpoint">
        {{ host.endpoint }}
      </p>

      <!-- 版本 / 运行时间：缺失时整行不渲染 -->
      <p
        v-if="metaItems.length > 0"
        class="mt-1 text-xs text-gray-400 dark:text-gray-500 app-truncate"
      >
        {{ metaItems.join(' · ') }}
      </p>
    </div>

    <!-- 资源计数（占位）：横向紧凑排布，每项「图标 + 数值 + 名称」竖排对齐 -->
    <div class="flex items-stretch gap-1.5 shrink-0">
      <div
        v-for="s in stats"
        :key="s.key"
        class="app-stat-tile w-16 flex flex-col items-center justify-center gap-0.5"
      >
        <span class="text-base leading-none text-gray-400 dark:text-gray-500" :class="s.icon" />
        <span class="text-sm font-semibold leading-tight app-tabular text-gray-600 dark:text-gray-300">
          {{ host.counts ? host.counts[s.key as keyof typeof host.counts] : '—' }}
        </span>
        <span class="text-xs leading-none text-gray-400 dark:text-gray-500">{{ s.label }}</span>
      </div>
    </div>

    <!-- 状态 + 行内操作 -->
    <div class="flex items-center gap-1.5 shrink-0 ml-auto">
      <span class="app-chip" :class="statusChip">
        {{ statusLabel }}
      </span>

      <!-- 操作按钮：常态半透明，hover 整行时才完全显形，保持列表干净 -->
      <div
        class="flex items-center gap-0.5 opacity-60 sm:opacity-0 sm:group-hover:opacity-100 transition-opacity duration-200 focus-within:opacity-100"
      >
        <el-tooltip :content="t('host.actions.edit')" placement="top">
          <el-button
            v-if="auth.isAdmin"
            text
            size="small"
            class="!px-1.5"
            :aria-label="t('host.actions.edit')"
            @click.stop="editHost"
          >
            <span class="i-carbon-edit text-base" />
          </el-button>
        </el-tooltip>

        <el-tooltip :content="t('host.actions.detail')" placement="top">
          <el-button
            text
            size="small"
            class="!px-1.5"
            :aria-label="t('host.actions.detail')"
            @click.stop="openStatus"
          >
            <span class="i-carbon-information text-base" />
          </el-button>
        </el-tooltip>

        <el-tooltip :content="t('host.actions.status')" placement="top">
          <el-button
            text
            size="small"
            class="!px-1.5"
            :aria-label="t('host.actions.status')"
            @click.stop="openStatus"
          >
            <span class="i-carbon-activity text-base" />
          </el-button>
        </el-tooltip>
      </div>

      <span class="i-carbon-arrow-right text-sm text-gray-300 dark:text-gray-600 shrink-0" />
    </div>
  </div>
</template>

<style scoped>
/* hover 时的操作按钮显形依赖父级 group，此处仅补 group 声明 */
</style>