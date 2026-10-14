<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ContainerStatusBadge from '@/components/business/ContainerStatusBadge.vue'
import { inspectContainer } from '@/api/container'
import { ApiRequestError } from '@/api'
import { formatBytes } from '@/utils/container-status'
import { formatDateTime } from '@/utils/datetime'
import type { ByteUnits } from '@/utils/container-status'
import type { ContainerDetail } from '@/types/api'

/**
 * 容器详情页（0.0.11）。
 *
 * ## 为什么是分组卡片而不是一个 JSON 树
 * `docs/features.md` 早期写的是「Inspect 完整配置信息」，实现时改成
 * **基本信息 + 四组结构化卡片**，理由是：JSON 树要求用户自己找字段，
 * 而「这个容器占多少内存、挂在哪个卷上、有没有暴露端口」这些问题
 * 答在结构化视图里只需一眼。为此牺牲的是「一个字段不落」——
 * 未展示的字段不在后端契约里（后端已经是精选投影），所以不存在遗漏。
 *
 * ## 为什么 env 单独警示
 * 详情里的 `config.env` 常含数据库连接串、API Token。后端已在类型注释里写明这一点，
 * 前端再警示一次：这是唯一一个「展示它可能造成泄漏」的区域。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const hostId = computed(() => String(route.params.hostId ?? ''))
const containerId = computed(() => String(route.params.id ?? ''))

const detail = ref<ContainerDetail | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

const byteUnits = computed<ByteUnits>(() => ({
  b: t('unit.byte'),
  kib: t('unit.kib'),
  mib: t('unit.mib'),
  gib: t('unit.gib')
}))

/** 「字段为空」与「值为空串」要区分：前者显示占位符，后者可能是用户刻意设的空值。 */
function orDash(value: string | null | undefined): string {
  return value && value.length > 0 ? value : '—'
}

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

function backToList(): void {
  void router.push({ name: 'host-containers', params: { hostId: hostId.value } })
}

/**
 * 重启策略的线上写法 → i18n key。
 *
 * 必须显式映射，**不能**用模板字符串拼：Docker 给的是 `unless-stopped` / `on-failure`，
 * 而 i18n key 用的是 `unlessStopped` / `onFailure`。拼出来的 key 不存在，
 * 界面上会直接显示 `container.restartPolicy.unless-stopped` 这种原始 key——
 * 难看，且只有真看到界面才会发现（vue-i18n 找不到 key 时是警告不报错）。
 */
const RESTART_POLICY_KEYS: Record<string, string> = {
  no: 'container.restartPolicy.no',
  always: 'container.restartPolicy.always',
  'on-failure': 'container.restartPolicy.onFailure',
  'unless-stopped': 'container.restartPolicy.unlessStopped'
}

function restartPolicyLabel(policy: string): string {
  const key = RESTART_POLICY_KEYS[policy]

  // 未登记的写法（将来 Docker 新增）直接显示原文，而不是一个 key 路径。
  return key ? t(key) : policy
}

function openLogs(): void {
  void router.push({
    name: 'host-container-logs',
    params: { hostId: hostId.value, id: containerId.value }
  })
}

/**
 * NanoCpus 是「十亿分之一核」，展示时必须换算成核数——直接显示 1500000000 没人看得懂。
 *
 * 小于 1 核时保留两位小数（0.25 核这种配额很常见）；大于等于 1 核时用整数 + 一位小数
 * （8 核不必写成「8.00」）。
 */
function cpuQuota(nanoCpus: number | null): string {
  if (nanoCpus == null || nanoCpus === 0) return t('container.detail.unlimited')

  const cores = nanoCpus / 1_000_000_000

  return `${cores >= 1 ? cores.toFixed(1) : cores.toFixed(2)} ${t('container.detail.cores')}`
}

watch(containerId, load, { immediate: true })
</script>

