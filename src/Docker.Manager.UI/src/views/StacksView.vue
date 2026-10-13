<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'

const { t } = useI18n()
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-layers" :title="t('nav.stacks')" :subtitle="t('stacks.subtitle')" narrow />

    <div class="app-card mt-6">
      <EmptyState icon="i-carbon-layers" :description="t('page.stacks.placeholder')">
        <p class="max-w-md text-xs text-gray-400 dark:text-gray-500">
          {{ t('stacks.hint') }}
        </p>
      </EmptyState>
    </div>
  </div>
</template>