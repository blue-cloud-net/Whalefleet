<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import PlaceholderSection from '@/components/common/PlaceholderSection.vue'

/** 数据卷页预告（整块排在 0.2.0）。 */
const { t } = useI18n()

const capabilities = computed(() => [
  t('page.volumes.cap1'),
  t('page.volumes.cap2'),
  t('page.volumes.cap3')
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-data-volume"
      :title="t('nav.volumes')"
      :subtitle="t('page.volumes.title')"
      narrow
    />

    <div class="app-section">
      <PlaceholderSection
        icon="i-carbon-data-volume"
        :title="t('state.notImplemented')"
        version="0.2.0"
        :capabilities="capabilities"
        :note="t('page.volumes.note')"
      />
    </div>
  </div>
</template>