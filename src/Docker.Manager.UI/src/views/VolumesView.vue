<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'

const { t } = useI18n()
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-data-volume" :title="t('nav.volumes')" :subtitle="t('page.volumes.placeholder')" narrow />

    <div class="app-card mt-6">
      <EmptyState icon="i-carbon-data-volume" :description="t('page.volumes.placeholder')" />
    </div>
  </div>
</template>