<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import PlaceholderSection from '@/components/common/PlaceholderSection.vue'

/** 镜像页预告（后端 0.0.10 / 前端 0.0.12）。 */
const { t } = useI18n()

const capabilities = computed(() => [
  t('page.images.cap1'),
  t('page.images.cap2'),
  t('page.images.cap3'),
  t('page.images.cap4')
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-image"
      :title="t('nav.images')"
      :subtitle="t('page.images.title')"
      narrow
    />

    <div class="app-section">
      <PlaceholderSection
        icon="i-carbon-image"
        :title="t('state.notImplemented')"
        version="0.0.12"
        :capabilities="capabilities"
        :note="t('page.images.note')"
      />
    </div>
  </div>
</template>