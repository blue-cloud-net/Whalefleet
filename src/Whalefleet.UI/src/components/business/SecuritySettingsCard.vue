<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import SectionCard from '@/components/common/SectionCard.vue'
import { useAuthStore } from '@/stores/auth'
import { getSecuritySettings, updateSecuritySettings } from '@/api/security-settings'
import type { SecurityPolicyOptions } from '@/types/api'

/**
 * 密码策略卡片（0.13.0）。
 *
 * ## 为什么只对 `user.write` 显示
 * 它是**实例级**设置：一份管所有人的密码强度与有效期。
 * 后端由 `user.write` 把守，前端提前隐藏只是为了让非管理员不去点一个必然 403 的保存按钮。
 *
 * ## 为什么保存后不重新拉取
 * 接口原样返回保存后的策略，直接用返回值覆盖即可——
 * 多一次往返只会让「保存成功」与「界面更新」之间多一个可失败的环节。
 */
const { t } = useI18n()
const auth = useAuthStore()

const visible = ref(false)
const loading = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)

const form = ref<SecurityPolicyOptions>({
  minLength: 10,
  requireLetter: true,
  requireDigitOrSymbol: true,
  passwordExpiryDays: 0,
  mustChangeOnFirstLogin: true
})

async function load(): Promise<void> {
  loading.value = true
  try {
    form.value = await getSecuritySettings()
    visible.value = true
    error.value = null
  } catch {
    // 无权 / 后端不可用：整张卡片不渲染，而不是显示一堆禁用控件。
    visible.value = false
  } finally {
    loading.value = false
  }
}

async function save(): Promise<void> {
  saving.value = true
  error.value = null
  try {
    form.value = await updateSecuritySettings(form.value)
    ElMessage.success(t('common.saved'))
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<template>
  <SectionCard v-if="visible && auth.hasPermission('user.write')" :title="t('settings.security.title')">
    <p class="mb-4 text-sm text-content-body">
      {{ t('settings.security.description') }}
    </p>

    <div v-loading="loading" class="space-y-4">
      <div class="flex items-center justify-between gap-4 flex-wrap">
        <span class="text-sm text-content-body">{{ t('settings.security.minLength') }}</span>
        <el-input-number v-model="form.minLength" :min="1" :max="1024" size="small" />
      </div>

      <div class="flex items-center justify-between gap-4 flex-wrap">
        <span class="text-sm text-content-body">{{ t('settings.security.requireLetter') }}</span>
        <el-switch v-model="form.requireLetter" />
      </div>

      <div class="flex items-center justify-between gap-4 flex-wrap">
        <span class="text-sm text-content-body">{{ t('settings.security.requireDigitOrSymbol') }}</span>
        <el-switch v-model="form.requireDigitOrSymbol" />
      </div>

      <div class="flex items-center justify-between gap-4 flex-wrap">
        <div class="min-w-0">
          <span class="text-sm text-content-body">{{ t('settings.security.expiry') }}</span>
          <p class="text-xs text-content-muted">{{ t('settings.security.expiryHint') }}</p>
        </div>
        <el-input-number v-model="form.passwordExpiryDays" :min="0" :max="3650" size="small" />
      </div>

      <div class="flex items-center justify-between gap-4 flex-wrap">
        <span class="text-sm text-content-body">{{ t('settings.security.mustChangeOnFirstLogin') }}</span>
        <el-switch v-model="form.mustChangeOnFirstLogin" />
      </div>

      <p v-if="error" role="alert" class="text-sm text-status-danger">{{ error }}</p>

      <div class="flex justify-end">
        <el-button type="primary" :loading="saving" @click="save">
          {{ t('common.save') }}
        </el-button>
      </div>
    </div>
  </SectionCard>
</template>
