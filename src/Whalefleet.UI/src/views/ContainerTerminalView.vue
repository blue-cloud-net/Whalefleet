<script setup lang="ts">
import { computed, defineAsyncComponent, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ContainerStatusBadge from '@/components/business/ContainerStatusBadge.vue'
import { inspectContainer } from '@/api/container'
import { ApiRequestError } from '@/api'
import { canPerform, actionDisabledReason } from '@/utils/container-actions'
import type { ContainerDetail } from '@/types/api'

/**
 * 容器终端页（0.10.1）。
 *
 * ## 为什么从详情页搬出来
 * 终端原本是详情页底部的一张卡片，问题不是「放不下」而是**语义不对**：
 * 它是与容器的一次**长时间双向会话**，而详情页是一份**快照**。
 * 混在一起的后果有三个：
 *  1. 详情页底部长出一个 22rem 的黑色面板，把「环境变量 / 标签」这些真正常看的
 *     内容推到更远的地方；
 *  2. 点「刷新」或状态变化重渲染时，终端会话处在同一张页面里，用户很难判断
 *     「断开」是容器状态变了还是页面被重绘了；
 *  3. 终端要**宽**（一屏能多放几列输出），详情页刻意是 `narrow` 的。
 * 拆成独立路由后，终端页可以放开宽度、把宿主高度交给视口，也就能被直接收藏 / 分享。
 *
 * ## 为什么还要拉一次 inspect
 * 「容器是不是在跑」必须由状态判定（`canPerform('terminal', …)`），
 * 而不是靠 URL 直接放行——已停止的容器 exec 必然失败。
 * 这次请求只用来取状态与标题里的容器名，不再渲染任何配置。
 *
 * ## xterm 仍然懒加载
 * `ContainerTerminal` 内部是 `await import('@xterm/...')`，
 * 这里再用 `defineAsyncComponent` 把**面板本身**也拆出去：
 * 打开终端页但没点「打开终端」的人不该下载组件代码。
 */
const ContainerTerminal = defineAsyncComponent(
  () => import('@/components/business/ContainerTerminal.vue')
)

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const hostId = computed(() => String(route.params.hostId ?? ''))
const containerId = computed(() => String(route.params.id ?? ''))

const detail = ref<ContainerDetail | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

/** 终端能否打开：容器必须处于运行中（暂停中同样不行，exec 会挂住）。 */
const terminalDisabled = computed(() =>
  detail.value ? !canPerform('terminal', detail.value.state.status) : true
)

const disabledReason = computed(() =>
  detail.value ? actionDisabledReason('terminal', detail.value.state.status) : null
)

/** 页头副标题：容器名 + 镜像，用来确认「没连错容器」。 */
const subtitle = computed(() =>
  detail.value ? `${detail.value.name} · ${detail.value.image}` : undefined
)

async function load(): Promise<void> {
  if (!hostId.value || !containerId.value) return

  loading.value = true
  error.value = null
  try {
    detail.value = await inspectContainer(hostId.value, containerId.value)
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
    detail.value = null
  } finally {
    loading.value = false
  }
}

function backToInspect(): void {
  void router.push({
    name: 'host-container-inspect',
    params: { hostId: hostId.value, id: containerId.value }
  })
}

watch(containerId, load, { immediate: true })
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-terminal" :title="t('container.terminal.title')" :subtitle="subtitle">
      <template #actions>
        <el-button @click="backToInspect">{{ t('container.detail.title') }}</el-button>
        <el-button :loading="loading" type="primary" @click="load">
          {{ t('container.action.refresh') }}
        </el-button>
      </template>

      <template v-if="detail" #badge>
        <ContainerStatusBadge :status="detail.state.status" class="ml-2 align-middle" />
      </template>
    </PageHeader>

    <div v-if="error" class="app-section">
      <EmptyState
        icon="i-carbon-warning-alt"
        :title="t('container.inspect.failed')"
        :description="error"
      >
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </EmptyState>
    </div>

    <SectionCard v-else :description="t('container.terminal.hint')">
      <!--
        `--terminal-height` 把 xterm 宿主的高度交给**页面**决定：
        组件自己的 scoped CSS 里默认 22rem（嵌在别处时的安全值），
        而独立页可以把「扣掉页头与卡片头之后剩下的视口」全给终端——
        终端越高，一次能看到的输出越多，这才是它单独成页的意义。
        ⚠️ 高度必须是**确定的像素值**（`calc` 也可以），
        百分比或 `flex-1` 会让 fit() 在首帧算出行列数为 0。
      -->
      <div class="app-terminal-page">
        <ContainerTerminal
          :host-id="hostId"
          :container-id="containerId"
          :disabled="terminalDisabled"
          :disabled-reason="disabledReason"
        />
      </div>
    </SectionCard>
  </div>
</template>

<style scoped>
.app-terminal-page {
  --terminal-height: clamp(24rem, calc(100vh - 22rem), 56rem);
}
</style>
