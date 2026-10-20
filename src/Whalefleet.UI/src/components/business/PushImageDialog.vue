<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { pushImage } from '@/api/image'
import { listRepositories } from '@/api/repository'
import { isDangling, stackedTagCount } from '@/utils/image-display'
import { findRepositoryForReference, repositoryAddressOf } from '@/utils/repository-display'
import { useAuthStore } from '@/stores/auth'
import type { ImagePushResult, ImageSummary, RepositorySummary } from '@/types/api'

/**
 * 推送镜像对话框（0.4.0）。
 *
 * ## 为什么默认「自动匹配」而不是让用户必选一条配置
 * 后端的匹配规则是「按镜像引用里的地址段自动找已登记的配置」，也就是
 * `harbor.local:5000/app:v1` → 找 `harbor.local:5000` 那一条。
 * 绝大多数情况下这就是用户想要的，而让他们在一个下拉框里再选一次，
 * 只会多出「选错成另一台仓库」的机会。
 *
 * 因此对话框把**匹配结果先摆出来**（命中哪一条 / 没命中会匿名推送），
 * 让风险可见，同时保留下拉框供「本地标签不带前缀、但就要推到内网 Harbor」
 * 这种场景显式指定。
 *
 * ## 为什么必须提示「只推第一个标签」
 * 后端推多个标签时取第一个并给出说明——而在界面上，一个镜像 ID 常挂
 * `nginx:latest` + `nginx:1.27` 两个标签。不说清楚，用户会以为两个都推上去了。
 */
const props = defineProps<{
  hostId: string
  /** 要推送的镜像；`null` 表示对话框未绑定具体镜像（当前不使用） */
  image: ImageSummary | null
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ pushed: [ImagePushResult] }>()

const { t } = useI18n()
const auth = useAuthStore()

const repositories = ref<RepositorySummary[]>([])
/** 显式指定的配置主键；空串 = 自动匹配（`el-option` 不接受 `null` 作为值） */
const repositoryId = ref('')
const platform = ref('')
const submitting = ref(false)
const errorMessage = ref<string | null>(null)
const lastResult = ref<ImagePushResult | null>(null)

/** 本次要推送的引用：镜像有多个标签时取第一个（与后端一致）。 */
const reference = computed(() => {
  const tags = props.image?.repoTags ?? []

  return tags.length > 0 ? tags[0] : ''
})

/** 悬挂镜像没有标签，推送必然失败（后端返回 1000）——界面直接拦住。 */
const isUntagged = computed(() => props.image === null || isDangling(props.image.repoTags))

/** 自动匹配到的配置（仅用于展示，提交时仍交给后端再匹配一次）。 */
const matchedRepository = computed(() =>
  reference.value.length === 0 ? null : findRepositoryForReference(reference.value, repositories.value)
)

/** 目标 仓库地址（从引用推出来，仅用于展示）。 */
const targetAddress = computed(() => repositoryAddressOf(reference.value))

const canSubmit = computed(
  () => !submitting.value && !isUntagged.value && reference.value.length > 0
)

/**
 * 加载已登记的配置。
 *
 * 只有具备 `repository.read` 才拉：没有该权限时后端会回 `1500`，
 * 而「推送到哪」由引用本身决定，缺这份列表不影响自动匹配（只是看不到匹配结果）。
 * 这里**不把失败抛给用户**——推送本身可能完全正常。
 */
async function loadRepositories(): Promise<void> {
  if (!auth.hasPermission('repository.read')) {
    repositories.value = []
    return
  }

  try {
    repositories.value = await listRepositories()
  } catch {
    repositories.value = []
  }
}

watch(visible, (open) => {
  if (!open) return

  repositoryId.value = ''
  platform.value = ''
  errorMessage.value = null
  lastResult.value = null
  void loadRepositories()
})

