<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { TableInstance } from 'element-plus'
import ContainerStatusBadge from '@/components/business/ContainerStatusBadge.vue'
import MetricSparkline from '@/components/business/MetricSparkline.vue'
import { describePorts, type PortDisplay } from '@/utils/container-status'
import { formatDateTime } from '@/utils/datetime'
import { TIP_DELAY_MS } from '@/utils/tooltip'
import { formatBytes, formatPercent } from '@/utils/log-format'
import { peakOf } from '@/utils/metric-chart'
import { CONTAINER_COLUMN_DEFAULTS, type ContainerColumn } from '@/stores/preferences'
import type {
  ContainerHealthSummary,
  ContainerStatsSample,
  ContainerSummary
} from '@/types/api'

/**
 * 容器列表表格（0.10.0 抽出，供容器列表页与编排详情页共用）。
 *
 * ## 为什么抽出来
 * 编排详情页的主体是「这个 Compose 项目下的容器」——它要**和服务端返回的容器
 * 长得一模一样**：同样的状态徽标、同样的端口链接、同样的指标列。
 * 复制一份表格过去等于把这份渲染抄第二遍，而抄第二遍的代价是：
 * 容器列表页改了端口展示规则，编排详情页不会跟着改，
 * 用户在两个页面看到同一批容器的不同信息量——
 * **症状是「这里显示的端口怎么和那边不一样」，几乎不可能靠读代码定位**。
 *
 * ## 行动作列用插槽，不写死
 * 两个页面的动作**不一样**：
 * - 容器列表页：详情 / 启停 / 重启 / 日志 / 删除 + 「更多」下拉（九个操作）；
 * - 编排详情页：详情 / 启停 / 重启 / 日志 / 终端（**不给删除**——
 *   compose 会把被删的容器重新建出来，那个按钮点下去只是让用户白等一次重建）。
 *
 * 因此动作列的内容由 `row-actions` 插槽提供。
 * 组件只负责**列的骨架**（宽度、顺序、渲染），「有哪些动作、点了做什么」归调用方。
 *
 * ## 端口、状态、色调一律复用现成实现
 * `describePorts` / `containerDisplayState` / `ContainerStatusBadge` 都是既有资产，
 * 这里**不重新实现**——逻辑只有一处，改一处就够。
 */
const props = withDefaults(
  defineProps<{
    /** 表格数据。 */
    containers: ContainerSummary[]
    /** 容器 ID → 健康与退出原因；查不到的容器不出现。 */
    healthMap?: Record<string, ContainerHealthSummary>
    /** 容器 ID → 采样序列（指标列用）。 */
    series?: Record<string, ContainerStatsSample[]>
    /**
     * 各列可见性。
     *
     * ⚠️ **名称列不在这里**：它既是行身份、又是进详情的唯一入口，
     * 关掉就得到一张「点不进任何地方」的表。
     *
     * 默认值取自已定义在 store 里的 `CONTAINER_COLUMN_DEFAULTS`（不重新写一份）——
     * 两份默认值分家时，编排详情页与容器列表页会就「默认显示哪几列」给出不同答案。
     */
    columns?: Record<ContainerColumn, boolean>
    /** 是否显示选择列（批量操作）。 */
    selectable?: boolean
    /** 加载中。 */
    loading?: boolean
    /** 是否显示「服务」列（compose 服务名）。 */
    showServiceColumn?: boolean
    /** 是否显示行动作列。 */
    showActions?: boolean
  }>(),
  {
    healthMap: () => ({}),
    series: () => ({}),
    columns: () => ({ ...CONTAINER_COLUMN_DEFAULTS }),
    selectable: true,
    loading: false,
    showServiceColumn: false,
    showActions: true
  }
)

const emit = defineEmits<{
  /** 名称被点击（进容器详情）。 */
  openDetail: [container: ContainerSummary]
  /** 选择变化。 */
  selectionChange: [rows: ContainerSummary[]]
  /** 表头排序（`prop` / `order` 与 EP 一致）。 */
  sortChange: [payload: { prop: string | null; order: string | null }]
}>()

const { t } = useI18n()

