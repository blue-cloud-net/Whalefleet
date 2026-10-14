<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { buildCommands, searchCommands, type SearchableCommand } from '@/utils/commands'

/**
 * 命令面板（0.0.14）。
 *
 * ## 为什么用 el-dialog 而不是自绘浮层
 * 自绘要自己处理焦点陷阱、滚动锁定、`aria-modal`、ESC 关闭、体积过渡——
 * 这些 el-dialog 都做好了，且已在整个项目里统一使用。命令面板的价值在**检索**，
 * 不在浮层动画。
 *
 * ## 键盘行为
 * - `↑` / `↓` 移动选中项，**循环**（到底回到顶）：面板通常只有 5–8 项，
 *   循环比「到底停住」少一次按键
 * - `Enter` 执行；`Esc` 关闭（由 el-dialog 负责）
 * - 选中项随查询变化自动重置到第一项：保留旧下标会让用户 Enter 到一个不相干的条目
 *
 * ## 为什么不加输入去抖
 * 候选集只有十几条，一次 Fuse 检索在亚毫秒级；去抖反而让按键到出结果之间
 * 有可感知的延迟。
 */
const visible = defineModel<boolean>({ required: true })

const props = defineProps<{
  /** 当前是否有可用的主机上下文 */
  hasHostContext: () => boolean
  /** 动作回调 */
  actions: {
    createContainer?: () => void
    pullImage?: () => void
    showShortcuts?: () => void
    toggleSidebar?: () => void
  }
}>()

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const query = ref('')
const selected = ref(0)
const results = ref<SearchableCommand[]>([])
/** fuse.js 动态 import 是否已完成；未完成时显示空态文案会有误导，故用骨架占位。 */
const fuseReady = ref(false)

const searchInput = ref<HTMLInputElement | null>(null)

/** 当前 URL 上的 hostId；全局作用域时为空串。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

/**
 * 候选集。
 *
 * 两个过滤都在**构造期**完成，而不是等用户点了才让路由守卫弹回总览：
 * 1. 管理员限定条目（用户 / 主机）对非管理员直接不出现
 * 2. 主机作用域条目在没有 hostId 时整组略去——它们的路由需要 `hostId` 参数，
 *    缺参数会让 vue-router 抛 "Missing required param"（足以白屏）。
 */
const allItems = computed<SearchableCommand[]>(() =>
  buildCommands(props.actions)
    .filter((item) => !item.requiresAdmin || auth.isAdmin)
    .filter((item) => !item.requiresHost || hasHost.value)
    .map((item) => {
      const displayLabel = t(item.label)
      return {
        ...item,
        displayLabel,
        haystack: [displayLabel, ...(item.keywords ?? [])].join(' ')
      }
    })
)

/** 是否存在可用的主机上下文。 */
const hasHost = computed(() => props.hasHostContext())

async function refresh(queryText: string): Promise<void> {
  results.value = await searchCommands(allItems.value, queryText)
  fuseReady.value = true
  selected.value = 0
}

watch(query, (value) => void refresh(value))

/** 每次打开都重置查询与选中项：上次搜的关键词留在框里会让用户以为搜不到东西。 */
watch(visible, async (open) => {
  if (!open) return

  query.value = ''
  await refresh('')
  await nextTick()
  searchInput.value?.focus()
})

const current = computed(() => results.value[selected.value])

function move(delta: number): void {
  const total = results.value.length
  if (total === 0) return

  // 循环：面板条目少，少一次按键比「到底停住」更值钱
  selected.value = (selected.value + delta + total) % total
}

function execute(item: SearchableCommand | undefined): void {
  if (!item) return

  visible.value = false

  // 动作优先：它可能自己打开对话框（创建容器），此时不能同时跳转
  if (item.action) {
    item.action()
    return
  }

  // routeName 由 buildCommands 保证存在；缺失只可能是数据写错，静默忽略
  if (!item.routeName) return

  void router.push(
    item.requiresHost ? { name: item.routeName, params: { hostId: hostId.value } } : { name: item.routeName }
  )
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    move(1)
    return
  }

  if (event.key === 'ArrowUp') {
    event.preventDefault()
    move(-1)
    return
  }

  if (event.key === 'Enter') {
    event.preventDefault()
    execute(current.value)
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    width="min(640px, 94vw)"
    top="12vh"
    append-to-body
    :show-close="false"
    class="app-command-palette"
  >
    <!-- 输入区：焦点框即输入框本身，不额外画一层边框 -->
    <div class="flex items-center gap-2.5 px-4 border-b border-line-subtle">
      <span class="i-carbon-search text-lg text-content-faint shrink-0" />
      <input
        ref="searchInput"
        v-model="query"
        type="text"
        :placeholder="t('command.placeholder')"
        class="flex-1 min-w-0 bg-transparent py-3.5 text-sm outline-none placeholder:text-content-faint"
        @keydown="onKeydown"
      />
      <kbd class="app-kbd shrink-0">Esc</kbd>
    </div>

    <!-- 结果区：给 max-height 而非固定 height，条目少时不留大片空白 -->
    <div class="max-h-[52vh] overflow-y-auto p-2">
      <ul v-if="results.length > 0" class="flex flex-col gap-0.5">
        <li v-for="(item, index) in results" :key="item.id">
          <button
            type="button"
            class="w-full flex items-center gap-3 px-2.5 py-2 rounded-md text-left transition-colors"
            :class="index === selected ? 'bg-brand-50 dark:bg-brand-500/15' : 'hover:bg-surface-hover'"
            @mouseenter="selected = index"
            @click="execute(item)"
          >
            <span
              class="text-base shrink-0"
              :class="index === selected ? 'text-brand-600 dark:text-brand-400' : 'text-content-faint'"
            >
              <span :class="item.icon" />
            </span>
            <span
              class="flex-1 min-w-0 truncate text-sm"
              :class="index === selected ? 'text-content-strong font-medium' : 'text-content-body'"
            >
              {{ item.displayLabel }}
            </span>
            <kbd v-if="item.shortcutHint" class="app-kbd shrink-0">{{ item.shortcutHint }}</kbd>
          </button>
        </li>
      </ul>

      <div v-else-if="!fuseReady" class="py-10 text-center">
        <el-skeleton :rows="3" animated />
      </div>

      <p v-else class="py-10 text-center text-sm text-content-muted">
        {{ t('command.noResults') }}
      </p>
    </div>

    <template #footer>
      <div class="flex items-center gap-4 px-4 py-2 border-t border-line-subtle text-xs text-content-faint">
        <span class="flex items-center gap-1.5">
          <kbd class="app-kbd">↑</kbd><kbd class="app-kbd">↓</kbd>{{ t('command.hintNavigate') }}
        </span>
        <span class="flex items-center gap-1.5">
          <kbd class="app-kbd">↵</kbd>{{ t('command.hintSelect') }}
        </span>
      </div>
    </template>
  </el-dialog>
</template>

<style scoped>
/* 键帽样式：独立成类而不是在 5 处重复同一组 UnoCSS 原子类 */
.app-kbd {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 1.5rem;
  padding: 0 0.3rem;
  font-family: 'JetBrains Mono', ui-monospace, monospace;
  font-size: 0.7rem;
  line-height: 1.5;
  border-radius: var(--radius-sm);
  border: 1px solid var(--border-subtle);
  background: var(--surface-sunken);
  color: var(--text-muted);
}
</style>