<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'

const { t } = useI18n()
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-image" :title="t('nav.images')" :subtitle="t('page.images.placeholder')" narrow />

    <div class="app-card mt-6">
      <EmptyState icon="i-carbon-image" :description="t('page.images.placeholder')" />
    </div>
  </div>
</template>