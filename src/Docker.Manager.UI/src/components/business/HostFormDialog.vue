<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { createHost, testHostConnection, updateHost } from '@/api/host'
import { assignHostGroup } from '@/api/host-group'
import type { AdminHostSummary, HostAuthKind, TlsConfigInput } from '@/types/api'

/**
 * 新建 / 编辑主机对话框（0.0.13）。
 *
 * ## 一个组件管两种模式，因为两者只差三个字段
 * 差别是：创建要 `name`（且不可改），编辑才有 `enabled` 与「清空凭据」。
 * 拆成两个组件会让凭据区域的校验与拼装复制一遍——而那段恰好是最容易写错的地方
 * （三个 TLS 路径必填、TLS 与 Basic 互斥、明文端点不能带 TLS）。
 *
 * ## 「测试连通性」为什么在表单里而不是列表行上
 * 后端的 `admin/hosts/test` 收的是**一份配置**（type + endpoint + 凭据），不是 `hostId`——
 * 它测的正是「还没保存的那份配置」。列表里也没有凭据内容（只有 `authKind`），
 * 拿不到凭据就没法忠实复现那台主机的连接条件，测出来的结论会骗人。
 * 因此：**表单里测当前填写的内容**（忠实），列表里显示后端探活结果 `reachable`（已保存的状态）。
 *
 * ## `reachable` 与「测试结果」是两件事
 * 列表上的连通状态来自后端探活（`reachable`，`null` = 未探测）；这里的测试结果只对应当前表单。
 * 保存不探活（写操作不该被网络超时拖住），因此保存后列表里的状态会变成「未探测」——
 * 这是刻意行为，界面如实显示。
 */
const props = defineProps<{
  /** 编辑目标；`null` 表示新建 */
  host: AdminHostSummary | null
  /** 可选分组（用于归属下拉） */
  groups: { id: string; name: string }[]
}>()

const visible = defineModel<boolean>({ required: true })
const emit = defineEmits<{ saved: []; removed: [] }>()

const { t } = useI18n()

const isEdit = computed(() => props.host !== null)

const name = ref('')
const type = ref<'socket' | 'tcp'>('socket')
const endpoint = ref('')
const authKind = ref<HostAuthKind>('none')
const enabled = ref(true)
/**
 * 分组下拉的值。
 *
 * 用**空串**表示「未分组」而不是 `null`：EP 的 `el-option` 不接受 `null` 作为 `value`，
 * 会在控制台刷出一串 prop 类型警告。空串是它接受的类型，语义也在提交时显式转回 null。
 */
const groupId = ref('')

const tls = ref<TlsConfigInput>({
  caCertificatePath: '',
  clientCertificatePath: '',
  clientKeyPath: '',
  clientKeyPassword: ''
})
const basicUsername = ref('')
const basicPassword = ref('')

const submitting = ref(false)
const testing = ref(false)
/** 测试结论：true 可连 / false 连不上 / null 未测 */
const testResult = ref<boolean | null>(null)
const errorMessage = ref<string | null>(null)

/** 端点形态与连接方式必须一致，否则后端返回 1301（而且错得很隐晦） */
const endpointLooksWrong = computed(() => {
  const value = endpoint.value.trim()
  if (value.length === 0) return false

  return type.value === 'tcp' ? !/^tcp:\/\/|^https:\/\//.test(value) : !value.startsWith('/')
})

/** TLS 三项路径全必填：少一项等于接受任意服务端证书或根本连不上 */
const tlsIncomplete = computed(
  () =>
    authKind.value === 'tls' &&
    (tls.value.caCertificatePath.trim().length === 0 ||
      tls.value.clientCertificatePath.trim().length === 0 ||
      tls.value.clientKeyPath.trim().length === 0)
)

const basicIncomplete = computed(
  () => authKind.value === 'basic' && basicUsername.value.trim().length === 0
)

const canSubmit = computed(
  () =>
    !submitting.value &&
    (isEdit.value || name.value.trim().length > 0) &&
    endpoint.value.trim().length > 0 &&
    !endpointLooksWrong.value &&
    !tlsIncomplete.value &&
    !basicIncomplete.value
)

