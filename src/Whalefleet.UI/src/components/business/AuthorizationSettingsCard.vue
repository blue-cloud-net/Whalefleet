<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import SectionCard from '@/components/common/SectionCard.vue'
import {
  getAuthorizationSettings,
  updateAuthorizationSettings,
  type ContainerPermissionMode
} from '@/api/authorization-settings'
const { t } = useI18n()
const allowed = ref(false)
const mode = ref<ContainerPermissionMode>('inheritHost')
const saving = ref(false)
const error = ref('')
onMounted(async () => {
  try {
    mode.value = (await getAuthorizationSettings()).containerPermissionMode
    allowed.value = true
  } catch {
    allowed.value = false
  }
})
async function save() {
  saving.value = true
  error.value = ''
  try {
    await updateAuthorizationSettings({ containerPermissionMode: mode.value })
    ElMessage.success(t('containerRbac.saved'))
  } catch (e) {
    error.value = String(e)
  } finally {
    saving.value = false
  }
}
</script>
<template>
  <SectionCard v-if="allowed" :title="t('containerRbac.settings')">
    <p class="mb-4 text-sm text-content-body">
      {{ t('containerRbac.explanation') }}
    </p>
    <el-radio-group
      v-model="mode"
      class="flex flex-col items-start gap-3"
      :aria-label="t('containerRbac.settings')"
    >
      <el-radio value="inheritHost">{{ t('containerRbac.inheritHost') }}</el-radio>
      <el-radio value="requireAssignment">{{ t('containerRbac.requireAssignment') }}</el-radio>
    </el-radio-group>
    <p v-if="error" role="alert">{{ error }}</p>
    <el-button class="mt-4" type="primary" :loading="saving" @click="save">
      {{ t('common.save') }}
    </el-button>
  </SectionCard>
</template>
