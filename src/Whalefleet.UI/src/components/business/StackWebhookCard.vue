<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { revokeStackWebhook, rotateStackWebhook } from '@/api/stack'
import { ApiRequestError } from '@/api'
import { formatDateTime } from '@/utils/datetime'
import { TIP_DELAY_MS } from '@/utils/tooltip'
import type { StackSummary } from '@/types/api'

/**
 * 编排 Webhook 卡片（0.10.0）。
 *
 * ## 令牌明文**只出现一次**
 * 服务端只存哈希，明文只在生成 / 轮换的那一刻返回一次。
 * 因此这里必须**当场展示 + 强调「请立刻保存」**——
 * 用户关掉对话框就再也拿不到了，而这不是「可以再生成一个」的普通配置。
 *
 * ## 为什么 URL 要整段可复制
 * CI 触发器（GitHub / GitLab webhook）只能填一个 URL，
 * 因此令牌在**路径**里；用户需要复制的是完整 URL，而不是路径片段。
 */
const props = defineProps<{
  /** 当前主机标识。 */
  hostId: string
  /** 对应的 Stack。 */
  stack: StackSummary
}>()

const emit = defineEmits<{ changed: [] }>()

const { t } = useI18n()

/** 刚生成的令牌明文（一次性）。 */
const freshToken = ref<string | null>(null)

/** 正在执行。 */
const busy = ref(false)

/** 错误消息。 */
const errorMessage = ref<string | null>(null)

/** Webhook 触发地址。 */
const webhookUrl = ref('')

watch(
  () => [props.hostId, props.stack.name],
  () => {
    freshToken.value = null
    errorMessage.value = null
    webhookUrl.value = buildUrl()
  },
  { immediate: true }
)

/**
 * 拼出 Webhook 触发地址。
 *
 * ⚠️ **用页面的 origin 而不是相对路径**：CI 触发器要的是**绝对 URL**，
 * 而它填的地址是在 GitHub 那边配置的，看不到本应用的页面路径。
 */
function buildUrl(): string {
  const origin = typeof window === 'undefined' ? '' : window.location.origin

  return `${origin}/api/v1/webhooks/stacks/{token}`
}

/**
 * 生成 / 轮换令牌。
 */
async function rotate(): Promise<void> {
  busy.value = true
  errorMessage.value = null

  try {
    const info = await rotateStackWebhook(props.hostId, props.stack.name)

    freshToken.value = info.token
    emit('changed')
  } catch (e) {
    errorMessage.value = e instanceof ApiRequestError ? e.message : t('stack.webhook.rotateFailed')
  } finally {
    busy.value = false
  }
}

/**
 * 吊销令牌。
 */
async function revoke(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      t('stack.webhook.revokeConfirm'),
      t('stack.webhook.revokeTitle'),
      {
        type: 'warning',
        confirmButtonText: t('common.confirm'),
        cancelButtonText: t('common.cancel')
      }
    )
  } catch {
    return
  }

  busy.value = true
  errorMessage.value = null

  try {
    await revokeStackWebhook(props.hostId, props.stack.name)
    freshToken.value = null
    emit('changed')
    ElMessage.success(t('stack.webhook.revoked'))
  } catch (e) {
    errorMessage.value = e instanceof ApiRequestError ? e.message : t('stack.webhook.revokeFailed')
  } finally {
    busy.value = false
  }
}

/**
 * 复制令牌明文。
 */
async function copyToken(): Promise<void> {
  if (!freshToken.value) return

  try {
    await navigator.clipboard.writeText(freshToken.value)
    ElMessage.success(t('common.copied'))
  } catch {
    ElMessage.error(t('common.copyFailed'))
  }
}
</script>

<template>
  <div class="flex flex-col gap-3">
    <!--
      状态行：是否启用 + 令牌提示 + 最近触发时间。
      ⚠️ **令牌提示不是令牌**：它只是明文的前几位，用于「区分哪一个令牌」，
      显示它是为了让用户知道「现在生效的是哪个」，而不是让他拿它去用。
    -->
    <div class="flex flex-wrap items-center gap-3">
      <el-tag :type="stack.webhookEnabled ? 'success' : 'info'" size="small">
        {{ stack.webhookEnabled ? t('stack.webhook.enabled') : t('stack.webhook.disabled') }}
      </el-tag>

      <span v-if="stack.webhookTokenHint" class="font-mono text-xs text-content-muted">
        {{ stack.webhookTokenHint }}…
      </span>

      <span v-if="stack.webhookLastUsedAt" class="text-xs text-content-muted">
        {{ t('stack.webhook.lastUsed', { time: formatDateTime(stack.webhookLastUsedAt) }) }}
      </span>
    </div>

    <!--
      明文只在生成时出现。这里必须**显式警告「只此一次」**——
      否则用户会以为刷新一下还能再看到。
    -->
    <el-alert v-if="freshToken" type="warning" :closable="false" show-icon>
      <template #title>{{ t('stack.webhook.tokenTitle') }}</template>
      <p class="mt-1 text-xs">{{ t('stack.webhook.tokenHint') }}</p>
      <div class="mt-2 flex items-center gap-2">
        <code class="flex-1 truncate rounded bg-surface-hover px-2 py-1 font-mono text-xs">
          {{ freshToken }}
        </code>
        <el-button size="small" @click="copyToken">{{ t('common.copy') }}</el-button>
      </div>
    </el-alert>

    <p class="text-xs text-content-muted">{{ t('stack.webhook.urlHint', { url: webhookUrl }) }}</p>

    <el-alert
      v-if="errorMessage"
      type="error"
      :closable="false"
      :title="errorMessage"
    />

    <div class="flex items-center gap-2">
      <el-tooltip
        :content="t('stack.webhook.rotateHint')"
        placement="top"
        :show-after="TIP_DELAY_MS"
      >
        <span class="inline-flex">
          <el-button :loading="busy" @click="rotate">
            {{ stack.webhookEnabled ? t('stack.webhook.rotate') : t('stack.webhook.enable') }}
          </el-button>
        </span>
      </el-tooltip>

      <el-button v-if="stack.webhookEnabled" :loading="busy" @click="revoke">
        {{ t('stack.webhook.revoke') }}
      </el-button>
    </div>
  </div>
</template>
