<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StatusBadge from '@/components/common/StatusBadge.vue'
import StackApplyDialog from '@/components/business/StackApplyDialog.vue'
import StackWebhookCard from '@/components/business/StackWebhookCard.vue'
import { useStackTask } from '@/composables/useStackTask'
import { inspectStack, removeStack, stopStack, updateStack } from '@/api/stack'
import { ApiRequestError } from '@/api'
import { stackStatusKey, stackTone, describeStackPorts } from '@/utils/stack-display'
import type { StackDetail } from '@/types/api'

/**
 * 编排 Stack 详情页（0.10.0）。
 *
 * ## 主体是**服务**而不是容器
 * 用户问的是「哪个服务没起来」，而一个项目可能有十几个服务——
 * 把它们摊平成一列容器，用户得自己在心里做「容器 → 服务」的归组。
 * 这里直接按 YAML 里声明的服务列，容器状态挂在服务下面。
 *
 * ## 为什么「更新」只对受管项目开放
 * 外部 compose 项目（用户自己在命令行 `docker compose up` 的）没有 YAML，
 * 更新必然失败（后端报 `1903`）。因此入口直接不给，而不是等点了才报错。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const detail = ref<StackDetail | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

/** 应用对话框。 */
const applyVisible = ref(false)

const { task, busy: taskBusy, percent, step, run } = useStackTask()

/** URL 上的 hostId。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

/** URL 上的 Stack 名称。 */
const stackName = computed(() => String(route.params.name ?? ''))

/** 是否受管（本平台部署的）。 */
const managed = computed(() => detail.value?.summary.managed === true)

async function load(): Promise<void> {
  if (!hostId.value || !stackName.value) return

  loading.value = true
  try {
    detail.value = await inspectStack(hostId.value, stackName.value)
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : t('stack.loadFailed')
  } finally {
    loading.value = false
  }
}

/**
 * 对话框提交（更新）。
 *
 * @param payload 提交内容。
 */
async function onApply(payload: {
  name: string
  composeContent: string
  pullAlways: boolean
  pruneOrphans: boolean
}): Promise<void> {
  const snapshot = await run(
    hostId.value,
    (taskId) =>
      updateStack(hostId.value, {
        name: payload.name,
        composeContent: payload.composeContent,
        taskId,
        pullAlways: payload.pullAlways,
        pruneOrphans: payload.pruneOrphans
      }),
    load
  )

  if (!snapshot) {
    ElMessage.error(t('stack.task.failed'))
    return
  }

  if (snapshot.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('stack.task.failed'))
    return
  }

  ElMessage.success(t('stack.task.succeeded'))
}

/** 停止编排。 */
async function stopStackByName(): Promise<void> {
  const snapshot = await run(
    hostId.value,
    (taskId) => stopStack(hostId.value, { name: stackName.value, taskId }),
    load
  )

  if (snapshot?.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('stack.task.failed'))
    return
  }

  if (snapshot) {
    ElMessage.success(t('stack.task.succeeded'))
  }
}

/** 删除编排。 */
async function removeStackByName(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('stack.remove.confirm', { name: stackName.value }),
      t('stack.remove.title'),
      {
        type: 'warning',
        confirmButtonText: t('common.confirm'),
        cancelButtonText: t('common.cancel')
      }
    )
  } catch {
    return
  }

  const snapshot = await run(
    hostId.value,
    (taskId) =>
      removeStack(hostId.value, { name: stackName.value, taskId, removeVolumes: false }),
    load
  )

  if (snapshot?.status === 'failed') {
    ElMessage.error(snapshot.errorMessage || t('stack.task.failed'))
    return
  }

  if (snapshot) {
    ElMessage.success(t('stack.task.succeeded'))
    // 删掉了就回列表：留在详情页只会看到一个空壳
    void router.push({ name: 'host-stacks', params: { hostId: hostId.value } })
  }
}

/** 回列表。 */
function backToList(): void {
  void router.push({ name: 'host-stacks', params: { hostId: hostId.value } })
}

