<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { AppLocale } from '@/locales'

defineProps<{ modelValue: AppLocale }>()
const emit = defineEmits<{ 'update:modelValue': [value: AppLocale] }>()

const { locale } = useI18n()

const LABELS: Record<AppLocale, string> = {
  'zh-CN': '中',
  'en-US': 'EN'
}

function toggle() {
  const next: AppLocale = locale.value === 'zh-CN' ? 'en-US' : 'zh-CN'
  emit('update:modelValue', next)
}
</script>

<template>
  <el-button
    text
    class="!px-2 !w-9 text-xs font-semibold text-content-muted hover:text-brand-600 dark:hover:text-brand-400 transition-colors"
    :title="LABELS[modelValue]"
    :aria-label="`switch to ${modelValue === 'zh-CN' ? 'en-US' : 'zh-CN'}`"
    @click="toggle"
  >
    {{ LABELS[modelValue] }}
  </el-button>
</template>