async function submit(): Promise<void> {
  if (!canSubmit.value) return

  submitting.value = true
  errorMessage.value = null
  lastResult.value = null
  try {
    const result = await pushImage(props.hostId, {
      image: reference.value,
      // 空串不发送：交给后端按地址段自动匹配
      repositoryId: repositoryId.value || undefined,
      platform: platform.value.trim() || undefined
    })

    lastResult.value = result
    ElMessage({
      // 匿名推送也是成功，但**必须说明**：用户看到「已推送」会以为凭据生效了，
      // 而实际上可能是「这个仓库根本不要凭据」——那往往意味着地址写错了
      type: result.authenticated ? 'success' : 'warning',
      message: result.authenticated
        ? t('image.push.done', { reference: result.reference })
        : t('image.push.doneAnonymous', { reference: result.reference }),
      showClose: true
    })
    emit('pushed', result)
  } catch (e) {
    // 1807（推送失败）/ 1804（凭据被拒）/ 1800（指定的配置不存在）
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}

function close(): void {
  visible.value = false
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('image.push.title')"
    width="min(560px, 94vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="t('image.push.reference')">
        <span class="font-mono text-sm text-content-strong">
          {{ reference || t('image.push.noTag') }}
        </span>
        <!-- 多标签：后端只推第一个，必须说清楚 -->
        <p v-if="stackedTagCount(image?.repoTags) > 0" class="mt-1 text-xs text-status-warn">
          {{ t('image.push.multiTag', { count: stackedTagCount(image?.repoTags) }) }}
        </p>
      </el-form-item>

      <el-alert
        v-if="isUntagged"
        class="mb-3"
        type="warning"
        :closable="false"
        :title="t('image.push.untagged')"
      />

      <el-form-item :label="t('image.push.repository')">
        <el-select v-model="repositoryId" class="w-full" :disabled="submitting || isUntagged">
          <el-option :label="t('image.push.registryAuto')" value="" />
          <el-option
            v-for="item in repositories"
            :key="item.id"
            :label="`${item.name}（${item.serverAddress}）`"
            :value="item.id"
          />
        </el-select>
        <!-- 把自动匹配的结果摆出来：否则用户不知道自己到底会不会带凭据 -->
        <p class="mt-1 text-xs text-content-muted">
          <template v-if="repositoryId !== ''">{{ t('image.push.registryManual') }}</template>
          <template v-else-if="matchedRepository">
            {{ t('image.push.matched', { name: matchedRepository.name, address: targetAddress }) }}
          </template>
          <template v-else>
            {{ t('image.push.unmatched', { address: targetAddress }) }}
          </template>
        </p>
      </el-form-item>

      <el-form-item :label="t('image.push.platform')">
        <el-input
          v-model="platform"
          :placeholder="t('image.push.platformPlaceholder')"
          :disabled="submitting || isUntagged"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('image.push.platformHint') }}</p>
      </el-form-item>

      <el-alert
        v-if="errorMessage"
        class="mt-1"
        type="error"
        :closable="false"
        :title="errorMessage"
      />

      <div v-if="lastResult" class="mt-3">
        <el-alert
          :type="lastResult.authenticated ? 'success' : 'warning'"
          :closable="false"
          :title="
            lastResult.authenticated
              ? t('image.push.logTitleAuth', { address: lastResult.repositoryServerAddress })
              : t('image.push.logTitleAnon', { address: lastResult.repositoryServerAddress })
          "
        />
        <pre
          v-if="lastResult.pushLog.length > 0"
          class="mt-2 max-h-48 overflow-auto rounded-md bg-surface-sunken p-3 text-xs font-mono text-content-body whitespace-pre-wrap"
          >{{ lastResult.pushLog.join('\n') }}</pre
        >
      </div>

      <p v-if="submitting" class="mt-3 text-xs text-content-muted">
        {{ t('image.push.waiting') }}
      </p>
    </el-form>

    <template #footer>
      <el-button :disabled="submitting" @click="close">{{ t('common.close') }}</el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ submitting ? t('image.push.submitting') : t('image.action.push') }}
      </el-button>
    </template>
  </el-dialog>
</template>
