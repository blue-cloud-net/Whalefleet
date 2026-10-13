<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'

const { t } = useI18n()
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-container-software" :title="t('nav.containers')" :subtitle="t('page.containers.placeholder')" narrow />

    <div class="app-card mt-6">
      <EmptyState icon="i-carbon-container-software" :description="t('page.containers.placeholder')" />
    </div>
  </div>
</template>