watch(visible, (open) => {
  if (!open) return

  errorMessage.value = null
  testResult.value = null
  testResult.value = null

  if (props.host !== null) {
    name.value = props.host.name
    type.value = props.host.type
    endpoint.value = props.host.endpoint
    authKind.value = props.host.authKind
    enabled.value = props.host.enabled
    groupId.value = props.host.groupId ?? ''
    // 凭据内容**不返回**给前端（后端只给 authKind），因此编辑时留空表示「不改凭据」
    tls.value = {
      caCertificatePath: '',
      clientCertificatePath: '',
      clientKeyPath: '',
      clientKeyPassword: ''
    }
    basicUsername.value = ''
    basicPassword.value = ''
    return
  }

  name.value = ''
  type.value = 'socket'
  endpoint.value = '/var/run/docker.sock'
  authKind.value = 'none'
  enabled.value = true
  groupId.value = ''
  tls.value = {
    caCertificatePath: '',
    clientCertificatePath: '',
    clientKeyPath: '',
    clientKeyPassword: ''
  }
  basicUsername.value = ''
  basicPassword.value = ''
})

/** 按当前表单拼出凭据部分；`clearCredentials` 只在编辑时且切回无认证时才需要。 */
function credentialPayload(): {
  tlsConfig?: TlsConfigInput
  basicAuth?: { username: string; password: string }
  clearCredentials?: boolean
} {
  if (authKind.value === 'none') {
    // 编辑态：原来有凭据、现在选「无认证」→ 必须显式清空。
    // JSON 分不清「没给 tlsConfig」与「tlsConfig: null」，没有这个开关就答不出「改回明文」，
    // 而残留的 TLS 凭据会被后端直接拒绝（1301），用户会卡在无法自救的配置里。
    return isEdit.value && props.host?.authKind !== 'none' ? { clearCredentials: true } : {}
  }

  if (authKind.value === 'tls') {
    return {
      tlsConfig: {
        caCertificatePath: tls.value.caCertificatePath.trim(),
        clientCertificatePath: tls.value.clientCertificatePath.trim(),
        clientKeyPath: tls.value.clientKeyPath.trim(),
        clientKeyPassword: tls.value.clientKeyPassword?.trim() || undefined
      }
    }
  }

  return {
    basicAuth: { username: basicUsername.value.trim(), password: basicPassword.value }
  }
}

/**
 * 编辑时凭据留空 = 不改。这一条必须显式判断，否则「只改端点」会把凭据一起清掉。
 */
function hasCredentialInput(): boolean {
  if (authKind.value === 'none') return false

  return authKind.value === 'tls' ? !tlsIncomplete.value : basicUsername.value.trim().length > 0
}

