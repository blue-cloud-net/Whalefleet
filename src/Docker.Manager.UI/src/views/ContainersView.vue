<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import PlaceholderSection from '@/components/common/PlaceholderSection.vue'

/**
 * 容器页预告（后端 0.0.9 / 前端 0.0.11）。
 *
 * 页头保留一句「这页将来负责什么」，主体用 `PlaceholderSection` 明确
 * 「尚未实现 + 排期 + 将提供哪些能力」，而不是留一张空白卡片。
 */
const { t } = useI18n()

const capabilities = computed(() => [
  t('page.containers.cap1'),
  t('page.containers.cap2'),
  t('page.containers.cap3'),
  t('page.containers.cap4')
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-container-software"
      :title="t('nav.containers')"
      :subtitle="t('page.containers.title')"
      narrow
    />

    <div class="app-section">
      <PlaceholderSection
        icon="i-carbon-container-software"
        :title="t('state.notImplemented')"
        version="0.0.11"
        :capabilities="capabilities"
        :note="t('page.containers.note')"
      />
    </div>
  </div>
</template>