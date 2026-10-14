<script setup lang="ts">
import { inject, onMounted, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useThemeStore } from '@/stores/theme'

const { locale } = useI18n()
const theme = useThemeStore()
const elLocale = inject<{ value: unknown }>('elementLocale', { value: null })

// 语言切换：<html lang="..."> 同步（可访问性 + 浏览器提示）
watch(locale, (val) => {
  document.documentElement.lang = val
})

onMounted(() => {
  document.documentElement.lang = locale.value
  theme.applyToDocument()
})
</script>

<template>
  <el-config-provider :locale="(elLocale?.value ?? undefined) as any">
    <RouterView />
  </el-config-provider>
</template>