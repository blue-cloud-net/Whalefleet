<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { listAdminHosts } from '@/api/host'
import {
  assignHostRole,
  assignUserRole,
  listRoles,
  listUserRoleAssignments,
  revokeHostRole,
  revokeUserRole
} from '@/api/role'
import { ApiRequestError } from '@/api'
import { roleText } from '@/utils/roles'
import type { AdminHostSummary, AdminUserSummary, RoleSummary } from '@/types/api'

/**
 * 「角色分配」对话框（0.1.0）。
 *
 * ## 为什么是一张「全局一行 + 每台主机一行」的表
 * 授权是 **`(用户, 角色, 作用域)` 三元组**：同一个人可以在 A 机是 `operator`、
 * 在 B 机只是 `viewer`。把作用域与角色并排放在同一行，管理员一眼就能回答
 * 「他在哪台机器上算什么」——这比「先选主机再看角色」少一次点击，也少一次记错。
 *
 * ## 为什么全局分配单独一块而不是「主机选择器里的『全部』项」
 * 两者是**不同的事实**：全局分配贡献角色的全局权限（`user.*` / `role.*` /
 * `host.manage` / `audit.read`），而主机分配**忽略**这些权限。
 * 合在一个下拉里，管理员会把「给他在 prod-01 上分配 admin」误当成
 * 「让他成为系统管理员」——后者是一次静默提权，而前者只是一台机器上的操作权。
 *
 * ## 为什么保存时只提交差量
 * 后端只有 `assign` / `revoke` 两个动作，没有「覆盖式设置这个人的授权」。
 * 因此界面自己算出差集：新增的发 assign，去掉的发 revoke。
 * 撤销一条不存在的分配会得到 `1515`，所以**不能**对每台机器都无脑发一遍撤销。
 *
 * ## 为什么用 `roleId` 而不是角色名
 * 两个端点的请求体都接受主键或标识。列表里选中的行天然带主键，
 * 用它就不存在「名字大小写 / 前后空白」这类需要再规整的输入。
 */
const props = defineProps<{
  /** 目标用户；为 null 时不显示 */
  user: AdminUserSummary | null
}>()

const visible = defineModel<boolean>({ required: true })
const emit = defineEmits<{ saved: [] }>()

const { t } = useI18n()

const roles = ref<RoleSummary[]>([])
const hosts = ref<AdminHostSummary[]>([])
const loading = ref(false)
const submitting = ref(false)
const errorMessage = ref<string | null>(null)

/** 全局分配（对所有主机生效，只贡献全局权限）。 */
const globalRoleIds = ref<string[]>([])

/** 主机名 → 该主机上持有的角色主键。值缺失 / 空数组表示「在这台机器上没有角色」。 */
const hostRoleIds = ref<Record<string, string[]>>({})

/** 打开时的初始分配，用于算差集——对没有变化的行发请求是纯浪费（撤销还会撞 1515）。 */
const initialGlobal = ref<string[]>([])
const initialByHost = ref<Record<string, string[]>>({})

/** 角色下拉的选项标签：可读名称优先，回退标识（自定义角色可能没填可读名称）。 */
function roleOptionLabel(role: RoleSummary): string {
  const name = role.displayName?.trim()

  return name && name.length > 0 ? `${name}（${role.name}）` : role.name
}

const hasHosts = computed(() => hosts.value.length > 0)

/** 当前选择与初始状态是否不同；没有变化就没什么可提交的。 */
const dirty = computed(() => {
  const sameGlobal = sameSet(globalRoleIds.value, initialGlobal.value)
  if (!sameGlobal) return true

  return hosts.value.some(
    (host) => !sameSet(hostRoleIds.value[host.name] ?? [], initialByHost.value[host.name] ?? [])
  )
})

watch(visible, (open) => {
  if (!open) return

  void load()
})

/** 两个集合是否相同（顺序无关；界面上的顺序由用户点击决定，不该影响判定）。 */
function sameSet(left: readonly string[], right: readonly string[]): boolean {
  if (left.length !== right.length) return false

  return left.every((item) => right.includes(item))
}

/**
 * 载入角色、主机与该用户现有的分配。
 *
 * 三个请求并发：它们互不依赖，而串行会让对话框在白屏里多等两个来回。
 * 分配列表里带 `hostName`，因此不必逐台主机再查一次。
 */
