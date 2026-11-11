<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import {
  createAlertRule,
  createNotificationChannel,
  listAlertRules,
  listNotificationChannels,
  removeAlertRule,
  removeNotificationChannel,
  setAlertRuleEnabled,
  testNotificationChannel,
  updateAlertRule,
  updateNotificationChannel
} from '@/api/alert'
import { useAuthStore } from '@/stores/auth'
import type {
  AlertKind,
  AlertRule,
  AlertSeverity,
  NotificationChannel,
  NotificationChannelKind,
  SaveAlertRuleRequest,
  SaveNotificationChannelRequest
} from '@/types/api'

const { t } = useI18n()
const auth = useAuthStore()

const activeTab = ref<'rules' | 'channels'>('rules')
const rules = ref<AlertRule[]>([])
const channels = ref<NotificationChannel[]>([])
const loading = ref(false)
const canWrite = computed(() => auth.hasPermission('alert.write'))

const alertKinds: AlertKind[] = [
  'host-offline',
  'container-cpu',
  'container-memory',
  'container-network',
  'login-failure-surge'
]

const severities: AlertSeverity[] = ['info', 'warning', 'critical']
const channelKinds: NotificationChannelKind[] = ['webhook', 'email']

const ruleDialog = ref(false)
const editingRuleId = ref<string | null>(null)
const ruleForm = reactive({
  kind: 'container-cpu' as AlertKind,
  host: '',
  enabled: false,
  infoThreshold: 70 as number | null,
  warningThreshold: 85 as number | null,
  criticalThreshold: 95 as number | null,
  severity: 'warning' as AlertSeverity,
  consecutiveSamples: 3,
  windowSeconds: null as number | null,
  cooldownSeconds: 300,
  channelIds: [] as string[]
})

const channelDialog = ref(false)
const editingChannelId = ref<string | null>(null)
const channelForm = reactive({
  name: '',
  kind: 'webhook' as NotificationChannelKind,
  enabled: true,
  webhookUrl: '',
  webhookBearerToken: '',
  emailRecipients: [] as string[]
})

const emailText = ref('')

const isThresholdKind = computed(() => ruleForm.kind !== 'host-offline')

async function load(): Promise<void> {
  loading.value = true
  try {
    const [loadedRules, loadedChannels] = await Promise.all([
      listAlertRules(),
      listNotificationChannels()
    ])
    rules.value = loadedRules
    channels.value = loadedChannels
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  } finally {
    loading.value = false
  }
}

function openCreateRule(): void {
  editingRuleId.value = null
  Object.assign(ruleForm, {
    kind: 'container-cpu',
    host: '',
    enabled: false,
    infoThreshold: 70,
    warningThreshold: 85,
    criticalThreshold: 95,
    severity: 'warning',
    consecutiveSamples: 3,
    windowSeconds: null,
    cooldownSeconds: 300,
    channelIds: []
  })
  ruleDialog.value = true
}

function openEditRule(rule: AlertRule): void {
  editingRuleId.value = rule.id
  Object.assign(ruleForm, {
    kind: rule.kind,
    host: rule.hostName ?? '',
    enabled: rule.enabled,
    infoThreshold: rule.infoThreshold,
    warningThreshold: rule.warningThreshold,
    criticalThreshold: rule.criticalThreshold,
    severity: rule.severity,
    consecutiveSamples: rule.consecutiveSamples,
    windowSeconds: rule.windowSeconds,
    cooldownSeconds: rule.cooldownSeconds,
    channelIds: [...rule.channelIds]
  })
  ruleDialog.value = true
}

