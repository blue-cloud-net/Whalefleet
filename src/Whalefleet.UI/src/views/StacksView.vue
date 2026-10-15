<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import PlaceholderSection from '@/components/common/PlaceholderSection.vue'

/** 编排页预告（整块排在 0.7.0）。 */
const { t } = useI18n()

const capabilities = computed(() => [
  t('page.stacks.cap1'),
  t('page.stacks.cap2'),
  t('page.stacks.cap3'),
  t('page.stacks.cap4')
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-layers"
      :title="t('nav.stacks')"
      :subtitle="t('page.stacks.title')"
      narrow
    />

    <div class="app-section">
      <PlaceholderSection
        icon="i-carbon-layers"
        :title="t('state.notImplemented')"
        version="0.7.0"
        :capabilities="capabilities"
        :note="t('page.stacks.note')"
      />
    </div>
  </div>
</template>