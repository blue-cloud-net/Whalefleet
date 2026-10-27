<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import IconActionButton from '@/components/common/IconActionButton.vue'
import { batchDisabledReason, type ContainerAction } from '@/utils/container-actions'

/**
 * 容器批量动作条（0.7.0）。
 *
 * ## 为什么要自适应
 *
 * 七个动作全平铺需要约 650px；而工具条左侧还有筛选组（搜索框 + 状态下拉，
 * 约 250px）。两者相加在 1280px 的窗口上就把工具条挤到折行——
 * 而折行后的「批量动作」会被读成「筛选栏的第二行」，用户根本不会想到
 * 「那里还有七个按钮」。
 *
 * 因此按**可用宽度**分两档：
 * - 够宽 → 七个全平铺（可发现性最好，高频动作一次点到）；
 * - 不够 → 四个高频（启动 / 停止 / 重启 / 删除）常驻，其余（终止 / 暂停 / 继续）
 *   收进「更多」下拉。
 *
 * ## 为什么不是「全部收进下拉」
 *
 * 工具条上的批量动作是「这一页支持批量操作」的**唯一提示**。
 * 全收进下拉就等于把它藏起来——而常驻 + 禁用（未勾选时）既有可发现性，
 * 也不会让人怀疑页面坏了。
 *
 * ## 两个分支共用同一份动作表
 *
 * 平铺与下拉渲染的是**同一个数组**（`ACTIONS`），只是按 `compact` 分组。
 * 两处各写一份的话，加一个动作要改两个地方，而漏改的那个症状是
 * 「宽屏有、窄屏没有」——那种问题在开发机上根本看不出来。
 */
const { t } = useI18n()

const props = withDefaults(
  defineProps<{
    /** 已选中的容器状态；用于判定动作是否可用 */
    states: string[]
    /** 正在执行：禁用全部动作 */
    busy?: boolean
  }>(),
  { busy: false }
)

const emit = defineEmits<{ run: [action: ContainerAction] }>()

/**
 * 动作表（顺序即展示顺序）。
 *
 * ⚠️ **图标必须在 `uno.config.ts` 的 safelist 里**（动态字符串扫不到）。
 */
interface BatchAction {
  key: ContainerAction
  icon: string
  labelKey: string
  /** 收进「更多」下拉的动作（低频） */
  overflow?: boolean
  /** 危险色 */
  tone?: 'danger'
}

const ACTIONS: BatchAction[] = [
  { key: 'start', icon: 'i-carbon-play', labelKey: 'container.action.start' },
  { key: 'stop', icon: 'i-carbon-stop', labelKey: 'container.action.stop' },
  { key: 'restart', icon: 'i-carbon-restart', labelKey: 'container.action.restart' },
  { key: 'remove', icon: 'i-carbon-trash-can', labelKey: 'container.action.remove', tone: 'danger' },
  { key: 'kill', icon: 'i-carbon-warning', labelKey: 'container.action.kill', overflow: true },
  { key: 'pause', icon: 'i-carbon-pause', labelKey: 'container.action.pause', overflow: true },
  { key: 'resume', icon: 'i-carbon-play', labelKey: 'container.action.resume', overflow: true }
]

/** 容器壳；用它来量工具条左侧还剩多少宽度。 */
const host = ref<HTMLElement | null>(null)

/**
 * 是否进入紧凑模式。
 *
 * ⚠️ **量的是容器宽度减去筛选组的估算宽度**，而不是窗口宽度：
 * 筛选组本身会随视口变宽（`sm:!w-56`），只看窗口宽度会在某个区间里算错。
 */
const compact = ref(false)

/** 筛选组 + 批量组的估算占用（px）。 */
const RESERVED_WIDTH = 460

/** 紧凑模式下的切换阈值（px）：低于它就把低频动作收进下拉。 */
const COMPACT_BREAKPOINT = 700

function measure(): void {
  const element = host.value
  if (!element) return

  compact.value = element.clientWidth - RESERVED_WIDTH < COMPACT_BREAKPOINT
}

let observer: ResizeObserver | null = null

onMounted(() => {
  measure()
  observer = new ResizeObserver(measure)
  if (host.value) observer.observe(host.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
})

/** 平铺的动作（紧凑模式下只留四个高频的）。 */
const visibleActions = computed(() =>
  compact.value ? ACTIONS.filter((action) => !action.overflow) : ACTIONS
)

/** 收进「更多」下拉的动作。 */
const overflowActions = computed(() => ACTIONS.filter((action) => action.overflow))

/** 某个动作当前的禁用原因（i18n key）；可用时为 `null`。 */
function disabledReason(action: ContainerAction): string | undefined {
  if (props.busy) return undefined

  const reason = batchDisabledReason(action, props.states)

  return reason === null ? undefined : t(reason)
}

/** 动作是否禁用。 */
function isDisabled(action: ContainerAction): boolean {
  return props.busy || disabledReason(action) !== undefined
}
</script>

<template>
  <div ref="host" class="flex flex-wrap items-center gap-1">
    <IconActionButton
      v-for="action in visibleActions"
      :key="action.key"
      :icon="action.icon"
      :label="t(action.labelKey)"
      :tone="action.tone ?? 'default'"
      show-label
      placement="bottom"
      :disabled="isDisabled(action.key)"
      :hint="disabledReason(action.key)"
      @click="emit('run', action.key)"
    />

    <!--
      低频动作（终止 / 暂停 / 继续）收进下拉。

      ⚠️ **下拉项不能用 `IconActionButton`**：它的 tooltip 需要包一层 `<span>`，
      而 `el-dropdown-item` 只接受文本/元素内容，塞组件进去会丢掉点击。

      ⚠️ **触发器也不能用 `IconActionButton`**：EP 的 `el-dropdown` 用 `ElOnlyChild`
      取插槽里的第一个节点，并给它挂 forwardRef **运行时指令**来拿 DOM 元素。
      `IconActionButton` 的根是 `el-tooltip`（多根 fragment），指令挂上去不生效
      （并告警「Runtime directive used on component with non-element root node」），
      下拉因此**永远打不开**。`el-button` 的根是单一 `<button>`，没问题。
    -->
    <el-dropdown v-if="overflowActions.length > 0" trigger="click">
      <el-button
        plain
        size="small"
        :aria-label="t('container.action.more')"
        :disabled="busy"
      >
        <template #icon>
          <span class="i-carbon-overflow-menu-horizontal" />
        </template>
        {{ t('container.action.more') }}
      </el-button>
      <template #dropdown>
        <el-dropdown-menu>
          <el-dropdown-item
            v-for="action in overflowActions"
            :key="action.key"
            :disabled="isDisabled(action.key)"
            @click="emit('run', action.key)"
          >
            <span :class="action.icon" aria-hidden="true" />
            <span class="ml-2">{{ t(action.labelKey) }}</span>
          </el-dropdown-item>
        </el-dropdown-menu>
      </template>
    </el-dropdown>
  </div>
</template>