<template>
  <div v-loading="loading">
    <PageHeader
      icon="i-carbon-container-software"
      :title="t('container.detail.title')"
      :subtitle="detail ? `${detail.name} · ${detail.image}` : undefined"
      narrow
    >
      <template #actions>
        <el-button @click="backToList">{{ t('container.backToList') }}</el-button>
        <el-button @click="openLogs">{{ t('container.action.logs') }}</el-button>
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

    <template v-else-if="detail">
      <div class="flex flex-col gap-4">
        <SectionCard :title="t('container.detail.basic')">
          <dl class="app-kv">
            <div><dt>{{ t('container.id') }}</dt><dd class="font-mono text-xs">{{ detail.id }}</dd></div>
            <div><dt>{{ t('container.col.created') }}</dt><dd>{{ formatDateTime(detail.created) }}</dd></div>
            <div>
              <dt>{{ t('container.detail.startedAt') }}</dt>
              <dd>{{ formatDateTime(detail.state.startedAt) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.detail.finishedAt') }}</dt>
              <dd>{{ formatDateTime(detail.state.finishedAt) }}</dd>
            </div>
            <!-- ⚠️ 运行中时后端刻意把 exitCode 抹成 null（Docker 给的 0 会被读成「已正常退出」），
                 因此这里只在已停止时展示，不能给个 0 糊弄过去。 -->
            <div v-if="!detail.state.running">
              <dt>{{ t('container.detail.exitCode') }}</dt>
              <dd :class="{ 'text-status-down': detail.state.exitCode !== 0 }">
                {{ detail.state.exitCode ?? '—' }}
              </dd>
            </div>
            <div v-if="detail.state.healthStatus">
              <dt>{{ t('container.detail.health') }}</dt>
              <dd>{{ detail.state.healthStatus }}</dd>
            </div>
            <div v-if="detail.state.oomKilled">
              <dt>{{ t('container.detail.oomKilled') }}</dt>
              <dd class="text-status-down">{{ t('container.detail.yes') }}</dd>
            </div>
          </dl>
        </SectionCard>

        <SectionCard :title="t('container.detail.hostConfig')">
          <dl class="app-kv">
            <div>
              <dt>{{ t('container.create.restartPolicy') }}</dt>
              <dd>{{ restartPolicyLabel(detail.hostConfig.restartPolicy) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.autoRemove') }}</dt>
              <dd>{{ detail.hostConfig.autoRemove ? t('container.detail.yes') : t('container.detail.no') }}</dd>
            </div>
            <div><dt>{{ t('container.create.network') }}</dt><dd>{{ orDash(detail.hostConfig.networkMode) }}</dd></div>
            <div>
              <dt>{{ t('container.detail.memoryLimit') }}</dt>
              <dd>{{ formatBytes(detail.hostConfig.memoryLimitBytes, byteUnits) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.detail.nanoCpus') }}</dt>
              <dd>{{ cpuQuota(detail.hostConfig.nanoCpus) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.detail.privileged') }}</dt>
              <dd>{{ detail.hostConfig.privileged ? t('container.detail.yes') : t('container.detail.no') }}</dd>
            </div>
          </dl>
        </SectionCard>

        <SectionCard :title="t('container.detail.config')">
          <dl class="app-kv">
            <div>
              <dt>{{ t('container.create.cmd') }}</dt>
              <dd class="font-mono text-xs">{{ orDash(detail.config.cmd.join(' ')) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.entrypoint') }}</dt>
              <dd class="font-mono text-xs">{{ orDash(detail.config.entrypoint.join(' ')) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.workingDir') }}</dt>
              <dd class="font-mono text-xs">{{ orDash(detail.config.workingDir) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.hostname') }}</dt>
              <dd class="font-mono text-xs">{{ orDash(detail.config.hostname) }}</dd>
            </div>
            <div>
              <dt>{{ t('container.create.tty') }}</dt>
              <dd>{{ detail.config.tty ? t('container.detail.yes') : t('container.detail.no') }}</dd>
            </div>
          </dl>
        </SectionCard>

        <SectionCard :title="t('container.detail.mounts')">
          <div v-if="detail.mounts.length > 0" class="app-table-wrap">
            <table class="app-table">
              <thead>
                <tr>
                  <th>{{ t('container.detail.mountSource') }}</th>
                  <th>{{ t('container.detail.mountTarget') }}</th>
                  <th class="text-right">{{ t('container.detail.mountMode') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(mount, index) in detail.mounts" :key="index">
                  <td class="font-mono text-xs">{{ mount.source || '—' }}</td>
                  <td class="font-mono text-xs">{{ mount.destination }}</td>
                  <td class="text-right text-xs">
                    {{ mount.readWrite ? t('container.detail.readWrite') : t('container.detail.readOnly') }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <p v-else class="text-sm text-content-faint">—</p>
        </SectionCard>

        <SectionCard :title="t('container.detail.networks')">
          <div v-if="detail.networks.length > 0" class="app-table-wrap">
            <table class="app-table">
              <thead>
                <tr>
                  <th>{{ t('nav.networks') }}</th>
                  <th>{{ t('container.detail.networkIp') }}</th>
                  <th>{{ t('container.detail.networkGateway') }}</th>
                  <th>{{ t('container.detail.networkMac') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="network in detail.networks" :key="network.name">
                  <td>{{ network.name }}</td>
                  <td class="font-mono text-xs">{{ orDash(network.ipAddress) }}</td>
                  <td class="font-mono text-xs">{{ orDash(network.gateway) }}</td>
                  <td class="font-mono text-xs">{{ orDash(network.macAddress) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
          <p v-else class="text-sm text-content-faint">—</p>
        </SectionCard>

        <SectionCard :title="t('container.detail.env')" :description="t('container.detail.envHint')">
          <ul v-if="detail.config.env.length > 0" class="app-log-list">
            <li v-for="(item, index) in detail.config.env" :key="index" class="font-mono text-xs">
              {{ item }}
            </li>
          </ul>
          <p v-else class="text-sm text-content-faint">—</p>
        </SectionCard>

        <SectionCard v-if="Object.keys(detail.config.labels).length > 0" :title="t('container.detail.labels')">
          <dl class="app-kv">
            <div v-for="(value, key) in detail.config.labels" :key="key">
              <dt class="font-mono">{{ key }}</dt>
              <dd class="font-mono text-xs break-all">{{ value }}</dd>
            </div>
          </dl>
        </SectionCard>
      </div>
    </template>
  </div>
</template>