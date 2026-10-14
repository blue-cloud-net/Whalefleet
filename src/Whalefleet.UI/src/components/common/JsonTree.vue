<script setup lang="ts">
import { computed, ref } from 'vue'
import CopyableText from '@/components/common/CopyableText.vue'

/**
 * JSON 树（0.0.14）。
 *
 * ## 解决的痛点
 * 容器 / 镜像的 inspect 返回几百行 JSON。此前只有两个选择：全量 `<pre>`（找不到字段）
 * 或什么都不展示（用户只能回终端 `docker inspect`）。本组件给出第三条路。
 *
 * ## 为什么默认只展开顶层
 * 几百行全展开会淹掉关键信息；全折叠则用户不知道里面有什么。
 * 顶层展开能立刻交代「这个对象的组成」，用户再按需下钻。
 *
 * ## 为什么叶子标量按类型着色
 * 数字 / 布尔 / null 混在一堆字符串里时，没有颜色几乎无法快速扫读。
 * 语义色沿用既有令牌（status-ok 绿 / warn 琥珀 / brand），不引入新色。
 *
 * ## 展开状态为什么放在每个节点实例里
 * 每个实例各管各的 `open`，不用全局 `Record<path, boolean>`：inspect 数据
 * 动辄几百个节点，一张路径表会让每次点击都触发一轮全量依赖追踪。
 * 「状态不跨节点共享」在这里恰好是优点——展开一个子对象不影响同名兄弟。
 */
const props = withDefaults(
  defineProps<{
    /** 任意可 JSON 化的值 */
    data: unknown
    /** 本节点的深度（根为 0），由父级递归传入 */
    depth?: number
    /** 展开到第几层；默认 1（只有顶层展开） */
    maxDepth?: number
  }>(),
  { depth: 0, maxDepth: 1 }
)

defineOptions({ name: 'JsonTree' })

type NodeKind = 'object' | 'array' | 'string' | 'number' | 'boolean' | 'null'

function kindOf(value: unknown): NodeKind {
  if (value === null) return 'null'
  if (Array.isArray(value)) return 'array'

  switch (typeof value) {
    case 'object':
      return 'object'
    case 'number':
      return 'number'
    case 'boolean':
      return 'boolean'
    default:
      return 'string'
  }
}

function isBranch(value: unknown): boolean {
  const kind = kindOf(value)
  return kind === 'object' || kind === 'array'
}

/** 对象与数组都按 `key: value` 逐条渲染——数组的 key 用下标，读起来与 `jq` 一致。 */
const entries = computed<Array<[string, unknown]>>(() =>
  isBranch(props.data) ? Object.entries(props.data as Record<string, unknown>) : []
)

/** 深度 < maxDepth 时默认展开；更深的层级一律收起。 */
const open = ref(props.depth < props.maxDepth)

function branchSummary(value: unknown): string {
  if (Array.isArray(value)) return `[${value.length}]`
  return `{${Object.keys(value as Record<string, unknown>).length}}`
}

/**
 * 叶子标量的着色类。
 *
 * key 与 value 都要着色：key 用弱化的 `content-muted`（够读不抢），
 * value 才用语义色（这是扫读时真正要找的信息）。
 */
const VALUE_CLASS: Record<NodeKind, string> = {
  string: 'text-status-ok',
  number: 'text-status-warn',
  boolean: 'text-brand-600 dark:text-brand-400',
  null: 'text-content-faint italic',
  object: '',
  array: ''
}

/** 叶子标量的展示文本。字符串原样（不加引号），其余用 JSON 表达。 */
function displayScalar(value: unknown): string {
  return typeof value === 'string' ? value : (JSON.stringify(value) ?? '—')
}

/**
 * 缩进宽度。
 *
 * 用 padding-left 而非嵌套 DOM：嵌套每层多一个容器，inspect 页的节点数
 * 随深度线性增长，折叠后仍会残留这些空壳。扁平渲染 + 缩进是更划算的选择。
 */
const INDENT_REM = 14
</script>

<template>
  <!-- 叶子：标量直接着色，并配一个悬停显现的复制按钮 -->
  <div v-if="!isBranch(data)" class="flex items-baseline gap-1 min-w-0">
    <span class="app-truncate" :class="VALUE_CLASS[kindOf(data)]">{{ displayScalar(data) }}</span>
    <CopyableText
      :value="displayScalar(data)"
      class="shrink-0 opacity-0 hover:opacity-100 focus-within:opacity-100 transition-opacity"
    />
  </div>

  <!--
    复合节点：折叠开关 + `{n}` / `[n]` 摘要。
    根节点与深层节点用**同一个**模板——深层节点被父级放在
    「`key:`」之后同一行内，点击箭头即展开，形态与常见 JSON 查看器一致。
  -->
  <div v-else class="min-w-0">
    <button
      type="button"
      class="app-json-row flex items-center gap-1.5 w-full text-left rounded hover:bg-surface-hover"
      @click="open = !open"
    >
      <span
        class="inline-block w-3 shrink-0 text-content-faint transition-transform"
        :class="open ? 'i-carbon-chevron-down' : 'i-carbon-chevron-right'"
      />
      <span class="text-content-faint shrink-0">{{ branchSummary(data) }}</span>
    </button>

    <template v-if="open">
      <div
        v-for="[key, child] in entries"
        :key="key"
        class="app-json-row flex items-start gap-1.5 rounded hover:bg-surface-hover min-w-0"
        :style="{ paddingLeft: `${(depth + 1) * INDENT_REM}rem` }"
      >
        <span class="text-content-muted shrink-0">{{ key }}:</span>

        <!--
          复合子节点：递归渲染。此处**不能**再加缩进 padding——
          子组件内部已按 depth+1 缩进，重复加会让层级越深偏得越快。
        -->
        <JsonTree
          v-if="isBranch(child)"
          :data="child"
          :depth="depth + 1"
          :max-depth="maxDepth"
          class="min-w-0 flex-1"
        />

        <template v-else>
          <span class="min-w-0" :class="VALUE_CLASS[kindOf(child)]">{{ displayScalar(child) }}</span>
          <CopyableText
            :value="displayScalar(child)"
            class="shrink-0 opacity-0 hover:opacity-100 focus-within:opacity-100 transition-opacity"
          />
        </template>
      </div>
    </template>
  </div>
</template>

<style scoped>
.app-json-row {
  /* 行高固定，避免展开 / 折叠时整块内容跳动 */
  line-height: 1.6;
  padding-right: 0.5rem;
}
</style>