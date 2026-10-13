<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'

const { t } = useI18n()
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-data-center" :title="t('nav.hosts')" :subtitle="t('page.hosts.placeholder')" narrow />

    <div class="app-card mt-6">
      <EmptyState icon="i-carbon-data-center" :description="t('page.hosts.placeholder')" />
    </div>
  </div>
</template>