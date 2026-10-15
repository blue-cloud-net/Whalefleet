<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import { getContainerLogs } from '@/api/container'
import { ApiRequestError } from '@/api'

/**
 * 容器日志页（0.0.11）。
 *
 * ## 为什么是「一次性读取」而不是跟随
 * 后端刻意没暴露 `follow`（见 `getContainerLogs` 的注释）：一次性 HTTP 响应跟着日志流
 * 只会把连接挂到容器退出。实时流属 0.4.0 的 SignalR 推送。
 *
 * 因此本页的交互重心是**参数 + 手动重载**，而不是自动滚动：
 * 一个「自动滚动」的开关在静态文本上没有意义——页面不会自己长出新行。
 * 它只在用户手动重载后把视图拉到底部，因此保留为「加载后跳到底部」的行为。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const hostId = computed(() => String(route.params.hostId ?? ''))
const containerId = computed(() => String(route.params.id ?? ''))

const tail = ref<number | ''>(200)
const timestamps = ref(false)
const includeStderr = ref(true)

const content = ref('')
const loading = ref(false)
const error = ref<string | null>(null)
/** 是否加载成功但内容为空——与「加载失败」是两件事，界面要分开说。 */
const loaded = ref(false)

/** 尾行列的候选值；「全部」用空串表示（省略 tail，后端返回全部）。 */
const TAIL_OPTIONS = [100, 200, 500, 1000, 5000]

async function load(): Promise<void> {
  if (!hostId.value || !containerId.value) return

  loading.value = true
  error.value = null
  try {
    const result = await getContainerLogs(hostId.value, containerId.value, {
      // 尾数是 number | ''：空串要转成 undefined，否则会把 0 发给后端（= 不要日志）
      ...(tail.value === '' ? {} : { tail: Number(tail.value) }),
      timestamps: timestamps.value,
      includeStderr: includeStderr.value
    })

    content.value = result.content
    loaded.value = true
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
    content.value = ''
    loaded.value = false
  } finally {
    loading.value = false
  }
}

function backToList(): void {
  void router.push({ name: 'host-containers', params: { hostId: hostId.value } })
}

watch([tail, timestamps, includeStderr], () => {
  // 参数一改就自动重载：用户调「行数」时的预期就是「立刻看到新的那一段」。
  void load()
})

watch(
  containerId,
  () => {
    loaded.value = false
    void load()
  },
  { immediate: true }
)

onMounted(load)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-container-software"
      :title="t('container.logs.title')"
      :subtitle="t('container.logs.notFollow')"
      narrow
    >
      <template #actions>
        <el-button @click="backToList">{{ t('container.backToList') }}</el-button>
        <el-button :loading="loading" type="primary" @click="load">
          {{ t('container.action.refresh') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <div class="flex items-center gap-4">
          <!-- `whitespace-nowrap` 不能省：中文标签在窄容器里会被拆成「显示 / 行数」两行，
               而这一行的高度由下拉框决定，于是标签看起来像是错位了。 -->
          <label class="flex items-center gap-1.5 text-xs whitespace-nowrap text-content-muted">
            <span>{{ t('container.logs.tail') }}</span>
            <el-select v-model="tail" size="small" class="w-28">
              <el-option :label="t('container.logs.tailAll')" value="" />
              <el-option v-for="option in TAIL_OPTIONS" :key="option" :value="option" :label="String(option)" />
            </el-select>
          </label>

          <el-checkbox v-model="timestamps" size="small" class="whitespace-nowrap">
            {{ t('container.logs.timestamps') }}
          </el-checkbox>
          <el-checkbox v-model="includeStderr" size="small" class="whitespace-nowrap">
            {{ t('container.logs.includeStderr') }}
          </el-checkbox>
        </div>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <!-- 日志用 <pre> 而不是普通 div：多行堆栈与制表符必须原样保留，
           且用户要能选中复制（排查问题时这是刚需）。 -->
      <pre
        v-else-if="loaded && content.length > 0"
        class="app-log"
      >{{ content }}</pre>

      <EmptyState
        v-else-if="loaded"
        class="p-6"
        icon="i-carbon-document"
        :title="t('container.logs.empty')"
        :description="t('container.logs.notFollow')"
      />
    </SectionCard>
  </div>
</template>