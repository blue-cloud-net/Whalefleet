<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { inspectContainer, updateContainer } from '@/api/container'
import {
  parseMemoryLimit,
  parseNanoCpus,
  RESTART_POLICIES,
  type RestartPolicy
} from '@/utils/advanced-input'
import type { ContainerDetail } from '@/types/api'

/**
 * 容器热更新对话框（0.3.0）。
 *
 * ## 只开放真能热改的字段
 * Docker 的 `update` 只接受资源类字段与重启策略——镜像、端口、环境变量、挂载、网络
 * **改不了**。本对话框因此只有三行，而不是把 inspect 里的字段都摆出来加一个「保存」：
 * 摆出来会让人填了、点了、什么也没变，而「字段不存在」比「字段无效」更难解释。
 *
 * ## 为什么不提供「取消内存 / CPU 限制」
 * 实测 `docker update --memory=0` **返回 200，而容器上的限制根本没变**
 * （守护进程对 update 的处理是「字段非 0 才应用」，`0` 与「不传」同义）。
 * 所以「取消限制」这个控件一旦做出来，就是一个**点了没反应却显示成功**的按钮。
 * 要取消限制只能重建容器，提示放在下面的说明文字里。
 *
 * ## 为什么要先 inspect 再填初值
 * 后端在更新前会先 inspect、把未改字段按当前值回填，因此返回值一定是真实生效值。
 * 对话框照着详情预填，用户能看到「现在的值」并只改要改的那一项——
 * 不预填的话输入框是空的，而「空 = 不改」这件事用户只能靠猜。
 */
const { t } = useI18n()

const props = defineProps<{
  hostId: string
  containerId: string | null
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ updated: [] }>()

const loading = ref(false)
const submitting = ref(false)

const restartPolicy = ref<RestartPolicy>('no')
const memoryInput = ref('')
const cpusInput = ref('')

/** 字段级错误：错误 **i18n key**，由模板 `t()` 取文案（见 AGENTS.md 的「纯逻辑返回 key」）。 */
const memoryError = ref<string | null>(null)
const cpusError = ref<string | null>(null)

const detail = ref<ContainerDetail | null>(null)

/**
 * 字节 → MiB（输入框的初值）。
 *
 * ⚠️ 单位选 **MiB** 而不是 KiB/GiB 混排：内存限制的常用值都是整 MiB，
 * 换成 GiB 反而要用户在小数里数位数。
 */
const currentMemoryMiB = computed(() => {
  const bytes = currentMemoryBytes.value

  return bytes === null ? null : Math.round(bytes / (1024 * 1024))
})

/** 当前内存上限（字节）；与 `parseMemoryLimit` 的出参单位一致。 */
const currentMemoryBytes = computed(() => detail.value?.hostConfig.memoryLimitBytes ?? null)

const currentNanoCpus = computed(() => detail.value?.hostConfig.nanoCpus ?? null)

const canSubmit = computed(
  () =>
    !loading.value &&
    !submitting.value &&
    (restartPolicy.value.length > 0 || memoryInput.value.trim().length > 0 || cpusInput.value.trim().length > 0)
)

watch(visible, (open) => {
  if (!open) return

  memoryError.value = null
  cpusError.value = null
  detail.value = null
  void load()
})

async function load(): Promise<void> {
  if (!props.containerId) return

  loading.value = true

  try {
    const result = await inspectContainer(props.hostId, props.containerId)

    detail.value = result
    restartPolicy.value = (RESTART_POLICIES as readonly string[]).includes(
      result.hostConfig.restartPolicy
    )
      ? (result.hostConfig.restartPolicy as RestartPolicy)
      : 'no'

    // ⚠️ 只预填「当前确实有限制」的两项：未限制（null）留空，
    // 因为空 = 不改，而把 null 显示成 0 会让人以为「0 就是不限」并顺手改它。
    memoryInput.value = currentMemoryMiB.value === null ? '' : String(currentMemoryMiB.value)
    cpusInput.value = currentNanoCpus.value === null ? '' : String(currentNanoCpus.value / 1e9)  } catch {
    // 拉不到详情就别让用户对着空表单瞎填：关掉对话框比给一个假初值好
    visible.value = false
  } finally {
    loading.value = false
  }
}

async function submit(): Promise<void> {
  const memory = parseMemoryLimit(memoryInput.value)
  const cpus = parseNanoCpus(cpusInput.value)

  memoryError.value = memory === 'error' ? 'container.update.memoryInvalid' : null
  cpusError.value = cpus === 'error' ? 'container.update.cpusInvalid' : null

  if (memory === 'error' || cpus === 'error') return

  submitting.value = true

  try {
    // ⚠️ 只发用户**可能**改过的字段：与「当前值相同」的字段不发出去。
    // 这不是省流量，而是 Docker 的 update 对未传字段的处理随版本变化，
    // 全部发出去等于把「没改的」也交给守护进程决定。
    const payload: Record<string, string | number> = {}

    if (restartPolicy.value !== detail.value?.hostConfig.restartPolicy) {
      payload.restartPolicy = restartPolicy.value
    }

    if (memory !== null && memory !== currentMemoryBytes.value) {
      payload.memoryLimitBytes = memory
    }

    if (cpus !== null && cpus !== currentNanoCpus.value) {
      payload.nanoCpus = cpus
    }

    // 三项都没变就不发请求：后端会把「全省略」当成非法（1000），
    // 而界面上「什么都没改」本就不该是错误。
    if (Object.keys(payload).length === 0) {
      visible.value = false
      return
    }

    const result = await updateContainer(props.hostId, props.containerId ?? '', payload)

    visible.value = false
    ElMessage({
      type: 'success',
      message: t('container.update.done', { name: detail.value?.name ?? '' })
    })
    emit('updated')
    void result
  } catch {
    // 错误由全局拦截器提示；对话框保持打开以便直接改。
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('container.update.title')"
    :width="480"
    @close="visible = false"
  >
    <div v-loading="loading">
      <el-form label-position="top" @submit.prevent="submit">
        <el-form-item :label="t('container.update.restartPolicy')">
          <el-select v-model="restartPolicy" class="w-full">
            <el-option
              v-for="policy in RESTART_POLICIES"
              :key="policy"
              :label="t(`container.restartPolicy.${policy === 'on-failure' ? 'onFailure' : policy}`)"
              :value="policy"
            />
          </el-select>
        </el-form-item>

        <el-form-item :label="t('container.update.memory')" :error="memoryError ? t(memoryError) : undefined">
          <el-input v-model="memoryInput" :placeholder="t('container.update.memoryPlaceholder')" />
        </el-form-item>

        <el-form-item :label="t('container.update.cpus')" :error="cpusError ? t(cpusError) : undefined">
          <el-input v-model="cpusInput" :placeholder="t('container.update.cpusPlaceholder')" />
        </el-form-item>

        <p class="text-xs text-content-muted">
          {{ t('container.update.hint') }}
        </p>
        <p class="mt-1 text-xs text-status-warn">
          {{ t('container.update.noUnlimited') }}
        </p>
      </el-form>
    </div>

    <template #footer>
      <el-button @click="visible = false">
        {{ t('common.cancel') }}
      </el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ t('common.save') }}
      </el-button>
    </template>
  </el-dialog>
</template>
