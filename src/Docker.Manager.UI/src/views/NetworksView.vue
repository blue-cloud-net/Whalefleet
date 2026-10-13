<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import PlaceholderSection from '@/components/common/PlaceholderSection.vue'

/** 网络页预告（整块排在 0.2.0）。 */
const { t } = useI18n()

const capabilities = computed(() => [
  t('page.networks.cap1'),
  t('page.networks.cap2'),
  t('page.networks.cap3')
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-network-3"
      :title="t('nav.networks')"
      :subtitle="t('page.networks.title')"
      narrow
    />

    <div class="app-section">
      <PlaceholderSection
        icon="i-carbon-network-3"
        :title="t('state.notImplemented')"
        version="0.2.0"
        :capabilities="capabilities"
        :note="t('page.networks.note')"
      />
    </div>
  </div>
</template>