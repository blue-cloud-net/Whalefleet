<script setup lang="ts">
import { computed, h, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import RegistryFormDialog from '@/components/business/RegistryFormDialog.vue'
import { listRegistries, removeRegistry, testRegistry } from '@/api/registry'
import { ApiRequestError } from '@/api'
import { describeRegistryTest } from '@/utils/registry-display'
import { formatDateTime } from '@/utils/datetime'
import { useAuthStore } from '@/stores/auth'
import type { RegistrySummary } from '@/types/api'

/**
 * 私有 Registry 配置页（0.4.0，**全局作用域**）。
 *
 * ## 为什么没有搜索框 / 分页 / 批量条
 * Registry 配置是「一个内网 Harbor、一个 GHSA 镜像仓库」这种量级的东西，
 * 通常个位数。给它配分页与批量删除，用户会以为这里应该有几十上百条。
 * 地址多了之后确实需要搜索，但那时再加——现在加只会让页面看起来空。
 *
 * ## 为什么把「账号」列拆成两个徽标
 * `hasCredentials` 回答的是「这条配置带不带账号」，但用户真正关心的是
 * 「推送时会用哪个账号」。匿名配置必须**显式写「匿名」**：空单元格会被读成
 * 「后端没返回」，而它的实际含义是「这条配置不会发送任何凭据」。
 */
const { t } = useI18n()
const auth = useAuthStore()

const registries = ref<RegistrySummary[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const formVisible = ref(false)
/** 正在测试的配置 id：让那一行的按钮转圈，而不是整页转圈 */
const testingIds = ref<Set<string>>(new Set())
/** 正在删除的配置 id */
const removingIds = ref<Set<string>>(new Set())

/** 能否改动（新增 / 删除 / 测试都要 `registry.write`：test 会拿凭据去连地址） */
const canWrite = computed(() => auth.hasPermission('registry.write'))

const isEmpty = computed(() => !loading.value && error.value === null && registries.value.length === 0)

async function load(): Promise<void> {
  loading.value = true
  try {
    registries.value = await listRegistries()
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function markBusy(target: typeof testingIds, id: string, busy: boolean): void {
  const next = new Set(target.value)
  if (busy) next.add(id)
  else next.delete(id)
  target.value = next
}

/**
 * 测试已保存的配置。
 *
 * 结论用 `ElMessage` 而不是行内展开：一条配置的测试结果是**瞬时**的，
 * 而表格行里塞一段会随刷新消失的说明，比弹一条消息更容易被忽略。
 * 三种结论的色调必须不同（成功 / 匿名 / 不可达），否则用户会把
 * 「连通但没带凭据」也当成成功。
 */
async function onTest(registry: RegistrySummary): Promise<void> {
  markBusy(testingIds, registry.id, true)
  try {
    const result = await testRegistry({ id: registry.id })
    const verdict = describeRegistryTest(result)

    ElMessage({
      type: verdict.tone === 'ok' ? 'success' : verdict.tone === 'warn' ? 'warning' : 'error',
      message: `${registry.name}：${result.message || t(verdict.messageKey)}`,
      showClose: true
    })
  } catch (e) {
    // 1804 / 1805 走异常路径（后端把「不连通」当失败响应），文案原样展示
    ElMessage({ type: 'error', message: e instanceof Error ? e.message : String(e), showClose: true })
  } finally {
    markBusy(testingIds, registry.id, false)
  }
}

/**
 * 删除配置。
 *
 * 确认框里要说明**影响面**：删掉后引用该地址的镜像将无法推送凭据
 * （推送会退化成匿名，公开仓库仍能成，私有仓库则 1804/1807）。
 * 只说「删除后不可恢复」，用户不知道自己在拆什么。
 */
async function onRemove(registry: RegistrySummary): Promise<void> {
  try {
    await ElMessageBox.confirm(
      h('div', { class: 'flex flex-col gap-1' }, [
        h('span', t('registry.confirm.remove.message', { name: registry.name })),
        h('span', { class: 'text-status-warn text-xs' }, t('registry.confirm.remove.impact'))
      ]),
      t('registry.confirm.remove.title'),
      {
        confirmButtonText: t('common.delete'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    // 用户取消：不是错误
    return
  }

  markBusy(removingIds, registry.id, true)
  try {
    await removeRegistry(registry.id)
    ElMessage({ type: 'success', message: t('registry.removed', { name: registry.name }) })
    await load()
  } finally {
    markBusy(removingIds, registry.id, false)
  }
}

async function onCreated(): Promise<void> {
  formVisible.value = false
  await load()
}

onMounted(load)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-container-registry"
      :title="t('nav.registries')"
      :subtitle="t('page.registries.subtitle')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <el-button v-if="canWrite" type="primary" @click="formVisible = true">
          {{ t('registry.action.add') }}
        </el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <span class="text-xs text-content-muted">{{ t('registry.total', { total: registries.length }) }}</span>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <template v-else>
        <el-table v-loading="loading" :data="registries" :row-key="(row: RegistrySummary) => row.id" empty-text="">
          <el-table-column :label="t('registry.col.name')" min-width="180">
            <template #default="{ row }">
              <div class="min-w-0">
                <div class="app-truncate font-medium text-content-strong">{{ row.name }}</div>
                <div v-if="row.description" class="app-truncate text-xs text-content-faint">
                  {{ row.description }}
                </div>
              </div>
            </template>
          </el-table-column>

          <el-table-column :label="t('registry.col.address')" min-width="200">
            <template #default="{ row }">
              <span class="font-mono text-xs text-content-body">{{ row.serverAddress }}</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('registry.col.account')" min-width="160">
            <template #default="{ row }">
              <!-- 匿名必须显式写出来：空单元格会被读成「后端没返回」， -->
              <!-- 而实际含义是「推送时不发送任何凭据」，两者处置完全不同 -->
              <span v-if="row.hasCredentials" class="text-xs text-content-body">
                {{ row.username }}
              </span>
              <span v-else class="text-xs text-content-faint">{{ t('registry.badge.anonymous') }}</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('registry.col.created')" width="170">
            <template #default="{ row }">
              <span class="text-xs text-content-muted">{{ formatDateTime(row.createdAt) }}</span>
            </template>
          </el-table-column>

          <el-table-column :label="t('container.col.actions')" :width="canWrite ? 92 : 44">
            <template #default="{ row }">
              <div class="flex items-center">
                <IconActionButton
                  v-if="canWrite"
                  icon="i-carbon-data-check"
                  :label="t('registry.action.test')"
                  :loading="testingIds.has(row.id)"
                  @click="onTest(row)"
                />
                <IconActionButton
                  v-if="canWrite"
                  icon="i-carbon-trash-can"
                  :label="t('common.delete')"
                  tone="danger"
                  :loading="removingIds.has(row.id)"
                  @click="onRemove(row)"
                />
              </div>
            </template>
          </el-table-column>
        </el-table>

        <EmptyState
          v-if="isEmpty"
          icon="i-carbon-container-registry"
          :description="t('registry.empty.title')"
        >
          <template #default>
            <el-button v-if="canWrite" type="primary" @click="formVisible = true">
              {{ t('registry.action.add') }}
            </el-button>
          </template>
        </EmptyState>
      </template>
    </SectionCard>

    <p class="mt-3 text-xs text-content-faint">{{ t('registry.footnote') }}</p>

    <RegistryFormDialog v-model="formVisible" @created="onCreated" />
  </div>
</template>