async function saveRule(): Promise<void> {
  try {
    const payload: SaveAlertRuleRequest = {
      id: editingRuleId.value ?? undefined,
      host: ruleForm.host.trim() || null,
      kind: ruleForm.kind,
      enabled: ruleForm.enabled,
      infoThreshold: isThresholdKind.value ? ruleForm.infoThreshold : null,
      warningThreshold: isThresholdKind.value ? ruleForm.warningThreshold : null,
      criticalThreshold: isThresholdKind.value ? ruleForm.criticalThreshold : null,
      severity: ruleForm.severity,
      consecutiveSamples: ruleForm.consecutiveSamples,
      windowSeconds: ruleForm.kind === 'login-failure-surge' ? ruleForm.windowSeconds : null,
      cooldownSeconds: ruleForm.cooldownSeconds,
      channelIds: [...ruleForm.channelIds]
    }

    if (editingRuleId.value) {
      await updateAlertRule(payload)
    } else {
      await createAlertRule(payload)
    }

    ruleDialog.value = false
    ElMessage.success(t('common.saved'))
    await load()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

async function toggleRule(rule: AlertRule, enabled: boolean): Promise<void> {
  try {
    await setAlertRuleEnabled(rule.id, enabled)
    rule.enabled = enabled
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
    await load()
  }
}

async function removeRule(rule: AlertRule): Promise<void> {
  try {
    await ElMessageBox.confirm(t('alert.confirm.remove'), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  await removeAlertRule(rule.id)
  await load()
}

function openCreateChannel(): void {
  editingChannelId.value = null
  Object.assign(channelForm, {
    name: '',
    kind: 'webhook',
    enabled: true,
    webhookUrl: '',
    webhookBearerToken: '',
    emailRecipients: []
  })
  emailText.value = ''
  channelDialog.value = true
}

function openEditChannel(channel: NotificationChannel): void {
  editingChannelId.value = channel.id
  Object.assign(channelForm, {
    name: channel.name,
    kind: channel.kind,
    enabled: channel.enabled,
    webhookUrl: channel.webhookUrl ?? '',
    webhookBearerToken: '',
    emailRecipients: channel.emailRecipients ?? []
  })
  emailText.value = (channel.emailRecipients ?? []).join(', ')
  channelDialog.value = true
}

async function saveChannel(): Promise<void> {
  try {
    const payload: SaveNotificationChannelRequest = {
      id: editingChannelId.value ?? undefined,
      name: channelForm.name,
      kind: channelForm.kind,
      enabled: channelForm.enabled,
      webhookUrl: channelForm.kind === 'webhook' ? channelForm.webhookUrl.trim() : null,
      webhookBearerToken:
        channelForm.kind === 'webhook' && channelForm.webhookBearerToken.trim().length > 0
          ? channelForm.webhookBearerToken.trim()
          : null,
      emailRecipients:
        channelForm.kind === 'email'
          ? emailText.value
              .split(',')
              .map((value) => value.trim())
              .filter((value) => value.length > 0)
          : null
    }

    if (editingChannelId.value) {
      await updateNotificationChannel(payload)
    } else {
      await createNotificationChannel(payload)
    }

    channelDialog.value = false
    ElMessage.success(t('common.saved'))
    await load()
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

async function testChannel(channel: NotificationChannel): Promise<void> {
  try {
    await testNotificationChannel(channel.id)
    ElMessage.success(t('alert.channel.testOk'))
  } catch (e) {
    ElMessage.error(e instanceof Error ? e.message : String(e))
  }
}

async function removeChannel(channel: NotificationChannel): Promise<void> {
  try {
    await ElMessageBox.confirm(t('alert.channel.confirmRemove'), t('common.delete'), {
      type: 'warning'
    })
  } catch {
    return
  }
  await removeNotificationChannel(channel.id)
  await load()
}

onMounted(load)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-warning-alt"
      :title="t('nav.alerts')"
      :subtitle="t('page.alerts.subtitle')"
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
      </template>
    </PageHeader>

    <SectionCard>
      <el-tabs v-model="activeTab">
        <el-tab-pane :label="t('alert.tab.rules')" name="rules">
          <div class="flex justify-end mb-3">
            <el-button v-if="canWrite" type="primary" @click="openCreateRule">
              {{ t('alert.rule.add') }}
            </el-button>
          </div>

          <EmptyState v-if="rules.length === 0 && !loading" icon="i-carbon-warning-alt" :title="t('alert.rule.empty')" />

          <el-table v-else v-loading="loading" :data="rules" row-key="id">
            <el-table-column :label="t('alert.rule.kind')" min-width="180">
              <template #default="{ row }">{{ t(`alert.kind.${row.kind}`) }}</template>
            </el-table-column>
            <el-table-column :label="t('alert.rule.scope')" width="160">
              <template #default="{ row }">{{ row.hostName || t('alert.rule.global') }}</template>
            </el-table-column>
            <el-table-column :label="t('alert.rule.thresholds')" min-width="200">
              <template #default="{ row }">
                <span v-if="row.infoThreshold !== null">
                  {{ row.infoThreshold }} / {{ row.warningThreshold }} / {{ row.criticalThreshold }}
                </span>
                <span v-else>{{ t(`alert.severity.${row.severity}`) }}</span>
              </template>
            </el-table-column>
            <el-table-column :label="t('alert.rule.cooldown')" width="110">
              <template #default="{ row }">{{ row.cooldownSeconds }}s</template>
            </el-table-column>
            <el-table-column :label="t('alert.rule.enabled')" width="100">
              <template #default="{ row }">
                <el-switch
                  :model-value="row.enabled"
                  :disabled="!canWrite"
                  @change="(value: string | number | boolean) => toggleRule(row, Boolean(value))"
                />
              </template>
            </el-table-column>
            <el-table-column v-if="canWrite" :label="t('common.actions')" width="160" align="right">
              <template #default="{ row }">
                <el-button text size="small" @click="openEditRule(row)">{{ t('common.edit') }}</el-button>
                <el-button text type="danger" size="small" @click="removeRule(row)">{{ t('common.delete') }}</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane :label="t('alert.tab.channels')" name="channels">
          <div class="flex justify-end mb-3">
            <el-button v-if="canWrite" type="primary" @click="openCreateChannel">
              {{ t('alert.channel.add') }}
            </el-button>
          </div>

          <EmptyState v-if="channels.length === 0 && !loading" icon="i-carbon-send-alt" :title="t('alert.channel.empty')" />

          <el-table v-else v-loading="loading" :data="channels" row-key="id">
            <el-table-column prop="name" :label="t('alert.channel.name')" min-width="180" />
            <el-table-column :label="t('alert.channel.kind')" width="120">
              <template #default="{ row }">{{ t(`alert.channelKind.${row.kind}`) }}</template>
            </el-table-column>
            <el-table-column :label="t('alert.channel.target')" min-width="260">
              <template #default="{ row }">
                {{ row.kind === 'webhook' ? row.webhookUrl : (row.emailRecipients ?? []).join(', ') }}
              </template>
            </el-table-column>
            <el-table-column :label="t('alert.rule.enabled')" width="100">
              <template #default="{ row }">
                <el-tag :type="row.enabled ? 'success' : 'info'" size="small">
                  {{ row.enabled ? t('common.enabled') : t('common.disabled') }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column v-if="canWrite" :label="t('common.actions')" width="240" align="right">
              <template #default="{ row }">
                <el-button text size="small" @click="testChannel(row)">{{ t('alert.channel.test') }}</el-button>
                <el-button text size="small" @click="openEditChannel(row)">{{ t('common.edit') }}</el-button>
                <el-button text type="danger" size="small" @click="removeChannel(row)">{{ t('common.delete') }}</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </SectionCard>

    <el-dialog v-model="ruleDialog" :title="editingRuleId ? t('alert.rule.edit') : t('alert.rule.add')" width="560px">
      <el-form label-position="top">
        <el-form-item :label="t('alert.rule.kind')">
          <el-select v-model="ruleForm.kind" class="w-full">
            <el-option v-for="kind in alertKinds" :key="kind" :label="t(`alert.kind.${kind}`)" :value="kind" />
          </el-select>
        </el-form-item>
        <el-form-item :label="t('alert.rule.scope')">
          <el-input v-model="ruleForm.host" clearable :placeholder="t('alert.rule.global')" />
        </el-form-item>
        <el-form-item :label="t('alert.rule.enabled')">
          <el-switch v-model="ruleForm.enabled" />
        </el-form-item>

        <template v-if="isThresholdKind">
          <div class="grid grid-cols-3 gap-3">
            <el-form-item :label="t('alert.severity.info')">
              <el-input-number v-model="ruleForm.infoThreshold" :min="0" class="w-full" />
            </el-form-item>
            <el-form-item :label="t('alert.severity.warning')">
              <el-input-number v-model="ruleForm.warningThreshold" :min="0" class="w-full" />
            </el-form-item>
            <el-form-item :label="t('alert.severity.critical')">
              <el-input-number v-model="ruleForm.criticalThreshold" :min="0" class="w-full" />
            </el-form-item>
          </div>
        </template>
        <el-form-item v-else :label="t('alert.rule.severity')">
          <el-select v-model="ruleForm.severity" class="w-full">
            <el-option v-for="value in severities" :key="value" :label="t(`alert.severity.${value}`)" :value="value" />
          </el-select>
        </el-form-item>

        <div class="grid grid-cols-2 gap-3">
          <el-form-item :label="t('alert.rule.consecutive')">
            <el-input-number v-model="ruleForm.consecutiveSamples" :min="1" :max="100" class="w-full" />
          </el-form-item>
          <el-form-item :label="t('alert.rule.cooldown')">
            <el-input-number v-model="ruleForm.cooldownSeconds" :min="0" :max="86400" class="w-full" />
          </el-form-item>
        </div>

        <el-form-item v-if="ruleForm.kind === 'login-failure-surge'" :label="t('alert.rule.window')">
          <el-input-number v-model="ruleForm.windowSeconds" :min="1" :max="86400" class="w-full" />
        </el-form-item>

        <el-form-item :label="t('alert.rule.channels')">
          <el-select v-model="ruleForm.channelIds" multiple class="w-full">
            <el-option v-for="channel in channels" :key="channel.id" :label="channel.name" :value="channel.id" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="ruleDialog = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" @click="saveRule">{{ t('common.save') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="channelDialog" :title="editingChannelId ? t('alert.channel.edit') : t('alert.channel.add')" width="520px">
      <el-form label-position="top">
        <el-form-item :label="t('alert.channel.name')">
          <el-input v-model="channelForm.name" />
        </el-form-item>
        <el-form-item :label="t('alert.channel.kind')">
          <el-select v-model="channelForm.kind" class="w-full">
            <el-option v-for="kind in channelKinds" :key="kind" :label="t(`alert.channelKind.${kind}`)" :value="kind" />
          </el-select>
        </el-form-item>
        <el-form-item :label="t('alert.rule.enabled')">
          <el-switch v-model="channelForm.enabled" />
        </el-form-item>

        <template v-if="channelForm.kind === 'webhook'">
          <el-form-item :label="t('alert.channel.webhookUrl')">
            <el-input v-model="channelForm.webhookUrl" placeholder="https://..." />
          </el-form-item>
          <el-form-item :label="t('alert.channel.bearerToken')">
            <el-input v-model="channelForm.webhookBearerToken" show-password :placeholder="t('alert.channel.tokenHint')" />
          </el-form-item>
        </template>
        <el-form-item v-else :label="t('alert.channel.recipients')">
          <el-input v-model="emailText" :placeholder="t('alert.channel.recipientsHint')" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="channelDialog = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" @click="saveChannel">{{ t('common.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>
