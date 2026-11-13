<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import {
  checkImageUpdatePolicy,
  createImageUpdatePolicy,
  listImageUpdatePolicies,
  removeImageUpdatePolicy,
  updateImageUpdatePolicy
} from '@/api/image-update'
import { ApiRequestError } from '@/api'
import { isDangling, primaryImageName } from '@/utils/image-display'
import { formatDateTime } from '@/utils/datetime'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import StatusBadge from '@/components/common/StatusBadge.vue'
import type { ImageUpdateMode, ImageUpdatePolicy, ImageUpdateState } from '@/types/api'

/**
 * 镜像自动更新卡片（0.15.0）。
 *
 * ## 为什么按「镜像 + 主机」而不是按镜像 ID
 * 要跟踪的正是「这个**标签**背后的摘要变了」。绑 ID 等于绑一个不可变的快照，
 * 永远查不出更新。因此悬空镜像（没有标签）**不提供**这个功能——
 * 没有标签就没有可比对的目标。
 *
 * ## 状态五档，展示必须分开
 * `unknown`（还没查过）显示「—」，`up-to-date`（查过、确实最新）显示「已是最新」。
 * 合并会让刚加上的策略立刻宣称自己是最新的，而它一次都没查过。
 *
 * ## 检查失败不是请求失败
 * 后端对「仓库不可达 / 需授权 / 限流」返回 **200** + `state=failed`。
 * 因此这里不靠 catch 判成败，而是看 `state`——否则用户会看到「操作失败」，
 * 而实际上请求完全成功，只是仓库今天不通。
 */
const props = defineProps<{
  /** 当前主机标识。 */
  hostId: string
  /** 镜像的全部标签（取第一个作为跟踪目标）。 */
  repoTags: readonly string[]
}>()

const { t, te } = useI18n()

/** 该镜像在本主机上的策略；可能不存在。 */
const policy = ref<ImageUpdatePolicy | null>(null)

/** 正在加载。 */
const loading = ref(false)

/** 正在执行动作。 */
const busy = ref(false)

/** 错误提示。 */
const errorMessage = ref<string | null>(null)

/** 当前跟踪的镜像引用（悬空镜像为空串）。 */
const reference = computed(() =>
  isDangling(props.repoTags) ? '' : primaryImageName(props.repoTags)
)

/** 是否有可跟踪的标签。 */
const trackable = computed(() => reference.value.length > 0)

/** 状态 → i18n key 的显式映射。 */
const STATE_KEY: Record<ImageUpdateState, string> = {
  unknown: 'imageUpdate.state.unknown',
  'up-to-date': 'imageUpdate.state.upToDate',
  'update-available': 'imageUpdate.state.updateAvailable',
  applied: 'imageUpdate.state.applied',
  failed: 'imageUpdate.state.failed'
}

/** 状态 → 徽标色调（四档语义色，与 StatusBadge 的 tone 对应）。 */
const STATE_TONE: Record<ImageUpdateState, 'ok' | 'down' | 'warn' | 'idle'> = {
  unknown: 'idle',
  'up-to-date': 'ok',
  'update-available': 'warn',
  applied: 'ok',
  failed: 'down'
}

/**
 * 读取错误码对应的提示；未登记的错误码回退到后端消息。
 *
 * @param error 捕获到的异常。
 * @returns 提示文案。
 */
function describeError(error: unknown): string {
  if (error instanceof ApiRequestError) {
    const key = `imageUpdate.error.${error.code}`

    if (te(key)) {
      return t(key)
    }

    return error.message
  }

  return t('common.operationFailed')
}

/** 加载策略。 */
async function load(): Promise<void> {
  if (!trackable.value) {
    policy.value = null

    return
  }

  loading.value = true
  errorMessage.value = null

  try {
    const all = await listImageUpdatePolicies(props.hostId)
    policy.value = all.find((item) => item.imageReference === reference.value) ?? null
  } catch (error) {
    errorMessage.value = describeError(error)
  } finally {
    loading.value = false
  }
}

/**
 * 开启跟踪（默认「只通知」）。
 *
 * 默认不是 `auto`：自动更新会真的拉镜像并重建容器，而用户点的是一个
 * 看起来像「加个监视」的开关。
 */
async function enable(): Promise<void> {
  busy.value = true

  try {
    policy.value = await createImageUpdatePolicy(props.hostId, {
      reference: reference.value,
      mode: 'notify',
      enabled: true
    })
    ElMessage.success(t('common.saved'))
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    busy.value = false
  }
}

/** 关闭跟踪（删除策略）。 */
async function disable(): Promise<void> {
  if (!policy.value) return

  busy.value = true

  try {
    await removeImageUpdatePolicy(props.hostId, policy.value.id)
    policy.value = null
    ElMessage.success(t('common.deleted'))
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    busy.value = false
  }
}

