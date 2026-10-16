<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { createUser } from '@/api/user'
import { listRoles } from '@/api/role'
import { roleText } from '@/utils/roles'
import type { RoleSummary } from '@/types/api'

/**
 * 新建用户对话框（0.0.13）。
 *
 * ## 角色为什么是下拉而不是自由输入
 * `admin/roles/list` 返回**全部角色**（含管理员运行时新建的自定义角色），
 * 因此下拉可以给真实存在的选项。早期没有角色列表端点，只能「内置名单 + 自由输入」，
 * 结果是拼错一个名字就得到一句「角色不存在」——现在这个口子不再需要。
 *
 * 留空 = 用后端的**默认角色**（由角色自身的 `isDefault` 决定），
 * 这让「不纠结角色」成为默认行为，而不是逼管理员先想清楚。
 *
 * ## 密码为什么在前端也校验一次
 * 后端才是权威（`1202` 弱口令），但让用户在提交前就看到要求，比提交后收到一个错误码更省事。
 * 前端校验与后端 `PasswordPolicy` 同规则：≥10 字符、含字母、含数字或符号。
 * 若两边规则漂移，后端会兜住——前端这层只是提示，不是防线。
 */
const visible = defineModel<boolean>({ required: true })
const emit = defineEmits<{ created: [] }>()

const { t } = useI18n()

const username = ref('')
const password = ref('')
const roleChoice = ref('')
const roles = ref<RoleSummary[]>([])
const submitting = ref(false)
const errorMessage = ref<string | null>(null)

/** 表单内联校验：只在用户真的填了之后才报，避免一打开就满屏红字。 */
const passwordIssue = computed(() => {
  if (password.value.length === 0) return null

  return isStrongPassword(password.value) ? null : t('user.form.passwordWeak')
})

const usernameIssue = computed(() => {
  const value = username.value.trim()
  if (value.length === 0) return null

  return /^[A-Za-z0-9._-]{3,32}$/.test(value) ? null : t('user.form.usernameInvalid')
})

const canSubmit = computed(
  () =>
    !submitting.value &&
    usernameIssue.value === null &&
    passwordIssue.value === null &&
    username.value.trim().length > 0 &&
    password.value.length > 0
)

watch(visible, (open) => {
  if (!open) return

  username.value = ''
  password.value = ''
  roleChoice.value = ''
  errorMessage.value = null

  void loadRoles()
})

/**
 * 拉角色选项。
 *
 * 失败时**不阻断建用户**：角色是可选字段，留空即用默认角色，
 * 而列表拉不到只应该让选项变少，不该让「建一个用默认角色的用户」也做不成。
 * 因此这里只清空选项、不报错。
 */
async function loadRoles(): Promise<void> {
  try {
    roles.value = await listRoles()
  } catch {
    roles.value = []
  }
}

function isStrongPassword(value: string): boolean {
  return value.length >= 10 && /[A-Za-z]/.test(value) && /[0-9]|[^A-Za-z0-9]/.test(value)
}

async function submit(): Promise<void> {
  if (!canSubmit.value) return

  submitting.value = true
  errorMessage.value = null
  try {
    const created = await createUser({
      username: username.value.trim(),
      password: password.value,
      // 空串表示「用默认角色」——后端会取默认角色，因此这里刻意不填空值占位
      role: roleChoice.value.length > 0 ? roleChoice.value : undefined
    })

    ElMessage({ type: 'success', message: t('user.create.done', { name: created.username }) })
    emit('created')
  } catch (e) {
    // 1200（用户名已存在）/ 1202（弱口令）都落在这里，文案由错误码映射给出
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
    :title="t('user.create.title')"
    width="min(520px, 92vw)"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
  >
    <el-form label-position="top" @submit.prevent>
      <el-form-item :label="t('user.form.username')" required>
        <el-input
          v-model="username"
          :placeholder="t('user.form.usernamePlaceholder')"
          :disabled="submitting"
        />
        <p v-if="usernameIssue" class="mt-1 text-xs text-status-down">{{ usernameIssue }}</p>
      </el-form-item>

      <el-form-item :label="t('user.form.password')" required>
        <el-input
          v-model="password"
          type="password"
          show-password
          autocomplete="new-password"
          :placeholder="t('user.form.passwordPlaceholder')"
          :disabled="submitting"
        />
        <p v-if="passwordIssue" class="mt-1 text-xs text-status-down">{{ passwordIssue }}</p>
        <p v-else class="mt-1 text-xs text-content-muted">{{ t('user.form.passwordHint') }}</p>
      </el-form-item>

      <el-form-item :label="t('user.form.role')">
        <el-select v-model="roleChoice" class="w-full" :disabled="submitting">
          <!-- 空值 = 用后端默认角色：让「不纠结角色」成为默认行为 -->
          <el-option :label="t('user.form.roleDefault')" value="" />
          <el-option
            v-for="role in roles"
            :key="role.id"
            :label="roleText(role, t)"
            :value="role.name"
          />
        </el-select>
        <p class="mt-1 text-xs text-content-muted">{{ t('user.form.roleHint') }}</p>
      </el-form-item>

      <el-alert
        v-if="errorMessage"
        class="mt-3"
        type="error"
        :closable="false"
        :title="errorMessage"
      />
    </el-form>

    <template #footer>
      <el-button :disabled="submitting" @click="close">{{ t('common.cancel') }}</el-button>
      <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
        {{ t('user.create.submit') }}
      </el-button>
    </template>
  </el-dialog>
</template>
