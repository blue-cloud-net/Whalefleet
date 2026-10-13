<script setup lang="ts">
import { computed } from 'vue'
import { MOCK_SCENARIO_LABELS, MOCK_SCENARIOS, resolveScenario, type MockScenario } from '@/mocks/scenarios'
import { MOCK_STORAGE_KEY, setMockRuntime } from '@/mocks/switch'

/**
 * 顶栏 MOCK 角标。
 *
 * **绝不静默 mock** —— 这是本方案最容易踩的坑：前端在打假数据，开发者却以为是
 * 真后端返回的，然后花一小时去查一个根本不存在的 bug。因此只要 mock 开着，
 * 顶栏就必须挂一个显眼且不可忽略的标记，并把当前场景写清楚。
 *
 * 切换场景后 `location.reload()`：mock 的 handler 是在启动时按场景组装的，
 * 改 URL query 后重载是最简单可靠的方式（不引入额外的响应式状态机）。
 */
const scenario = computed<MockScenario>(() =>
  resolveScenario(window.location.search, import.meta.env.VITE_MOCK_SCENARIO)
)

const label = computed(() => MOCK_SCENARIO_LABELS[scenario.value])

/** 关闭 mock：清除运行时开关并回到默认（无 query）入口。 */
function disable() {
  setMockRuntime(null)
  const url = new URL(window.location.href)
  url.searchParams.delete('mock')
  window.location.href = url.toString()
}

/** 切换场景：保留当前路径（不把用户踢回总览），只换 query。 */
function switchTo(next: MockScenario) {
  const url = new URL(window.location.href)
  url.searchParams.set('mock', next)
  window.location.href = url.toString()
}
</script>

<template>
  <el-dropdown trigger="click">
    <span
      class="app-chip shrink-0 cursor-pointer bg-amber-500/15 text-amber-600 dark:text-amber-400 ring-amber-500/40 font-semibold app-tabular"
      :title="label"
      role="status"
      :aria-label="`Mock 模式已开启：${label}`"
    >
      <span class="i-carbon-debug text-xs" aria-hidden="true" />
      MOCK · {{ scenario }}
    </span>

    <template #dropdown>
      <el-dropdown-menu class="min-w-64">
        <el-dropdown-item disabled>
          <span class="text-xs leading-relaxed">
            当前数据来自本地 mock，<b>不是真实后端</b>。开发完请关闭。
          </span>
        </el-dropdown-item>

        <el-dropdown-item
          v-for="s in MOCK_SCENARIOS"
          :key="s"
          divided
          :class="{ 'text-brand-600 dark:text-brand-400 font-medium': s === scenario }"
          @click="switchTo(s)"
        >
          <span class="flex items-center gap-2.5">
            <span class="text-sm">{{ s }}</span>
            <span
              v-if="s === scenario"
              class="i-carbon-checkmark text-sm ml-auto text-brand-500 shrink-0"
            />
          </span>
          <span class="block text-xs text-content-faint mt-0.5">{{ MOCK_SCENARIO_LABELS[s] }}</span>
        </el-dropdown-item>

        <el-dropdown-item divided @click="disable">
          <span class="flex items-center gap-2">
            <span class="i-carbon-power text-sm" />
            关闭 mock（清除 <code class="text-xs">{{ MOCK_STORAGE_KEY }}</code>）
          </span>
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>