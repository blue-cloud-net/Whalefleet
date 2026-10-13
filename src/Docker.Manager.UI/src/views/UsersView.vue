<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import PlaceholderSection from '@/components/common/PlaceholderSection.vue'

/** 用户管理页预告（admin 专用，0.0.8）。 */
const { t } = useI18n()

const capabilities = computed(() => [
  t('page.users.cap1'),
  t('page.users.cap2'),
  t('page.users.cap3'),
  t('page.users.cap4')
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-user-multiple"
      :title="t('nav.users')"
      :subtitle="t('page.users.title')"
      narrow
    />

    <div class="app-section">
      <PlaceholderSection
        icon="i-carbon-user-multiple"
        :title="t('state.notImplemented')"
        version="0.0.8"
        :capabilities="capabilities"
        :note="t('page.users.note')"
      />
    </div>
  </div>
</template>