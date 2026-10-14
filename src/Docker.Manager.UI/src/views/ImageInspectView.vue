<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import JsonTree from '@/components/common/JsonTree.vue'
import ImageSize from '@/components/business/ImageSize.vue'
import { inspectImage } from '@/api/image'
import { ApiRequestError } from '@/api'
import { isDangling, primaryImageName, shortDigest } from '@/utils/image-display'
import { formatDateTime } from '@/utils/datetime'
import type { ImageDetail } from '@/types/api'

/**
 * 镜像详情页（0.0.12）。
 *
 * ## 为什么是结构化投影而不是 JSON 树
 * roadmap 早期写的是「JSON 树形展示」，实现时改成**分组卡片**（与容器详情页同一取舍）：
 * JSON 树要求用户自己找字段，而「这个镜像用什么命令启动、暴露了哪些端口、声明了哪些卷」
 * 在结构化视图里一眼可得。为此牺牲的「一个字段不落」并不存在——后端 inspect
 * 本身就是精选投影，没有藏在树里的字段。
 * 原始 JSON 仍以可折叠的形式保留，供排查契约问题时对照（它是**投影后**的 JSON，
 * 不是守护进程的原始响应）。
 *
 * ## 为什么 env 与 labels 要贴警示
 * 有人把凭据写进 `ENV` / `LABEL`（构建参数、私有仓库口令）。后端已在类型注释里写明，
 * 前端再警示一次：这是本页唯一「展示出来就可能造成泄漏」的区域。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const hostId = computed(() => String(route.params.hostId ?? ''))
// `route.params.id` 已由路由解码（段内 `sha256%3A…` → `sha256:…`），不要再手动 decode
const imageId = computed(() => String(route.params.id ?? ''))

const detail = ref<ImageDetail | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

/** 「字段为空」与「值为空串」要区分：前者显示占位符，后者可能是刻意的空值。 */
function orDash(value: string | null | undefined): string {
  return value && value.length > 0 ? value : '—'
}

async function load(): Promise<void> {
  if (!hostId.value || !imageId.value) return

  loading.value = true
  error.value = null
  try {
    detail.value = await inspectImage(hostId.value, imageId.value)
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
    detail.value = null
  } finally {
    loading.value = false
  }
}

function backToList(): void {
  void router.push({ name: 'host-images', params: { hostId: hostId.value } })
}

watch(imageId, load, { immediate: true })
</script>

