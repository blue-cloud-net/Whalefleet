<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { addRepository, testRepository } from '@/api/repository'
import { describeRepositoryTest } from '@/utils/repository-display'
import type { RepositorySummary } from '@/types/api'

/**
 * 新增 仓库配置的对话框（0.4.0）。
 *
 * ## 为什么只有「新增」没有「编辑」
 * 后端**不返回口令，也不返回口令密文**（连密文都能当钥匙用），
 * 于是「留空表示不修改」这类编辑语义无法实现：服务端要保留旧值就得把旧值读出来。
 * 所以改口令的正确做法是删掉重加，界面不提供做不到的功能。
 *
 * ## 为什么「测试连接」用**未保存的**输入
 * 用户填完地址与口令、点保存，才发现凭据不对——那时配置已经落地了。
 * 这里直接调 `test` 的「未保存」形态（`serverAddress` + 凭据，不带 `id`），
 * 让「先试再存」成为默认动线。
 *
 * ⚠️ 测试失败**不阻止保存**：Registry 可能只是暂时不可达（网络抖动 / 内网未开机），
 * 而配置本身是合法的。把两个动作绑死，用户就得先修好网络才能登记一条配置。
 */
const visible = defineModel<boolean>({ required: true })

const emit = defineEmits<{ created: [RepositorySummary] }>()

const { t } = useI18n()

const name = ref('')
const serverAddress = ref('')
const username = ref('')
const password = ref('')
const description = ref('')

const submitting = ref(false)
const testing = ref(false)
const errorMessage = ref<string | null>(null)
/**
 * 测试结论：`null` 表示尚未测试过。
 *
 * `type` 直接存 `el-alert` 的取值而不是业务语义（`ok` / `warn`），
 * 否则「ok」会被当成未知类型，alert 静默退化成默认样式——图标与配色全丢。
 */
const testVerdict = ref<{ type: 'success' | 'warning' | 'error'; text: string } | null>(null)

/**
 * 账号与口令必须**同时给或同时不给**（后端两个方向都校验）。
 *
 * 只给账号等于「用空口令登录」，只给口令等于「没有账号」——两者都是笔误，
 * 而放过去只会得到一条必然认证失败的配置，且失败要等到第一次推送才发现。
 */
const accountPairError = computed(() => {
  const hasUser = username.value.trim().length > 0
  const hasPassword = password.value.length > 0

  return hasUser !== hasPassword
})

const canSubmit = computed(
  () =>
    !submitting.value &&
    name.value.trim().length > 0 &&
    serverAddress.value.trim().length > 0 &&
    !accountPairError.value
)

watch(visible, (open) => {
  if (!open) return

  name.value = ''
  serverAddress.value = ''
  username.value = ''
  password.value = ''
  description.value = ''
  errorMessage.value = null
  testVerdict.value = null
})

/** 清掉上一次的测试结论：改了任一字段后，旧结论就不再代表当前输入。 */
function invalidateVerdict(): void {
  testVerdict.value = null
}

async function runTest(): Promise<void> {
  if (serverAddress.value.trim().length === 0) {
    errorMessage.value = t('repository.form.error.addressRequired')
    return
  }

  testing.value = true
  errorMessage.value = null
  testVerdict.value = null
  try {
    const result = await testRepository({
      serverAddress: serverAddress.value.trim(),
      username: username.value.trim() || undefined,
      password: password.value || undefined
    })
    const verdict = describeRepositoryTest(result)

    // 结论文案优先用后端给的原因（含具体 HTTP 状态与仓库返回的提示），
    // 映射出来的 key 只是兜底：后端那句更可能指出真正的原因。
    testVerdict.value = {
      type: verdict.tone === 'ok' ? 'success' : verdict.tone === 'warn' ? 'warning' : 'error',
      text: result.message || t(verdict.messageKey)
    }
  } catch (e) {
    // 1804（凭据被拒）与 1805（不可达）都走异常路径：后端把「不连通」当失败响应，
    // 而不是 `reachable: false` 的成功响应。
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    testing.value = false
  }
}

async function submit(): Promise<void> {
  if (!canSubmit.value) return

  submitting.value = true
  errorMessage.value = null
  try {
    const created = await addRepository({
      name: name.value.trim(),
      serverAddress: serverAddress.value.trim(),
      username: username.value.trim() || undefined,
      password: password.value || undefined,
      description: description.value.trim() || undefined
    })

    ElMessage({ type: 'success', message: t('repository.created', { name: created.name }) })
    emit('created', created)
    visible.value = false
  } catch (e) {
    // 1801（别名占用）/ 1802（地址占用）都要原样展示：用户需要知道自己撞的是哪一条
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('repository.form.title')"
    width="min(560px, 92vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent="submit">
      <el-form-item :label="t('repository.form.name')" required>
        <el-input
          v-model="name"
          :placeholder="t('repository.form.namePlaceholder')"
          :disabled="submitting"
          @input="invalidateVerdict"
        />
      </el-form-item>

      <el-form-item :label="t('repository.form.address')" required>
        <el-input
          v-model="serverAddress"
          :placeholder="t('repository.form.addressPlaceholder')"
          :disabled="submitting"
          @input="invalidateVerdict"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('repository.form.addressHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('repository.form.username')">
        <el-input
          v-model="username"
          :placeholder="t('repository.form.usernamePlaceholder')"
          :disabled="submitting"
          autocomplete="off"
          @input="invalidateVerdict"
        />
      </el-form-item>

      <el-form-item :label="t('repository.form.password')">
        <el-input
          v-model="password"
          type="password"
          show-password
          :placeholder="t('repository.form.passwordPlaceholder')"
          :disabled="submitting"
          autocomplete="new-password"
          @input="invalidateVerdict"
        />
        <p class="mt-1 text-xs text-content-muted">{{ t('repository.form.passwordHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('repository.form.description')">
        <el-input
          v-model="description"
          :placeholder="t('repository.form.descriptionPlaceholder')"
          :disabled="submitting"
        />
      </el-form-item>

      <el-alert
        v-if="accountPairError"
        class="mt-1"
        type="warning"
        :closable="false"
        :title="t('repository.form.error.accountPair')"
      />

      <el-alert
        v-if="testVerdict"
        class="mt-3"
        :type="testVerdict.type"
        :closable="false"
        :title="testVerdict.text"
      />

      <el-alert
        v-if="errorMessage"
        class="mt-3"
        type="error"
        :closable="false"
        :title="errorMessage"
      />
    </el-form>

    <template #footer>
      <div class="flex items-center justify-between gap-2">
        <!-- 测试放在左边：它是次要动作，不能与「保存」并列抢夺注意力 -->
        <el-button :loading="testing" :disabled="submitting" @click="runTest">
          {{ testing ? t('repository.form.testing') : t('repository.form.test') }}
        </el-button>
        <div class="flex items-center gap-2">
          <el-button :disabled="submitting" @click="visible = false">
            {{ t('common.cancel') }}
          </el-button>
          <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
            {{ submitting ? t('repository.form.submitting') : t('repository.form.submit') }}
          </el-button>
        </div>
      </div>
    </template>
  </el-dialog>
</template>
