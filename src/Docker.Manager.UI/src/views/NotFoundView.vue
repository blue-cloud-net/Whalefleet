<script setup lang="ts">
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
</script>

<template>
  <div class="min-h-screen flex flex-col items-center justify-center gap-5 p-6 relative overflow-hidden">
    <div
      class="absolute inset-0 -z-10 bg-[radial-gradient(ellipse_60%_50%_at_50%_40%,rgb(var(--brand-500)/0.1),transparent_70%)]"
      aria-hidden="true"
    />

    <span
      class="w-20 h-20 rounded-2xl grid place-items-center text-5xl text-content-faint bg-surface-sunken ring-1 ring-line-subtle"
    >
      <span class="i-carbon-warning-alt" />
    </span>

    <div class="text-center">
      <h1 class="text-lg font-semibold text-content-body">{{ t('notfound.title') }}</h1>
      <p class="mt-1 text-xs text-content-faint font-mono">
        {{ $route.fullPath }}
      </p>
    </div>

    <el-button type="primary" @click="$router.push('/')">
      <span class="i-carbon-arrow-left text-base" />
      {{ t('notfound.back') }}
    </el-button>
  </div>
</template>