async function onTest(): Promise<void> {
  testing.value = true
  testResult.value = null
  errorMessage.value = null
  try {
    // 测的是**未保存**的配置：因此走 test 端点而不是拿 hostId 去探活
    testResult.value = await testHostConnection({
      type: type.value,
      endpoint: endpoint.value.trim(),
      ...credentialPayload()
    })
  } catch (e) {
    // 字段非法（1000）走异常；「连不上」则是 200 + false，会落到上面的分支
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
    if (isEdit.value && props.host !== null) {
      const hostId = props.host.name

      await updateHost(hostId, {
        endpoint: endpoint.value.trim(),
        enabled: enabled.value,
        ...(hasCredentialInput() ? credentialPayload() : {}),
        // 切回无认证时必须显式下发，不论上面是否带了凭据
        ...(authKind.value === 'none' && props.host.authKind !== 'none'
          ? { clearCredentials: true }
          : {})
      })

      // 归属单独一条请求：它有自己的端点（为了能表达「移出分组」）
      if (groupId.value !== (props.host.groupId ?? '')) {
        await assignHostGroup(hostId, groupId.value === '' ? null : groupId.value)
      }
    } else {
      const created = await createHost({
        name: name.value.trim(),
        type: type.value,
        endpoint: endpoint.value.trim(),
        ...credentialPayload()
      })

      if (groupId.value !== '') {
        await assignHostGroup(created.name, groupId.value)
      }
    }

    ElMessage({ type: 'success', message: t('host.form.saved') })
    emit('saved')
  } catch (e) {
    errorMessage.value = e instanceof Error ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="isEdit ? t('host.form.editTitle') : t('host.form.createTitle')"
    width="min(640px, 94vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent>
      <el-form-item :label="t('host.form.name')" :required="!isEdit">
        <el-input
          v-model="name"
          :disabled="isEdit || submitting"
          :placeholder="t('host.form.namePlaceholder')"
        />
        <p class="mt-1 text-xs text-content-muted">
          {{ isEdit ? t('host.form.nameImmutable') : t('host.form.nameHint') }}
        </p>
      </el-form-item>

      <el-form-item :label="t('host.form.type')" required>
        <el-radio-group v-model="type" :disabled="submitting">
          <el-radio-button value="socket">{{ t('host.type.socket') }}</el-radio-button>
          <el-radio-button value="tcp">{{ t('host.type.tcp') }}</el-radio-button>
        </el-radio-group>
      </el-form-item>

      <el-form-item :label="t('host.form.endpoint')" required>
        <el-input
          v-model="endpoint"
          :disabled="submitting"
          :placeholder="type === 'socket' ? '/var/run/docker.sock' : 'tcp://10.0.0.5:2375'"
        />
        <p v-if="endpointLooksWrong" class="mt-1 text-xs text-status-down">
          {{ type === 'socket' ? t('host.form.endpointSocketHint') : t('host.form.endpointTcpHint') }}
        </p>
      </el-form-item>

      <el-form-item :label="t('host.form.auth')">
        <el-select v-model="authKind" class="w-full" :disabled="submitting">
          <el-option :label="t('host.auth.none')" value="none" />
          <el-option :label="t('host.auth.tls')" value="tls" />
          <el-option :label="t('host.auth.basic')" value="basic" />
        </el-select>
        <p class="mt-1 text-xs text-content-muted">
          {{ isEdit ? t('host.form.authEditHint') : t('host.form.authHint') }}
        </p>
      </el-form-item>

      <template v-if="authKind === 'tls'">
        <el-form-item :label="t('host.form.caCert')" required>
          <el-input v-model="tls.caCertificatePath" :disabled="submitting" />
        </el-form-item>
        <el-form-item :label="t('host.form.clientCert')" required>
          <el-input v-model="tls.clientCertificatePath" :disabled="submitting" />
        </el-form-item>
        <el-form-item :label="t('host.form.clientKey')" required>
          <el-input v-model="tls.clientKeyPath" :disabled="submitting" />
        </el-form-item>
        <el-form-item :label="t('host.form.clientKeyPassword')">
          <el-input v-model="tls.clientKeyPassword" type="password" show-password :disabled="submitting" />
        </el-form-item>
        <p class="-mt-2 mb-3 text-xs text-content-muted">{{ t('host.form.tlsNote') }}</p>
      </template>

      <template v-if="authKind === 'basic'">
        <el-form-item :label="t('host.form.basicUser')" required>
          <el-input v-model="basicUsername" :disabled="submitting" />
        </el-form-item>
        <el-form-item :label="t('host.form.basicPassword')">
          <el-input v-model="basicPassword" type="password" show-password :disabled="submitting" />
        </el-form-item>
      </template>

      <el-form-item v-if="groups.length > 0" :label="t('host.form.group')">
        <el-select v-model="groupId" clearable class="w-full" :disabled="submitting">
          <el-option :label="t('host.group.ungrouped')" value="" />
          <el-option
            v-for="group in groups"
            :key="group.id"
            :label="group.name"
            :value="group.id"
          />
        </el-select>
      </el-form-item>

      <el-form-item v-if="isEdit" :label="t('host.form.enabled')">
        <el-switch v-model="enabled" :disabled="submitting" />
      </el-form-item>

      <el-alert v-if="errorMessage" class="mb-3" type="error" :closable="false" :title="errorMessage" />
      <el-alert
        v-else-if="testResult === true"
        class="mb-3"
        type="success"
        :closable="false"
        :title="t('host.form.testOk')"
      />
      <el-alert
        v-else-if="testResult === false"
        class="mb-3"
        type="warning"
        :closable="false"
        :title="t('host.form.testFailed')"
      />
    </el-form>

    <template #footer>
      <el-button :disabled="submitting" @click="visible = false">{{ t('common.cancel') }}</el-button>
      <el-button :loading="testing" :disabled="submitting" @click="onTest">
        {{ t('host.action.test') }}
      </el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ isEdit ? t('common.save') : t('common.confirm') }}
      </el-button>
    </template>
  </el-dialog>
</template>
