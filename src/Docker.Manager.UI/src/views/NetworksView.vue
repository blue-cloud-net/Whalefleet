<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'

const { t } = useI18n()
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-network-3" :title="t('nav.networks')" :subtitle="t('page.networks.placeholder')" narrow />

    <div class="app-card mt-6">
      <EmptyState icon="i-carbon-network-3" :description="t('page.networks.placeholder')" />
    </div>
  </div>
</template>