<template>
  <div v-loading="loading">
    <PageHeader icon="i-carbon-image" :title="t('image.detail.title')" narrow>
      <template #actions>
        <el-button @click="backToList">{{ t('image.backToList') }}</el-button>
        <el-button :loading="loading" type="primary" @click="load">
          {{ t('common.refresh') }}
        </el-button>
      </template>

      <template v-if="detail" #badge>
        <span class="ml-2 align-middle text-sm text-content-muted">
          {{ primaryImageName(detail.repoTags) }}
        </span>
      </template>
    </PageHeader>

    <div v-if="error" class="app-section">
      <EmptyState icon="i-carbon-warning-alt" :description="error">
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </EmptyState>
    </div>

    <div v-else-if="detail" class="flex flex-col gap-4">
      <SectionCard :title="t('image.detail.overview')">
        <dl class="app-kv">
          <div>
            <dt>{{ t('image.col.repository') }}</dt>
            <dd>
              <span v-if="isDangling(detail.repoTags)" class="text-content-muted italic">
                {{ t('image.dangling.hint') }}
              </span>
              <span v-else class="font-medium">{{ detail.repoTags.join(' · ') }}</span>
            </dd>
          </div>
          <div>
            <dt>{{ t('image.detail.id') }}</dt>
            <dd class="font-mono text-xs break-all">{{ detail.id }}</dd>
          </div>
          <div v-if="detail.repoDigests.length > 0">
            <dt>{{ t('image.detail.digest') }}</dt>
            <dd class="font-mono text-xs">{{ shortDigest(detail.repoDigests[0]) }}</dd>
          </div>
          <div><dt>{{ t('image.col.created') }}</dt><dd>{{ formatDateTime(detail.created) }}</dd></div>
          <div><dt>{{ t('image.col.size') }}</dt><dd><ImageSize :size="detail.size" /></dd></div>
          <div>
            <dt>{{ t('image.detail.platform') }}</dt>
            <dd>{{ orDash(detail.os) }} / {{ orDash(detail.architecture) }}</dd>
          </div>
          <div v-if="detail.osVersion">
            <dt>{{ t('image.detail.osVersion') }}</dt>
            <dd>{{ detail.osVersion }}</dd>
          </div>
          <div><dt>{{ t('image.detail.layers') }}</dt><dd>{{ detail.layerCount }}</dd></div>
          <div v-if="detail.author"><dt>{{ t('image.detail.author') }}</dt><dd>{{ detail.author }}</dd></div>
          <div v-if="detail.comment"><dt>{{ t('image.detail.comment') }}</dt><dd>{{ detail.comment }}</dd></div>
        </dl>
      </SectionCard>

      <SectionCard :title="t('image.detail.build')">
        <dl class="app-kv">
          <div>
            <dt>{{ t('container.create.entrypoint') }}</dt>
            <dd class="font-mono text-xs break-all">{{ orDash(detail.entrypoint.join(' ')) }}</dd>
          </div>
          <div>
            <dt>{{ t('container.create.cmd') }}</dt>
            <dd class="font-mono text-xs break-all">{{ orDash(detail.cmd.join(' ')) }}</dd>
          </div>
          <div>
            <dt>{{ t('container.create.workingDir') }}</dt>
            <dd class="font-mono text-xs">{{ orDash(detail.workingDir) }}</dd>
          </div>
        </dl>
      </SectionCard>

      <SectionCard :title="t('image.detail.declared')">
        <dl class="app-kv">
          <div>
            <dt>{{ t('image.detail.exposedPorts') }}</dt>
            <dd>
              <span v-if="detail.exposedPorts.length === 0" class="text-content-faint">—</span>
              <span v-else class="flex flex-wrap gap-1">
                <span
                  v-for="port in detail.exposedPorts"
                  :key="port"
                  class="rounded px-1.5 py-0.5 font-mono text-xs bg-surface-sunken text-content-body"
                >
                  {{ port }}
                </span>
              </span>
            </dd>
          </div>
          <div>
            <dt>{{ t('image.detail.volumes') }}</dt>
            <dd class="font-mono text-xs break-all">
              {{ detail.volumes.length > 0 ? detail.volumes.join(' · ') : '—' }}
            </dd>
          </div>
        </dl>
      </SectionCard>

      <SectionCard :title="t('container.detail.env')" :description="t('container.detail.envHint')">
        <ul v-if="detail.env.length > 0" class="app-log-list">
          <li v-for="(item, index) in detail.env" :key="index" class="font-mono text-xs">
            {{ item }}
          </li>
        </ul>
        <p v-else class="text-sm text-content-faint">—</p>
      </SectionCard>

      <SectionCard
        v-if="Object.keys(detail.labels).length > 0"
        :title="t('container.detail.labels')"
        :description="t('image.detail.labelsHint')"
      >
        <dl class="app-kv">
          <div v-for="(value, key) in detail.labels" :key="key">
            <dt class="font-mono">{{ key }}</dt>
            <dd class="font-mono text-xs break-all">{{ value }}</dd>
          </div>
        </dl>
      </SectionCard>

      <SectionCard :title="t('image.detail.raw')" density="flush">
        <el-collapse>
          <el-collapse-item :title="t('image.detail.rawToggle')" name="raw">
            <!--
              用 JSON 树而不是 <pre>：结构化投影已经是精选字段，这里再铺开全量 JSON
              主要是为了排查契约问题，而排查的人恰恰知道要找哪个字段——
              树能折叠到具体子树，<pre> 只能靠滚动与搜索。
            -->
            <JsonTree :data="detail" :max-depth="1" class="max-h-80 overflow-auto" />
          </el-collapse-item>
        </el-collapse>
      </SectionCard>
    </div>
  </div>
</template>
