<script setup lang="ts">
import { computed, h, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import IconActionButton from '@/components/common/IconActionButton.vue'
import ListPagination from '@/components/common/ListPagination.vue'
import { createNetwork, inspectNetwork, listNetworks, removeNetwork } from '@/api/network'
import { ApiRequestError } from '@/api'
import {
  isBuiltInNetwork,
  isNetworkRemovable,
  isValidGateway,
  isValidResourceName,
  isValidSubnet,
  networkSubnetLabel
} from '@/utils/resource-display'
import { PAGE_SIZE_ALL, resolvePageSize, usePreferencesStore } from '@/stores/preferences'
import { TIP_DELAY_MS } from '@/utils/tooltip'
import type { NetworkDetail, NetworkSummary } from '@/types/api'

/**
 * 网络列表页（0.2.0，分页于 0.14.x 补齐）。
 *
 * ## 三处与镜像页刻意不同
 * 1. **分页与容器 / 镜像同一套口径**：服务端内存切片，`pageIndex` 从 0 开始，
 *    每页条数来自用户偏好、上限 100（超出由后端夹紧并回显）。
 * 2. **没有「容器数」列**：Docker 的列表接口不返回 `Containers`，只有详情接口返回。
 *    补一个恒为 0 的列会把正在用的网络显示成「0 个容器」——
 *    真实接入情况在详情抽屉里，那才是权威数据。
 * 3. **删除没有 `force`**：Docker 的删除网络接口不提供该参数，
 *    有容器接入就必然失败。界面据实说明「先断开」，不给一个按了也没用的开关。
 * 4. **内置网络禁删**：`bridge` / `host` / `none`（老版本还有 `null`）。
 */
const { t } = useI18n()
const route = useRoute()

const networks = ref<NetworkSummary[]>([])
const total = ref(0)
const pageIndex = ref(0)
/**
 * 每页条数来自用户偏好（设置页可改），`PAGE_SIZE_ALL`（0）表示「全部」。
 *
 * 类型是 `number` 而不是 store 的 `PageSize` 联合：后端回显的 `result.pageSize`
 * 是任意 number，写回时需要接住它。「全部」靠与 `PAGE_SIZE_ALL` 相等来判断。
 */
const pageSize = ref<number>(usePreferencesStore().pageSize)
const loading = ref(false)
const error = ref<string | null>(null)
const busyNames = ref<Set<string>>(new Set())

/** 创建对话框 */
const createVisible = ref(false)
const createBusy = ref(false)
const createFormRef = ref<FormInstance>()
const createForm = ref({ name: '', driver: '', subnet: '', gateway: '', attachable: false })

/** 详情抽屉 */
const detailVisible = ref(false)
const detail = ref<NetworkDetail | null>(null)
const detailBusy = ref(false)

/** URL 上的 hostId。**以它为准**而不是 store.currentId：深链进入时 store 可能还没被守卫写入。 */
const hostId = computed(() => String(route.params.hostId ?? ''))

const isEmpty = computed(() => !loading.value && error.value === null && networks.value.length === 0)

/**
 * 总页数。
 *
 * 必须用 `resolvePageSize` 而不是直接 `total / pageSize`：
 * `pageSize` 可能是 `PAGE_SIZE_ALL`（0，除以它得 Infinity），也可能被上限夹紧。
 */
const pageCount = computed(() =>
  Math.max(1, Math.ceil(total.value / resolvePageSize(pageSize.value, total.value)))
)

/**
 * 列表查询参数。
 *
 * ⚠️ 「全部」必须在这里换算成真实条数再发出去：0 传出去会被后端
 * `Math.Clamp(pageSize, 1, 100)` 夹成 1，用户选了「全部」却只看到一行。
 */
function listParams() {
  return {
    pageIndex: pageIndex.value,
    pageSize: resolvePageSize(pageSize.value, total.value)
  }
}

/**
 * 创建表单的校验规则。
 *
 * ⚠️ **前端校验只为即时反馈**，后端那道永远在，且规则逐条对齐
 * （名字同一条正则、网段必须是带前缀长度的 CIDR）。
 */
const createRules = computed<FormRules>(() => ({
  name: [
    { required: true, message: t('network.form.nameRequired'), trigger: 'blur' },
    {
      validator: (_rule, value: string, callback) => {
        callback(
          isValidResourceName(String(value ?? '').trim())
            ? undefined
            : new Error(t('network.form.nameInvalid'))
        )
      },
      trigger: 'blur'
    }
  ],
  subnet: [
    {
      validator: (_rule, value: string, callback) => {
        callback(
          isValidSubnet(String(value ?? '').trim())
            ? undefined
            : new Error(t('network.form.subnetInvalid'))
        )
      },
      trigger: 'blur'
    }
  ],
  gateway: [
    {
      validator: (_rule, value: string, callback) => {
        callback(
          isValidGateway(String(value ?? '').trim())
            ? undefined
            : new Error(t('network.form.gatewayInvalid'))
        )
      },
      trigger: 'blur'
    }
  ]
}))

async function load(): Promise<void> {
  if (!hostId.value) return

  loading.value = true
  try {
    const result = await listNetworks(hostId.value, listParams())

    // 删光当前页（如把最后一页的最后一个网络删了）时自动回退一页：
    // 否则用户会停在一片空白上，且「共 N 个」与空表同时显示。
    if (result.items.length === 0 && result.pageIndex > 0 && result.total > 0) {
      pageIndex.value = result.pageIndex - 1
      await load()
      return
    }

    networks.value = result.items
    total.value = result.total
    // ⚠️ 以**服务端回显**的页码 / 页数为准：请求写 pageSize=5000 时后端会夹到 100。
    pageIndex.value = result.pageIndex
    // 「全部」不回写：回显的是换算后的真实条数，写回去就把「全部」降级成了具体页大小。
    if (pageSize.value !== PAGE_SIZE_ALL) pageSize.value = result.pageSize
    error.value = null
  } catch (e) {
    error.value = e instanceof ApiRequestError ? e.message : t('network.loadFailed')
  } finally {
    loading.value = false
  }
}

async function goToPage(next: number): Promise<void> {
  if (next < 0 || next >= pageCount.value) return

  pageIndex.value = next
  await load()
}

/**
 * 换每页条数。
 *
 * ⚠️ 这里**只更新本地状态、不发请求**：`ListPagination` 在改页大小时会同时抛
 * `update:pageSize` 与 `update:pageIndex`(0)，两个事件各接一个处理器。
 * 若这里也 load()，一次改页大小就会打两遍请求。真正的查询统一由 `goToPage(0)` 触发。
 */
function onPageSizeChange(size: number): void {
  usePreferencesStore().setPageSize(size)
  pageSize.value = size
}

function openCreate(): void {
  createForm.value = {
    name: '',
    driver: '',
    subnet: '',
    gateway: '',
    attachable: false
  }
  createVisible.value = true
}

async function submitCreate(): Promise<void> {
  const valid = await createFormRef.value?.validate().catch(() => false)
  if (valid === false) return

  createBusy.value = true
  try {
    const driver = createForm.value.driver.trim()
    const subnet = createForm.value.subnet.trim()
    const gateway = createForm.value.gateway.trim()

    const created = await createNetwork(hostId.value, {
      name: createForm.value.name.trim(),
      ...(driver.length > 0 ? { driver } : {}),
      ...(subnet.length > 0 ? { subnet } : {}),
      ...(gateway.length > 0 ? { gateway } : {}),
      attachable: createForm.value.attachable
    })

    createVisible.value = false
    await load()

    ElMessage({ type: 'success', message: t('network.created', { name: created.name }) })
  } catch (e) {
    // 1404 有两条路径（同名 / 子网重叠），后端保留了守护进程给的原因文本，
    // 直接把 message 展示出来比另写一套文案有用。
    if (e instanceof ApiRequestError) {
      ElMessage({ type: 'error', message: e.message })
    }
  } finally {
    createBusy.value = false
  }
}

/**
 * 删除网络。
 *
 * 内置网络不给删除入口；仍有容器接入时后端报 1404 并在消息里点明「还有几个容器」——
 * 直接展示那句话，用户就知道该先去断开谁。
 */
async function removeOne(network: NetworkSummary): Promise<void> {
  if (!isNetworkRemovable(network)) return

  try {
    await ElMessageBox.confirm(
      h('span', t('network.remove.confirm.message', { name: network.name })),
      t('network.remove.confirm.title'),
      {
        confirmButtonText: t('common.delete'),
        cancelButtonText: t('common.cancel'),
        type: 'warning'
      }
    )
  } catch {
    return
  }

  busyNames.value = new Set(busyNames.value).add(network.name)

  try {
    await removeNetwork(hostId.value, network.id)
    await load()
    ElMessage({ type: 'success', message: t('network.removed', { name: network.name }) })
  } catch (e) {
    if (e instanceof ApiRequestError) {
      ElMessage({ type: 'error', message: e.message })
    }
  } finally {
    const next = new Set(busyNames.value)
    next.delete(network.name)
    busyNames.value = next
  }
}

async function openDetail(network: NetworkSummary): Promise<void> {
  detailVisible.value = true
  detailBusy.value = true
  detail.value = null

  try {
    detail.value = await inspectNetwork(hostId.value, network.id)
  } catch (e) {
    detailVisible.value = false
    if (e instanceof ApiRequestError) {
      ElMessage({ type: 'error', message: e.message })
    }
  } finally {
    detailBusy.value = false
  }
}

watch(
  () => route.params.hostId,
  () => {
    error.value = null
    // 换主机必须回到第 0 页：停在上一台主机的第 3 页，换过去很可能是一片空白
    pageIndex.value = 0
    void load()
  },
  { immediate: true }
)
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-network-3"
      :title="t('nav.networks')"
      :title-tip="t('page.networks.subtitle')"
      narrow
    >
      <template #actions>
        <el-button :loading="loading" @click="load">{{ t('common.refresh') }}</el-button>
        <el-button type="primary" @click="openCreate">{{ t('network.action.create') }}</el-button>
      </template>
    </PageHeader>

    <SectionCard density="flush">
      <template #actions>
        <span class="text-xs text-content-muted">
          {{ t('network.total', { total }) }}
        </span>
      </template>

      <div v-if="error" class="p-6 flex flex-col items-center gap-3">
        <p class="text-sm text-content-muted">{{ error }}</p>
        <el-button @click="load">{{ t('common.retry') }}</el-button>
      </div>

      <EmptyState
        v-else-if="isEmpty"
        icon="i-carbon-network-3"
        :title="t('network.empty')"
        :description="t('network.emptyHint')"
      />

      <template v-else>
        <el-table
          v-loading="loading"
          :data="networks"
          :row-key="(row: NetworkSummary) => row.id"
        >
        <el-table-column :label="t('network.col.name')" min-width="220">
          <template #default="{ row }">
            <!--
              名称即详情入口，用 `<button>` 才可 Tab / 可读屏。
              ⚠️ 悬浮提示**始终**给：提示在不在与内容长短无关，
              读者才能把它当成可靠的入口。
            -->
            <el-tooltip :content="row.name" placement="top-start" :show-after="TIP_DELAY_MS">
              <button
                type="button"
                class="app-truncate block text-left font-medium hover:text-brand-500 hover:underline"
                :class="isBuiltInNetwork(row) ? 'text-content-strong' : 'text-brand-600'"
                @click="openDetail(row)"
              >
                {{ row.name }}
              </button>
            </el-tooltip>
            <el-tooltip :content="row.id" placement="top-start" :show-after="TIP_DELAY_MS">
              <div class="app-truncate font-mono text-xs text-content-faint">
                {{ row.id }}
              </div>
            </el-tooltip>
          </template>
        </el-table-column>

        <el-table-column :label="t('network.col.driver')" width="120">
          <template #default="{ row }">
            <span class="text-xs">{{ row.driver }}</span>
          </template>
        </el-table-column>

        <el-table-column :label="t('network.col.subnet')" min-width="180">
          <template #default="{ row }">
            <span class="font-mono text-xs text-content-muted">
              {{ networkSubnetLabel(row) }}
            </span>
          </template>
        </el-table-column>

        <el-table-column :label="t('network.col.flags')" width="150">
          <template #default="{ row }">
            <div class="flex flex-wrap gap-1">
              <el-tag v-if="isBuiltInNetwork(row)" size="small" type="info">
                {{ t('network.tag.builtIn') }}
              </el-tag>
              <el-tag v-if="row.internal" size="small" type="warning">
                {{ t('network.tag.internal') }}
              </el-tag>
              <el-tag v-if="row.attachable" size="small" type="success">
                {{ t('network.tag.attachable') }}
              </el-tag>
            </div>
          </template>
        </el-table-column>

        <el-table-column :label="t('common.actions')" width="96" align="right">
          <template #default="{ row }">
            <IconActionButton
              icon="i-carbon-information"
              :title="t('network.action.detail')"
              @click="openDetail(row)"
            />
            <!-- 内置网络不给删除入口：按了也必然 1404 -->
            <IconActionButton
              v-if="isNetworkRemovable(row)"
              icon="i-carbon-trash-can"
              :title="t('network.action.remove')"
              danger
              :loading="busyNames.has(row.name)"
              @click="removeOne(row)"
            />
          </template>
        </el-table-column>
        </el-table>

        <!-- 分页条始终显示：翻到后面的页删光条目后仍能回到上一页，而不是对着一片空白猜。 -->
        <ListPagination
          :total="total"
          :page-index="pageIndex"
          :page-size="pageSize"
          :disabled="loading"
          @update:page-index="goToPage"
          @update:page-size="onPageSizeChange"
        />
      </template>
    </SectionCard>

    <!-- 创建对话框 -->
    <el-dialog v-model="createVisible" :title="t('network.form.title')" width="480px">
      <el-form ref="createFormRef" :model="createForm" :rules="createRules" label-width="96px">
        <el-form-item :label="t('network.form.name')" prop="name">
          <el-input v-model="createForm.name" :placeholder="t('network.form.namePlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('network.form.driver')">
          <el-input v-model="createForm.driver" :placeholder="t('network.form.driverPlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('network.form.subnet')" prop="subnet">
          <el-input v-model="createForm.subnet" :placeholder="t('network.form.subnetPlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('network.form.gateway')" prop="gateway">
          <el-input v-model="createForm.gateway" :placeholder="t('network.form.gatewayPlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('network.form.attachable')">
          <el-switch v-model="createForm.attachable" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="createVisible = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="createBusy" @click="submitCreate">
          {{ t('common.create') }}
        </el-button>
      </template>
    </el-dialog>

    <!-- 详情抽屉：容器接入情况只在这里能看到 -->
    <el-drawer v-model="detailVisible" :title="detail?.name ?? t('network.detail.title')" size="520px">
      <div v-loading="detailBusy">
        <template v-if="detail">
          <el-descriptions :column="1" border>
            <el-descriptions-item :label="t('network.col.driver')">
              {{ detail.driver }}
            </el-descriptions-item>
            <el-descriptions-item :label="t('network.detail.scope')">
              {{ detail.scope }}
            </el-descriptions-item>
            <el-descriptions-item :label="t('network.detail.ipVersion')">
              <span class="font-mono text-xs">
                IPv4 {{ detail.enableIPv4 ? t('network.detail.on') : t('network.detail.off') }} ·
                IPv6 {{ detail.enableIPv6 ? t('network.detail.on') : t('network.detail.off') }}
              </span>
            </el-descriptions-item>
            <el-descriptions-item :label="t('network.detail.id')">
              <span class="font-mono text-xs">{{ detail.id }}</span>
            </el-descriptions-item>
          </el-descriptions>

          <div class="mt-4">
            <p class="mb-2 text-sm font-medium">{{ t('network.detail.ipam') }}</p>
            <p v-if="detail.ipam.length === 0" class="text-xs text-content-muted">
              {{ t('network.detail.noIpam') }}
            </p>
            <el-descriptions v-else :column="1" border>
              <el-descriptions-item
                v-for="(entry, index) in detail.ipam"
                :key="index"
                :label="t('network.detail.pool', { index: index + 1 })"
              >
                <div class="font-mono text-xs">
                  <div>{{ t('network.detail.subnetLabel') }}: {{ entry.subnet ?? '—' }}</div>
                  <div>{{ t('network.detail.gatewayLabel') }}: {{ entry.gateway ?? '—' }}</div>
                  <div>{{ t('network.detail.ipRangeLabel') }}: {{ entry.ipRange ?? '—' }}</div>
                </div>
              </el-descriptions-item>
            </el-descriptions>
          </div>

          <div class="mt-4">
            <p class="mb-2 text-sm font-medium">{{ t('network.detail.containers') }}</p>
            <p v-if="detail.containers.length === 0" class="text-xs text-content-muted">
              {{ t('network.detail.noContainers') }}
            </p>
            <el-table v-else :data="detail.containers" size="small">
              <el-table-column :label="t('network.detail.containerName')" min-width="160">
                <template #default="{ row }">
                  <span class="text-xs">{{ row.name }}</span>
                </template>
              </el-table-column>
              <el-table-column :label="t('network.detail.ipv4')" min-width="160">
                <template #default="{ row }">
                  <span class="font-mono text-xs">{{ row.ipv4Address ?? '—' }}</span>
                </template>
              </el-table-column>
              <el-table-column :label="t('network.detail.mac')" min-width="150">
                <template #default="{ row }">
                  <span class="font-mono text-xs text-content-muted">
                    {{ row.macAddress ?? '—' }}
                  </span>
                </template>
              </el-table-column>
            </el-table>
          </div>

          <div v-if="Object.keys(detail.options).length > 0" class="mt-4">
            <p class="mb-2 text-sm font-medium">{{ t('network.detail.options') }}</p>
            <el-descriptions :column="1" border>
              <el-descriptions-item v-for="(value, key) in detail.options" :key="key" :label="key">
                <span class="font-mono text-xs">{{ value }}</span>
              </el-descriptions-item>
            </el-descriptions>
          </div>
        </template>
      </div>
    </el-drawer>
  </div>
</template>