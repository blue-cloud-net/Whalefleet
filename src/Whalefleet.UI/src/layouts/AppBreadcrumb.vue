<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import { buildCrumbs } from '@/utils/breadcrumbs'

/**
 * 顶栏面包屑：**自动从路由 meta 派生**，各页面不需要自己维护。
 *
 * 层级规则见 `utils/breadcrumbs.ts`（纯函数，已被单测覆盖）——
 * 主机作用域会带上主机名一段，既是「我在哪台机器上」的提示，也是回主机概览的入口。
 */

const route = useRoute()
const { t } = useI18n()
const host = useHostStore()

const crumbs = computed(() =>
  buildCrumbs({
    titleKey: route.meta?.title,
    isHostScope: route.meta?.scope === 'host',
    currentHost: host.currentHost,
    t: (key: string) => t(key)
  })
)
</script>

<template>
  <el-breadcrumb class="hidden sm:block whitespace-nowrap" separator="/">
    <el-breadcrumb-item
      v-for="(crumb, index) in crumbs"
      :key="crumb.key"
      :to="index < crumbs.length - 1 ? crumb.to : undefined"
    >
      {{ crumb.label }}
    </el-breadcrumb-item>
  </el-breadcrumb>
</template>