watch(
  () => [route.params.hostId, route.params.name],
  () => {
    void load()
  },
  { immediate: true }
)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-layers"
      :title="stackName"
      :title-tip="t('page.stacks.subtitle')"
      narrow
    >
      <template #actions>
        <el-button @click="backToList">{{ t('common.back') }}</el-button>
        <el-button
          v-if="managed"
          type="primary"
          @click="applyVisible = true"
        >
          {{ t('stack.action.update') }}
        </el-button>
      </template>
    </PageHeader>

    <div v-if="taskBusy" class="mb-4">
      <el-alert type="info" :closable="false" show-icon>
        <template #title>{{ t('stack.task.running') }}</template>
        <div class="mt-1 flex items-center gap-3">
          <el-progress
            :percentage="percent ?? 0"
            :status="task?.status === 'failed' ? 'exception' : undefined"
            class="flex-1"
          />
          <span class="text-xs text-content-muted">{{ step ?? '' }}</span>
        </div>
      </el-alert>
    </div>

    <div v-if="error" class="p-6 flex flex-col items-center gap-3">
      <p class="text-sm text-content-muted">{{ error }}</p>
      <el-button @click="load">{{ t('common.retry') }}</el-button>
    </div>

    <template v-else-if="detail">
      <div class="flex flex-col gap-4">
        <!-- 概览 -->
        <SectionCard :title="t('stack.section.overview')">
          <div class="grid grid-cols-2 gap-4 sm:grid-cols-4">
            <div>
              <p class="text-xs text-content-muted">{{ t('stack.column.status') }}</p>
              <StatusBadge :tone="stackTone(detail.summary.status)" class="mt-1">
                {{ t(stackStatusKey(detail.summary.status)) }}
              </StatusBadge>
            </div>
            <div>
              <p class="text-xs text-content-muted">{{ t('stack.column.services') }}</p>
              <p class="mt-1 text-sm text-content-strong">{{ detail.summary.serviceCount }}</p>
            </div>
            <div>
              <p class="text-xs text-content-muted">{{ t('stack.column.containers') }}</p>
              <p class="mt-1 text-sm text-content-strong">
                {{ detail.summary.runningCount }} / {{ detail.summary.containerCount }}
              </p>
            </div>
            <div>
              <p class="text-xs text-content-muted">{{ t('stack.column.resources') }}</p>
              <p class="mt-1 text-sm text-content-strong">
                {{
                  t('stack.resources', {
                    networks: detail.summary.networkCount,
                    volumes: detail.summary.volumeCount
                  })
                }}
              </p>
            </div>
          </div>

          <!-- 解析期的提示必须显眼：它是「有些配置没生效」的唯一解释 -->
          <el-alert
            v-if="detail.warnings.length > 0"
            class="mt-4"
            type="warning"
            :closable="false"
            show-icon
          >
            <template #title>{{ t('stack.warnings.title') }}</template>
            <ul class="mt-1 list-disc pl-4 text-xs">
              <li v-for="(warning, index) in detail.warnings" :key="index">{{ warning }}</li>
            </ul>
          </el-alert>

          <div class="mt-4 flex items-center gap-2">
            <el-button :disabled="taskBusy" @click="stopStackByName">
              {{ t('stack.action.stop') }}
            </el-button>
            <el-button
              type="danger"
              plain
              :disabled="taskBusy"
              @click="removeStackByName"
            >
              {{ t('stack.action.remove') }}
            </el-button>
          </div>
        </SectionCard>

        <!-- 服务列表 -->
        <SectionCard :title="t('stack.section.services')">
          <EmptyState
            v-if="detail.services.length === 0"
            icon="i-carbon-cube"
            :title="t('stack.services.empty')"
            :description="t('stack.services.emptyHint')"
          />

          <el-table v-else :data="detail.services" row-key="name">
            <el-table-column :label="t('stack.column.service')" min-width="140">
              <template #default="{ row }">
                <span class="text-sm font-medium text-content-strong">{{ row.name }}</span>
              </template>
            </el-table-column>

            <el-table-column :label="t('stack.column.image')" min-width="200">
              <template #default="{ row }">
                <span class="font-mono text-xs text-content">{{ row.image }}</span>
              </template>
            </el-table-column>

            <el-table-column :label="t('stack.column.state')" width="120">
              <template #default="{ row }">
                <span class="text-sm text-content">{{ row.state ?? '—' }}</span>
              </template>
            </el-table-column>

            <el-table-column :label="t('stack.column.health')" width="120">
              <template #default="{ row }">
                <span class="text-sm text-content">{{ row.health ?? '—' }}</span>
              </template>
            </el-table-column>

            <el-table-column :label="t('stack.column.ports')" min-width="180">
              <template #default="{ row }">
                <span class="font-mono text-xs text-content-muted">
                  {{ describeStackPorts(row.ports) || '—' }}
                </span>
              </template>
            </el-table-column>

            <el-table-column :label="t('stack.column.restartPolicy')" width="140">
              <template #default="{ row }">
                <span class="text-xs text-content-muted">{{ row.restartPolicy ?? '—' }}</span>
              </template>
            </el-table-column>
          </el-table>
        </SectionCard>

        <!-- Webhook -->
        <SectionCard :title="t('stack.section.webhook')">
          <StackWebhookCard
            :host-id="hostId"
            :stack="detail.summary"
            @changed="load"
          />
        </SectionCard>

        <!-- YAML 原文 -->
        <SectionCard :title="t('stack.section.compose')">
          <template v-if="detail.composeContent">
            <pre
              class="max-h-[32rem] overflow-auto rounded bg-surface-hover p-3 font-mono text-xs text-content"
            >{{ detail.composeContent }}</pre>
          </template>
          <EmptyState
            v-else
            icon="i-carbon-document"
            :title="t('stack.compose.unavailable')"
            :description="t('stack.compose.unavailableHint')"
          />
        </SectionCard>
      </div>
    </template>

    <StackApplyDialog
      v-model:visible="applyVisible"
      mode="update"
      :stack="detail?.summary ?? null"
      :compose-content="detail?.composeContent ?? null"
      @submit="onApply"
    />
  </div>
</template>
