<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import EmptyState from '@/components/common/EmptyState.vue'
import PageHeader from '@/components/common/PageHeader.vue'

const { t } = useI18n()
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-user-multiple" :title="t('nav.users')" :subtitle="t('page.users.placeholder')" narrow />

    <div class="app-card mt-6">
      <EmptyState icon="i-carbon-user-multiple" :description="t('page.users.placeholder')" />
    </div>
  </div>
</template>