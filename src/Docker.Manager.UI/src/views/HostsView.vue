<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import PageHeader from '@/components/common/PageHeader.vue'
import PlaceholderSection from '@/components/common/PlaceholderSection.vue'

/**
 * 主机管理页预告（admin 专用，0.0.8）。
 *
 * 与其他占位页不同，这一页会真的去连主机——所以「连通性测试」被单列为一项能力，
 * 它对应后端的临时客户端 + Ping（不经客户端池）那条实现。
 */
const { t } = useI18n()

const capabilities = computed(() => [
  t('page.hosts.cap1'),
  t('page.hosts.cap2'),
  t('page.hosts.cap3'),
  t('page.hosts.cap4')
])
</script>

<template>
  <div>
    <PageHeader
      icon="i-carbon-data-center"
      :title="t('nav.hosts')"
      :subtitle="t('page.hosts.title')"
      narrow
    />

    <div class="app-section">
      <PlaceholderSection
        icon="i-carbon-data-center"
        :title="t('state.notImplemented')"
        version="0.0.8"
        :capabilities="capabilities"
        :note="t('page.hosts.note')"
      />
    </div>
  </div>
</template>