/**
 * 表格实例。
 *
 * ⚠️ **通过 `defineExpose` 交出去**而不是让调用方传 ref 进来：
 * prop 是只读的，父组件传进来的 ref 对象在子组件里改它属于「改 props」，
 * Vue 会告警，而告警在开发时一闪而过、很容易被忽略。
 */
const tableRef = ref<TableInstance | null>(null)

defineExpose({
  /** 清空选择（筛选变化、批量动作执行后由调用方调用）。 */
  clearSelection: () => tableRef.value?.clearSelection()
})

/**
 * 每个容器的端口展示结果（容器 ID → 分好组的端口）。
 *
 * ⚠️ **在 `computed` 里算一次**而不是在模板里调函数：
 * 模板里的 `portGroups(row)` 每渲染一次就算一次，而它内部要分组 + 排序——
 * 一页 20 行 × 每次刷新都在重复做同一件事。
 */
const portGroups = computed<Record<string, PortDisplay[]>>(() => {
  const map: Record<string, PortDisplay[]> = {}

  for (const container of props.containers) {
    map[container.id] = describePorts(container.ports)
  }

  return map
})

/**
 * 一个指标单元格要渲染的全部东西。
 *
 * ## 为什么把「文案 + 采样 + 上限 + 提示」一起算好
 * 模板里分开算的话，同一个单元格要连续调四五次函数（取采样、取峰值、格式化、
 * 拼 tooltip），而每次都是同一批数据；更重要的是**上限必须和文案成对出现**——
 * 文案是「0.5%」而上限是另一处算出来的 100，一旦两处口径不一致，
 * 用户看到的就是一条「按不同纵轴画的线」。
 */
interface MetricCell {
  /** 主文案。 */
  text: string
  /** 采样序列（旧 → 新）。 */
  values: (number | null)[]
  /** 纵轴上限。 */
  max: number
  /** 图表与数值共用的悬浮提示（含轴范围）。 */
  label: string
}

interface MetricCells {
  cpu: MetricCell
  memory: MetricCell
  network: MetricCell
}

/**
 * 把采样序列换算成三个指标单元格（容器 ID → 单元格）。
 *
 * 在 `computed` 里一次算完，而不是在模板里按需算：模板里那种写法会在
 * 每次回流（每秒一批）时把同一页的 100 行重算 3 遍，而结果完全一样。
 *
 * ## 三个纵轴上限的取法各不相同（但它们都有**绝对含义**）
 * | 指标 | 上限 | 理由 |
 * |------|------|------|
 * | CPU | 100（峰值超 100 时取峰值） | 百分比；多核并行会让它超 100 |
 * | 内存 | 100（= 占上限的百分比） | 「离打满还有多远」才是要看的 |
 * | 网络 | 峰值 | 速率没有天然上限，只能自比 |
 *
 * 内存上限未设（`memoryLimit === 0`）时退回「按用量峰值画」：
 * 没有分母就没有百分比，画一条占比恒为 0 的直线等于声称「它一直没占内存」。
 */
const metricViews = computed<Record<string, MetricCells | undefined>>(() => {
  const map: Record<string, MetricCells | undefined> = {}

  for (const container of props.containers) {
    const samples = props.series[container.id]

    if (!samples || samples.length === 0) continue

    const sample = samples[samples.length - 1]
    if (!sample) continue

    const count = samples.length

    // ---- CPU ----
    const cpuValues = samples.map((item) => item.cpuPercent)
    const cpuMax = Math.max(100, peakOf(cpuValues))

    // ---- 内存 ----
    // 只要有一条采样带上限，就统一按「占上限的百分比」画；
    // 中途改变上限（热更新）会让曲线跳一下，但那是事实。
    const hasMemoryLimit = samples.some((item) => item.memoryLimit > 0)
    const memoryValues = hasMemoryLimit
      ? samples.map((item) =>
          item.memoryLimit > 0 ? (item.memoryUsage / item.memoryLimit) * 100 : null
        )
      : samples.map((item) => item.memoryUsage)
    const memoryMax = hasMemoryLimit ? 100 : peakOf(memoryValues)

    // ---- 网络 ----
    const networkValues = samples.map((item) => item.networkRxBytesPerSecond)
    const networkMax = peakOf(networkValues)

    // 内存的**可见文案只给已用量**：`160.0 MB / 62.5 GB` 在 110px 的列里会被折行，
    // 而折行后一行指标就变成了两行文字，整张表的行高跟着被撑开。
    // 上限没有丢——它在 tooltip 里（用户真正想知道「离满还有多远」时会悬停）。
    const memoryLabel =
      sample.memoryLimit > 0
        ? t('container.col.memoryTitle', {
            used: formatBytes(sample.memoryUsage),
            limit: formatBytes(sample.memoryLimit)
          })
        : t('container.col.memoryTitleNoLimit', { used: formatBytes(sample.memoryUsage) })

    map[container.id] = {
      cpu: {
        text: formatPercent(sample.cpuPercent),
        values: cpuValues,
        max: cpuMax,
        label: t('container.col.cpuTitle', { count, max: Math.round(cpuMax) })
      },
      memory: {
        text: formatBytes(sample.memoryUsage),
        values: memoryValues,
        max: memoryMax,
        label: memoryLabel
      },
      network: {
        text: formatRate(sample.networkRxBytesPerSecond),
        values: networkValues,
        max: networkMax,
        label: t('container.col.networkTitle', {
          count,
          peak: formatRate(networkMax)
        })
      }
    }
  }

  return map
})

