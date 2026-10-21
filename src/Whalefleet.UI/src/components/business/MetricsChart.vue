<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { storeToRefs } from 'pinia'
import { useThemeStore } from '@/stores/theme'
import type { ContainerStatsSample } from '@/types/api'

/**
 * 容器多指标叠加图（0.6.0）。
 *
 * ## 为什么用 ECharts 而不是自己画
 * 「多指标叠加 + 时间窗切换 + tooltip」这三件事手写要几百行，而 ECharts 按需引入
 * 只带折线 + 网格 + tooltip 三个模块。**按需**很关键：全量包会把首屏拉大好几倍，
 * 而这张图只在容器详情页出现——它由调用方用 `defineAsyncComponent` 懒加载。
 *
 * ## 为什么 CPU / 内存用左轴、网络用右轴
 * 三者量纲不同（百分比 vs 字节/秒）。共用一个轴会让网络曲线把 CPU 压成一条直线，
 * 看起来像「CPU 一直是 0」。
 *
 * ## ⚠️ 主题色必须运行时读取 CSS 变量
 * canvas 不继承 CSS 变量。硬编码颜色会让暗色模式下的网格与文字看不清，
 * 而主题切换时若不重新 setOption，图会停在旧配色上——因此这里 watch 主题。
 */
echarts.use([LineChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer])

const props = defineProps<{
  /** 采样序列（旧 → 新） */
  samples: ContainerStatsSample[]
}>()

const { t } = useI18n()
const theme = useThemeStore()
const { resolvedMode, color } = storeToRefs(theme)

const host = ref<HTMLDivElement | null>(null)
let chart: echarts.ECharts | null = null

/** 从 CSS 变量里取颜色（取不到时给一个安全的回退值）。 */
function cssVar(name: string, fallback: string): string {
  if (typeof window === 'undefined') return fallback

  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim()

  return value === '' ? fallback : value
}

/** 品牌色是「RGB 通道」形式（`64 158 255`），canvas 需要完整的 rgb()。 */
function brand(alpha = 1): string {
  return `rgb(${cssVar('--brand-500', '64 158 255')} / ${alpha})`
}

function buildOption() {
  const points = props.samples
  const time = points.map((sample) => sample.timestamp)

  const cpu = points.map((sample) => Number(sample.cpuPercent.toFixed(1)))
  const memory = points.map((sample) =>
    sample.memoryLimit > 0 ? Number(((sample.memoryUsage / sample.memoryLimit) * 100).toFixed(1)) : null
  )
  const rx = points.map((sample) => sample.networkRxBytesPerSecond)
  const tx = points.map((sample) => sample.networkTxBytesPerSecond)

  const text = cssVar('--content-muted', '#64748b')
  const line = cssVar('--line-subtle', '#e2e8f0')

  return {
    animation: false,
    grid: { left: 44, right: 56, top: 34, bottom: 28 },
    tooltip: { trigger: 'axis' as const },
    legend: {
      top: 0,
      textStyle: { color: text, fontSize: 11 },
      itemWidth: 10,
      itemHeight: 8
    },
    xAxis: {
      type: 'category' as const,
      data: time,
      axisLabel: {
        color: text,
        fontSize: 10,
        // 只显示时分秒：完整 ISO 时间会挤成一团
        formatter: (value: string) => value.slice(11, 19)
      },
      axisLine: { lineStyle: { color: line } }
    },
    yAxis: [
      {
        type: 'value' as const,
        min: 0,
        max: 100,
        axisLabel: { color: text, fontSize: 10, formatter: '{value}%' },
        splitLine: { lineStyle: { color: line } }
      },
      {
        type: 'value' as const,
        axisLabel: { color: text, fontSize: 10, formatter: (value: number) => `${value}` },
        splitLine: { show: false }
      }
    ],
    series: [
      {
        name: t('container.detail.cpu'),
        type: 'line' as const,
        smooth: true,
        showSymbol: false,
        data: cpu,
        lineStyle: { width: 2, color: brand(1) }
      },
      {
        name: t('container.detail.memory'),
        type: 'line' as const,
        smooth: true,
        showSymbol: false,
        data: memory,
        lineStyle: { width: 2, type: 'dashed' as const, color: brand(0.65) }
      },
      {
        name: `${t('container.detail.networkIo')} ↓`,
        type: 'line' as const,
        yAxisIndex: 1,
        smooth: true,
        showSymbol: false,
        data: rx,
        lineStyle: { width: 1, color: cssVar('--status-ok', '#16a34a') }
      },
      {
        name: `${t('container.detail.networkIo')} ↑`,
        type: 'line' as const,
        yAxisIndex: 1,
        smooth: true,
        showSymbol: false,
        data: tx,
        lineStyle: { width: 1, type: 'dotted' as const, color: cssVar('--status-warn', '#d97706') }
      }
    ]
  }
}

function render(): void {
  if (!host.value) return

  chart ??= echarts.init(host.value)
  chart.setOption(buildOption(), true)
}

function resize(): void {
  chart?.resize()
}

onMounted(() => {
  render()
  window.addEventListener('resize', resize)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', resize)
  chart?.dispose()
  chart = null
})

// 采样更新：只重设数据（不重建实例）
watch(() => props.samples, render)

// 主题切换：canvas 不继承 CSS 变量，必须重新取色重绘
watch([resolvedMode, color], render)
</script>

<template>
  <div ref="host" class="h-56 w-full" role="img" :aria-label="t('container.detail.metricsChart')" />
</template>
