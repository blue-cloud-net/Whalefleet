<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { useAuthStore } from '@/stores/auth'
import type { HostOverview, HostCounts } from '@/types/api'
import { formatUptime, EMPTY, type DurationUnits } from '@/utils/datetime'
import { hostTone, HOST_TONE_CHIP, HOST_TONE_LABEL_KEY, HOST_TONE_RAIL } from '@/utils/host-status'
import StatusDot from '@/components/common/StatusDot.vue'

const props = defineProps<{
  host: HostOverview
}>()

const router = useRouter()
const { t } = useI18n()
const auth = useAuthStore()

/**
 * 资源计数：由 store 的 `loadHostDetails()` 从单台概览端点（`POST /hosts/{hostId}/overview`）补齐。
 * 列表端点刻意不带计数，因此刚进页面时短暂为空 → 显示「—」。
 */
const stats = computed(() => [
  { key: 'containers', label: t('nav.containers'), icon: 'i-carbon-container-software' },
  { key: 'images', label: t('nav.images'), icon: 'i-carbon-image' },
  { key: 'volumes', label: t('nav.volumes'), icon: 'i-carbon-data-volume' },
  { key: 'networks', label: t('nav.networks'), icon: 'i-carbon-network-3' }
])

/**
 * 磁贴数值。
 *
 * 容器磁贴显示「运行 / 总数」而不是总数：单看 `37` 无法判断这台机器是不是已经挂了
 * 一大半容器，`12/37` 一眼就能看出来。运行数缺失时（离线主机 / 旧版后端响应）
 * 退回只显示总数，**绝不**拼出 `undefined/37`。
 */
function statValue(key: string): string {
  const counts = props.host.counts
  if (!counts) return '—'

  if (key === 'containers') {
    return typeof counts.containersRunning === 'number'
      ? `${counts.containersRunning}/${counts.containers}`
      : String(counts.containers)
  }

  const value = counts[key as keyof HostCounts]

  return typeof value === 'number' ? String(value) : '—'
}

/** 磁贴 tooltip：把「12/37」展开成完整语义，屏幕阅读器与悬停都能读到 */
function statTitle(key: string): string {
  const counts = props.host.counts
  if (!counts || key !== 'containers') return ''

  return t('host.counts.runningTotal', {
    running: counts.containersRunning,
    total: counts.containers
  })
}

/** 状态语义：ok / down / idle，配色与文案统一从 utils/host-status 取 */
const tone = computed(() => hostTone(props.host.status))

const statusLabel = computed(() => t(HOST_TONE_LABEL_KEY[tone.value]))

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
    class="app-card app-card-interactive group relative overflow-hidden px-4 py-3.5 flex flex-wrap items-center gap-x-6 gap-y-3 cursor-pointer focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-500 focus-visible:ring-offset-2 focus-visible:ring-offset-surface-page"
    @click="enter"
    @keydown="onKeydown"
  >
    <!-- 左侧状态色条：整行的状态一目了然，hover 时加亮 -->
    <span
      class="absolute left-0 top-0 bottom-0 w-1 transition-opacity duration-300 opacity-60 group-hover:opacity-100"
      :class="HOST_TONE_RAIL[tone]"
      aria-hidden="true"
    />

    <!-- 主机标识 -->
    <div class="min-w-0 flex-1 basis-56 pl-2">
      <div class="flex items-center gap-2">
        <StatusDot :status="host.status" size="md" />
        <h3 class="text-sm font-semibold text-content-strong app-truncate">
          {{ host.name }}
        </h3>
        <span class="app-chip shrink-0 bg-surface-sunken text-content-muted ring-line-subtle">
          <span class="text-xs" :class="typeIcon" />
          {{ host.type === 'socket' ? t('host.type.socket') : t('host.type.tcp') }}
        </span>
      </div>

      <p class="mt-1 text-xs text-content-muted app-truncate font-mono" :title="host.endpoint">
        {{ host.endpoint }}
      </p>

      <!-- 版本 / 运行时间：缺失时整行不渲染 -->
      <p v-if="metaItems.length > 0" class="mt-1 text-xs text-content-faint app-truncate">
        {{ metaItems.join(' · ') }}
      </p>
    </div>

    <!--
      资源计数（占位）：每项「图标 + 数值 + 名称」竖排对齐。

      宽度按断点给两档：窄屏保持 w-16，否则四块 96px + 12px 间隔 = 420px 会把
      整行撑破（390px 视口下卡片横向溢出 11px）；宽屏才放到 w-24 让「数据卷」
      这类三字标签有足够留白（实测 64px 时内容区仅余 4px）。
    -->
    <div class="flex items-stretch gap-1.5 sm:gap-3 shrink-0">
      <div
        v-for="s in stats"
        :key="s.key"
        class="app-stat-tile w-16 sm:w-24 flex flex-col items-center justify-center gap-0.5 sm:gap-1"
        :title="statTitle(s.key)"
      >
        <span class="text-base sm:text-lg leading-none text-content-faint" :class="s.icon" />
        <span
          class="text-sm sm:text-base font-semibold leading-tight app-tabular text-content-body whitespace-nowrap"
        >
          {{ statValue(s.key) }}
        </span>
        <span class="text-xs leading-none text-content-faint">{{ s.label }}</span>
      </div>
    </div>

    <!-- 状态 + 行内操作 -->
    <div class="flex items-center gap-1.5 shrink-0 ml-auto">
      <span class="app-chip" :class="HOST_TONE_CHIP[tone]">
        {{ statusLabel }}
      </span>

      <!--
        操作按钮：常态半透明，hover 整行时才完全显形，保持列表干净。

        早先这里是三个按钮，其中「查看详情」与「状态图」**绑的是同一个 openStatus()**
        （详情本该指概览，而整行点击已经能进概览），属于纯粹的冗余，已删除。
      -->
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

      <span class="i-carbon-arrow-right text-sm text-content-faint shrink-0" aria-hidden="true" />
    </div>
  </div>
</template>

<style scoped>
/* hover 时的操作按钮显形依赖父级 group，此处仅补 group 声明 */
</style>