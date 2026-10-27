<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { killContainer } from '@/api/container'
import {
  COMMON_SIGNALS,
  DEFAULT_SIGNAL,
  isNonTerminating,
  validateSignal
} from '@/utils/signals'

/**
 * 容器终止对话框（0.7.0，等价于 `docker kill [-s SIGNAL]`）。
 *
 * ## 为什么要有这个对话框
 *
 * `kill` 与 `stop` 的差别是**本质**的：`stop` 先发 SIGTERM 并等进程自己退出，
 * 而 `kill` 直接把信号送到主进程——默认 SIGKILL，容器**没有机会清理现场**。
 * 因此它必须二次确认，而且确认文案要写明「数据可能来不及落盘」。
 *
 * ## 为什么让用户选信号
 *
 * `docker kill -s SIGHUP` 这类用法是「给进程发个信号」，容器**可能继续运行**
 * （取决于它的主进程怎么处理信号）。把它做成「只有 SIGKILL」会少掉一类正当用法；
 * 而做成「随便填什么」又太危险。因此：常用信号给下拉 + 允许手输，
 * 并对**非终止信号**额外提示一次。
 *
 * ## 批量时也要用这个对话框吗
 *
 * 不。批量终止固定用 SIGKILL + 一次简单确认——
 * 批量场景下让用户逐个挑信号不现实，而 SIGKILL 是「全部停掉」的明确语义。
 */
const { t } = useI18n()

const props = defineProps<{
  hostId: string
  /** 要终止的容器（ID 与名字）；批量时多于一个 */
  targets: Array<{ id: string; name: string }>
}>()

const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ killed: [containerIds: string[]] }>()

const signal = ref(DEFAULT_SIGNAL)
const submitting = ref(false)

/** 归一化后的信号；`null` = 交给守护进程取默认值。 */
const normalizedSignal = computed(() => validateSignal(signal.value))

/**
 * 选中的信号是否属于「不保证终止」的那一类。
 *
 * ⚠️ 提示它不是因为它危险，而是因为**预期会落空**：
 * 用户点「终止」却发现容器还在跑，会以为按钮坏了。
 */
const nonTerminating = computed(() => isNonTerminating(normalizedSignal.value))

const canSubmit = computed(() => !submitting.value && props.targets.length > 0)

watch(visible, (open) => {
  if (open) {
    signal.value = DEFAULT_SIGNAL
  }
})

async function submit(): Promise<void> {
  submitting.value = true

  try {
    for (const target of props.targets) {
      await killContainer(props.hostId, target.id, normalizedSignal.value ?? undefined)
    }

    visible.value = false
    ElMessage({
      type: 'success',
      message: t('container.action.done', { name: props.targets[0]?.name ?? '' })
    })
    emit('killed', props.targets.map((target) => target.id))
  } catch {
    // 错误由全局拦截器提示；对话框保持打开，让用户改信号后重试。
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('container.kill.confirm.title')"
    :width="460"
    @close="visible = false"
  >
    <el-alert
      type="warning"
      :closable="false"
      :title="t('container.kill.confirm.message', {
        name: targets[0]?.name ?? '',
        signal: normalizedSignal ?? DEFAULT_SIGNAL
      })"
    />

    <el-form label-position="top" class="mt-3" @submit.prevent="submit">
      <el-form-item :label="t('container.kill.signal')">
        <el-select
          v-model="signal"
          filterable
          allow-create
          default-first-option
          clearable
          class="w-full"
          :placeholder="t('container.signal.custom')"
        >
          <el-option
            v-for="option in COMMON_SIGNALS"
            :key="option.value"
            :value="option.value"
            :label="t(option.labelKey)"
          />
        </el-select>
      </el-form-item>

      <p class="text-xs text-content-muted">
        {{ t('container.kill.signalHint') }}
      </p>

      <p v-if="nonTerminating" class="mt-2 text-xs text-status-warn">
        {{ t('container.kill.nonTerminating', { signal: normalizedSignal ?? '' }) }}
      </p>
    </el-form>

    <template #footer>
      <el-button @click="visible = false">
        {{ t('common.cancel') }}
      </el-button>
      <el-button type="danger" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ t('container.kill.confirm.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