/** 取某行的指标单元格（模板里要用它两三次，避免重复写索引）。 */
function metricView(row: ContainerSummary): MetricCells | undefined {
  return metricViews.value[row.id]
}

/**
 * 网络速率的展示。
 *
 * ⚠️ 不能用 `formatPercent`：速率的单位是字节/秒，百分比格式化会把它错写成 `x%`。
 */
function formatRate(bytesPerSecond: number | null): string {
  if (bytesPerSecond === null) return '—'

  if (bytesPerSecond >= 1024 * 1024) return `${(bytesPerSecond / 1024 / 1024).toFixed(1)} MB/s`
  if (bytesPerSecond >= 1024) return `${(bytesPerSecond / 1024).toFixed(0)} KB/s`

  return `${Math.round(bytesPerSecond)} B/s`
}
</script>

<template>
  <!--
    列宽分工：`min-width` 的列吃掉容器富余出来的宽度，`width` 的列纹丝不动。
       状态（徽标 + 一行状态文案）、创建时间（一段定长日期）、操作（一排定宽图标）、
       三个指标列都是内容长度可预期的列，给固定宽才不会随窗口缩放抖动；
       名称 / 镜像 / 端口三列内容长度不定，让它们按比例瓜分多余空间。

     ⚠️ **名称列没有 `v-if`**：它是行身份 + 进详情的唯一入口，
     不在 `columns` 开关里（理由见 props 的 `columns` 注释）。

       `sortable="custom"` 表示**本地不算、交给后端排**：Docker 侧要一次取全量才能定序，
       前端排当前页会得出一个看着对、全局错的顺序。 -->
  <el-table
    ref="tableRef"
    v-loading="loading"
    :data="containers"
    :row-key="(row: ContainerSummary) => row.id"
    empty-text=""
    @selection-change="emit('selectionChange', $event)"
    @sort-change="emit('sortChange', $event)"
  >
    <el-table-column v-if="selectable" type="selection" width="44" reserve-selection />

    <el-table-column prop="name" :label="t('container.col.name')" min-width="240" sortable="custom">
      <template #default="{ row }">
        <div class="min-w-0">
          <!--
            名称即详情入口：用 `<button>` 而不是 `<div @click>`。
            div 版本键盘 Tab 不到、读屏念不出「这是个可点的东西」，
            也无法响应 Enter / Space——而「点名称看详情」是最顺手的路径，
            不该只对鼠标用户成立。
            `text-left` + `w-fit`：默认 button 是居中的且撑满，直接套样式会把
            两行内容（名称 + 短 ID）居中排掉，左对齐的表格列会被撑歪。

            ⚠️ 悬浮提示**始终**给，不按「是否被截断」来判断：
            容器名往往带前缀与随机后缀，同一列里有的长有的短，
            用户没法从「这行没提示」推断出「这行没被截断」——
            提示在不在与内容长短无关，才能被当作可靠的入口。
          -->
          <el-tooltip :content="row.name" placement="top-start" :show-after="TIP_DELAY_MS">
            <button
              type="button"
              class="app-truncate block w-fit max-w-full text-left font-medium text-content-strong hover:text-brand-500 hover:underline"
              @click="emit('openDetail', row)"
            >
              {{ row.name }}
            </button>
          </el-tooltip>
          <el-tooltip :content="row.id" placement="top-start" :show-after="TIP_DELAY_MS">
            <div class="app-truncate font-mono text-xs text-content-faint">
              {{ row.id.slice(0, 12) }}
            </div>
          </el-tooltip>
        </div>
      </template>
    </el-table-column>

    <!--
      服务列（编排详情页专用）：容器名（`shop-api-1`）与 compose 服务名（`api`）
      不是一回事——用户在 Compose 文件里写的是服务名，
      而「哪个服务没起来」这个问题要靠服务名才能问出口。
    -->
    <el-table-column
      v-if="showServiceColumn"
      :label="t('container.col.service')"
      min-width="120"
    >
      <template #default="{ row }">
        <span class="text-sm text-content">{{ row.composeService ?? '—' }}</span>
      </template>
    </el-table-column>

    <el-table-column
      v-if="columns.image"
      prop="image"
      :label="t('container.col.image')"
      min-width="200"
      sortable="custom"
    >
      <template #default="{ row }">
        <!-- 镜像名同样始终给提示：长镜像名（带仓库地址 + 标签 + 摘要）会被截断 -->
        <el-tooltip :content="row.image" placement="top-start" :show-after="TIP_DELAY_MS">
          <span class="app-truncate block text-sm">{{ row.image }}</span>
        </el-tooltip>
      </template>
    </el-table-column>

    <!--
      状态列：**状态与健康合成一列**。
      「运行中」和「健康」回答的是同一个问题——「这个服务还好吗」，
      分成两列时一行里会出现「运行中 / 不健康」两个词，读者得自己判断哪个才是结论。
      合成后只有四种结论：健康 / 不健康（黄）/ 运行中 / 已停止。
    -->
    <el-table-column
      v-if="columns.state"
      prop="state"
      :label="t('container.col.state')"
      width="170"
      sortable="custom"
    >
      <template #default="{ row }">
        <div class="flex items-center gap-2">
          <ContainerStatusBadge
            :status="row.state"
            :health="healthMap[row.id]?.healthStatus ?? null"
          />
          <!-- 重启次数：健康信息自带的副产品，丢了可惜；跟着状态一起显示 -->
          <span
            v-if="(healthMap[row.id]?.restartCount ?? 0) > 0"
            class="text-xs text-status-warn app-tabular"
            :title="t('container.detail.restartCount')"
          >
            ↻ {{ healthMap[row.id]?.restartCount }}
          </span>
        </div>
        <div v-if="row.status" class="app-truncate mt-0.5 text-xs text-content-faint">
          {{ row.status }}
        </div>
      </template>
    </el-table-column>

    <!--
      三个指标列：**数值在上、迷你图在下**的纵向布局。

      ⚠️ 早先是横排（数值 + 80px 裸图），一列 130–150px 装不下
      「160.0 MB / 62.5 GB」这种文案，于是单元格内折行——一行指标变成两行文字，
      整张表的行高被撑开，看上去就是「挤在一堆」。
      纵向布局只占一列宽（112px），两行都是单行文本，不会折行。

      ⚠️ **宽度必须写死**：`min-width` 会让这三列随窗口缩放，
      而迷你图是拉满整宽的——列一宽，同一个百分比的变化幅度看起来就变缓了。

      ⚠️ 内存**只显示已用量**：上限放 tooltip。用户要判断「离满还有多远」时
      看图的高度比例就够了，而把「62.5 GB」一直摆在列里只会吃掉宽度。
    -->
    <el-table-column v-if="columns.cpu" :label="t('container.col.cpu')" width="112">
      <template #default="{ row }">
        <div v-if="metricView(row)" class="flex flex-col gap-1">
          <span class="font-mono text-xs app-tabular">{{ metricView(row)!.cpu.text }}</span>
          <MetricSparkline
            :values="metricView(row)!.cpu.values"
            :max="metricView(row)!.cpu.max"
            tone="cpu"
            :label="metricView(row)!.cpu.label"
          />
        </div>
        <span v-else class="text-xs text-content-faint">—</span>
      </template>
    </el-table-column>

    <el-table-column v-if="columns.memory" :label="t('container.col.memory')" width="112">
      <template #default="{ row }">
        <div v-if="metricView(row)" class="flex flex-col gap-1">
          <span
            class="font-mono text-xs app-tabular"
            :title="metricView(row)!.memory.label"
          >
            {{ metricView(row)!.memory.text }}
          </span>
          <MetricSparkline
            :values="metricView(row)!.memory.values"
            :max="metricView(row)!.memory.max"
            tone="memory"
            :label="metricView(row)!.memory.label"
          />
        </div>
        <span v-else class="text-xs text-content-faint">—</span>
      </template>
    </el-table-column>

    <el-table-column v-if="columns.network" :label="t('container.col.network')" width="112">
      <template #default="{ row }">
        <div v-if="metricView(row)" class="flex flex-col gap-1">
          <span class="font-mono text-xs app-tabular">{{ metricView(row)!.network.text }}</span>
          <MetricSparkline
            :values="metricView(row)!.network.values"
            :max="metricView(row)!.network.max"
            tone="network"
            :label="metricView(row)!.network.label"
          />
        </div>
        <span v-else class="text-xs text-content-faint">—</span>
      </template>
    </el-table-column>

    <!--
      端口列。

      ⚠️ **一行一个映射**：`-p 8080:80` 在 Docker 里是两条记录
      （`0.0.0.0` 与 `::`），逐条渲染会让同一个端口占两行、
      而两行长得几乎一样——用户会以为是两个端口。合并规则见 `describePorts`。

      ⚠️ **只有 TCP 才是链接**：UDP 没有浏览器能直接打开的语义，
      做成链接只会让人点开发现打不开。
    -->
    <el-table-column v-if="columns.ports" :label="t('container.col.ports')" min-width="220">
      <template #default="{ row }">
        <div
          v-if="(portGroups[row.id] ?? []).length > 0"
          class="flex flex-col gap-0.5 font-mono text-xs leading-relaxed"
        >
          <div
            v-for="(group, index) in portGroups[row.id] ?? []"
            :key="index"
            class="flex items-baseline gap-1"
          >
            <!--
              链接新窗口打开：`target="_blank"` + `rel="noopener noreferrer"`。
              ⚠️ `noopener` 不能省：新窗口里的页面能通过 `window.opener`
              反向操控本页（改地址、发起请求），而目标地址是用户自己填的端口。
            -->
            <el-tooltip
              v-if="group.url"
              :content="group.url"
              placement="top-start"
              :show-after="TIP_DELAY_MS"
            >
              <a
                :href="group.url"
                target="_blank"
                rel="noopener noreferrer"
                class="text-brand-500 hover:underline"
              >
                {{ group.text }}
              </a>
            </el-tooltip>
            <span v-else>{{ group.text }}</span>

            <!--
              同一端口绑了多个**具体**地址：只显示首个，其余收成「+N」。
              通配地址（0.0.0.0 / ::）不算——它们只表达「所有网卡」，列出来是噪声。
            -->
            <span
              v-if="group.extraIps.length > 0"
              class="text-content-faint"
              :title="group.extraIps.join('\n')"
            >
              +{{ group.extraIps.length }}
            </span>
          </div>
        </div>
        <span v-else class="text-xs text-content-faint">—</span>
      </template>
    </el-table-column>

    <el-table-column
      v-if="columns.created"
      prop="created"
      :label="t('container.col.created')"
      width="170"
      sortable="custom"
    >
      <template #default="{ row }">
        <span class="text-xs text-content-muted">{{ formatDateTime(row.created) }}</span>
      </template>
    </el-table-column>

    <!--
      行动作列：内容由调用方给（`row-actions` 插槽）。
      宽度固定 180px：六个 28px 圆形按钮（`IconActionButton` 的 `medium` 尺寸）
      紧邻排列，6×28=168px，留 12px 余量。
    -->
    <el-table-column v-if="showActions" :label="t('container.col.actions')" width="180">
      <template #default="{ row }">
        <div class="flex items-center">
          <slot name="row-actions" :row="row" />
        </div>
      </template>
    </el-table-column>
  </el-table>
</template>