async function load(): Promise<void> {
  if (props.user === null) return

  loading.value = true
  errorMessage.value = null

  try {
    const [roleList, hostList, assignmentList] = await Promise.all([
      listRoles(),
      listAdminHosts(),
      listUserRoleAssignments(props.user.id)
    ])

    roles.value = roleList
    hosts.value = hostList

    const global = assignmentList
      .filter((assignment) => assignment.hostId === null)
      .map((assignment) => assignment.roleId)

    const byHost: Record<string, string[]> = {}
    for (const host of hostList) {
      byHost[host.name] = assignmentList
        .filter((assignment) => assignment.hostName === host.name)
        .map((assignment) => assignment.roleId)
    }

    globalRoleIds.value = global
    hostRoleIds.value = byHost
    // 快照初始状态：只有「打开时就有的」才需要发撤销
    initialGlobal.value = [...global]
    initialByHost.value = Object.fromEntries(
      Object.entries(byHost).map(([name, ids]) => [name, [...ids]])
    )
  } catch (e) {
    errorMessage.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

/**
 * 提交差量：先补新增、再撤减少。
 *
 * **串行而不是并发**：某一步失败时能停在原地，用户知道「到哪一步为止已经生效」。
 * 并发下会出现「部分主机改了、部分没改」却说不清是哪几台，而重试会重复提交。
 * 先 assign 后 revoke 的顺序也有意为之——先建后删，中途失败不会留下「权限突然全没了」的状态。
 */
async function submit(): Promise<void> {
  if (submitting.value || props.user === null) return

  const user = props.user
  submitting.value = true
  errorMessage.value = null

  try {
    // ===== 全局作用域 =====
    for (const roleId of globalRoleIds.value) {
      if (!initialGlobal.value.includes(roleId)) {
        await assignUserRole(user.id, { roleId })
      }
    }

    for (const roleId of initialGlobal.value) {
      if (!globalRoleIds.value.includes(roleId)) {
        await revokeUserRole(user.id, { roleId, host: null })
      }
    }

    // ===== 主机作用域 =====
    for (const host of hosts.value) {
      const desired = hostRoleIds.value[host.name] ?? []
      const before = initialByHost.value[host.name] ?? []

      for (const roleId of desired) {
        if (!before.includes(roleId)) {
          await assignHostRole(host.name, { userId: user.id, roleId })
        }
      }

      for (const roleId of before) {
        if (!desired.includes(roleId)) {
          await revokeHostRole(host.name, { userId: user.id, roleId })
        }
      }
    }

    ElMessage({
      type: 'success',
      message: t('roleAssign.done', { name: user.username }),
      showClose: true
    })
    visible.value = false
    emit('saved')
  } catch (e) {
    errorMessage.value = e instanceof ApiRequestError ? e.message : String(e)
  } finally {
    submitting.value = false
  }
}

/** 把角色主键还原成展示名（`displayName（name）`，回退标识）。 */
function roleLabel(roleId: string): string {
  const role = roles.value.find((item) => item.id === roleId)

  if (role === undefined) return roleId

  return roleText(role, t)
}
</script>

<template>
  <el-dialog
    v-model="visible"
    :title="t('roleAssign.title')"
    width="min(760px, 94vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <p class="mb-3 text-sm text-content-body">
      {{ t('roleAssign.for', { name: user?.username ?? '—' }) }}
    </p>

    <div v-loading="loading" class="min-h-24">
      <el-alert
        v-if="errorMessage"
        class="mb-3"
        type="error"
        :closable="false"
        :title="errorMessage"
      />

      <template v-else>
        <el-alert
          class="mb-4"
          type="info"
          :closable="false"
          :title="t('roleAssign.model')"
          :description="t('roleAssign.globalHint')"
        />

        <div class="mb-4">
          <label class="mb-1 block text-sm font-medium text-content-strong">
            {{ t('roleAssign.global.title') }}
          </label>
          <el-select
            v-model="globalRoleIds"
            multiple
            clearable
            class="w-full"
            :disabled="submitting"
            :placeholder="t('roleAssign.global.placeholder')"
          >
            <el-option
              v-for="role in roles"
              :key="role.id"
              :label="roleOptionLabel(role)"
              :value="role.id"
            />
          </el-select>
        </div>

        <el-empty
          v-if="!loading && !hasHosts"
          :description="t('roleAssign.empty')"
          :image-size="64"
        />

        <template v-else>
          <div class="mb-1 text-sm font-medium text-content-strong">
            {{ t('roleAssign.host.title') }}
          </div>
          <p class="mb-2 text-xs text-content-muted">{{ t('roleAssign.hostHint') }}</p>

          <table class="w-full text-sm">
            <thead>
              <tr class="border-b border-line-subtle text-left text-xs text-content-muted">
                <th class="py-2 pr-3 font-medium">{{ t('roleAssign.col.host') }}</th>
                <th class="py-2 font-medium">{{ t('roleAssign.col.roles') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="host in hosts"
                :key="host.name"
                class="border-b border-line-subtle last:border-b-0"
              >
                <td class="w-48 py-2 pr-3 align-top">
                  <div class="font-medium text-content-strong">{{ host.name }}</div>
                  <div class="font-mono text-xs text-content-faint">{{ host.endpoint }}</div>
                </td>
                <td class="py-2 align-top">
                  <el-select
                    v-model="hostRoleIds[host.name]"
                    multiple
                    clearable
                    class="w-full"
                    :disabled="submitting"
                    :placeholder="t('roleAssign.none')"
                  >
                    <el-option
                      v-for="role in roles"
                      :key="role.id"
                      :label="roleOptionLabel(role)"
                      :value="role.id"
                    />
                  </el-select>
                </td>
              </tr>
            </tbody>
          </table>

          <!-- 全局已选角色的文字回显：下拉收起后看不到选了什么，而管理员需要复核 -->
          <p v-if="globalRoleIds.length > 0" class="mt-3 text-xs text-content-muted">
            {{ t('roleAssign.global.summary', { roles: globalRoleIds.map(roleLabel).join(' · ') }) }}
          </p>
        </template>
      </template>
    </div>

    <p class="mt-3 text-xs text-content-faint">{{ t('roleAssign.footnote') }}</p>

    <template #footer>
      <el-button :disabled="submitting" @click="visible = false">{{ t('common.cancel') }}</el-button>
      <el-button
        type="primary"
        :loading="submitting"
        :disabled="loading || !dirty"
        @click="submit"
      >
        {{ t('common.save') }}
      </el-button>
    </template>
  </el-dialog>
</template>