/**
 * 切换执行模式。
 *
 * @param mode 新的执行模式。
 */
async function setMode(mode: ImageUpdateMode): Promise<void> {
  if (!policy.value) return

  busy.value = true

  try {
    policy.value = await updateImageUpdatePolicy(props.hostId, {
      id: policy.value.id,
      mode
    })
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    busy.value = false
  }
}

/**
 * 启用 / 停用。
 *
 * @param enabled 目标状态。
 */
async function setEnabled(enabled: boolean): Promise<void> {
  if (!policy.value) return

  busy.value = true

  try {
    policy.value = await updateImageUpdatePolicy(props.hostId, {
      id: policy.value.id,
      enabled
    })
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    busy.value = false
  }
}

/** 立即检查。 */
async function check(): Promise<void> {
  if (!policy.value) return

  busy.value = true

  try {
    const result = await checkImageUpdatePolicy(props.hostId, policy.value.id)
    policy.value = result.policy

    // 结论分三种走向，每种给不同的反馈——统一成「检查完成」等于什么也没说。
    if (result.policy.state === 'failed') {
      ElMessage.warning(result.policy.message ?? t('imageUpdate.state.failed'))
    } else if (result.policy.state === 'update-available') {
      ElMessage.warning(t('imageUpdate.check.updateAvailable'))
    } else if (result.recreatedContainers > 0) {
      ElMessage.success(t('imageUpdate.check.applied', { count: result.recreatedContainers }))
    } else {
      ElMessage.success(t('imageUpdate.check.upToDate'))
    }
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    busy.value = false
  }
}

watch(() => [props.hostId, reference.value], load, { immediate: true })
</script>

<template>
  <SectionCard
    v-if="trackable"
    :title="t('imageUpdate.title')"
    :description="t('imageUpdate.hint')"
  >
    <template #actions>
      <template v-if="policy">
        <el-switch
          :model-value="policy.enabled"
          size="small"
          :disabled="busy"
          @update:model-value="(v: any) => setEnabled(Boolean(v))"
        />
        <el-button size="small" :loading="busy" icon="i-carbon-renew" @click="check">
          {{ t('imageUpdate.checkNow') }}
        </el-button>
        <el-button size="small" type="danger" plain :disabled="busy" @click="disable">
          {{ t('imageUpdate.untrack') }}
        </el-button>
      </template>
      <el-button
        v-else
        type="primary"
        size="small"
        :loading="busy || loading"
        icon="i-carbon-add"
        @click="enable"
      >
        {{ t('imageUpdate.track') }}
      </el-button>
    </template>

    <EmptyState
      v-if="!policy && !loading"
      icon="i-carbon-notification"
      :description="t('imageUpdate.notTracked')"
      compact
    />

    <div v-else-if="policy" class="space-y-4">
      <dl class="app-facts">
        <div>
          <dt>{{ t('imageUpdate.state.label') }}</dt>
          <dd>
            <StatusBadge :tone="STATE_TONE[policy.state]">{{ t(STATE_KEY[policy.state]) }}</StatusBadge>
          </dd>
        </div>

        <div>
          <dt>{{ t('imageUpdate.mode.label') }}</dt>
          <dd>
            <!--
              模式用单选而不是下拉：只有两档，而且「自动重建容器」是个有副作用的
              选择，摊开显示比藏在下拉里更诚实。
            -->
            <el-radio-group
              :model-value="policy.mode"
              size="small"
              :disabled="busy"
              @update:model-value="(v: any) => setMode(v as ImageUpdateMode)"
            >
              <el-radio-button value="notify">{{ t('imageUpdate.mode.notify') }}</el-radio-button>
              <el-radio-button value="auto">{{ t('imageUpdate.mode.auto') }}</el-radio-button>
            </el-radio-group>
          </dd>
        </div>

        <div>
          <dt>{{ t('imageUpdate.lastCheckedAt') }}</dt>
          <dd>{{ policy.lastCheckedAt ? formatDateTime(policy.lastCheckedAt) : '—' }}</dd>
        </div>

        <div v-if="policy.remoteDigest">
          <dt>{{ t('imageUpdate.remoteDigest') }}</dt>
          <dd class="font-mono text-xs break-all">{{ policy.remoteDigest }}</dd>
        </div>
      </dl>

      <!--
        失败原因与「有更新」提示都要显示原文：用户需要知道是网络问题、
        还是仓库要授权（两者的处置完全不同）。
      -->
      <p v-if="policy.message" class="text-xs text-content-muted">{{ policy.message }}</p>

      <p v-if="policy.mode === 'auto'" class="text-xs text-content-faint">
        {{ t('imageUpdate.autoHint') }}
      </p>
    </div>
  </SectionCard>
</template>
