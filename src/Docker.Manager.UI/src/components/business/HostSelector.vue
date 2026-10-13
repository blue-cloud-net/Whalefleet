<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import StatusDot from '@/components/common/StatusDot.vue'

const route = useRoute()
const router = useRouter()
const { t } = useI18n()
const host = useHostStore()

const currentHost = computed(() => host.currentHost)
const hosts = computed(() => host.hosts)
const inHostScope = computed(() => route.meta.scope === 'host')

function statusLabel(status?: string): string {
  switch (status) {
    case 'online':
      return t('host.status.online')
    case 'offline':
      return t('host.status.offline')
    default:
      return t('host.status.unknown')
  }
}

function statusChip(status?: string): string {
  switch (status) {
    case 'online':
      return 'text-green-600 dark:text-green-400 ring-green-200/70 dark:ring-green-500/25'
    case 'offline':
      return 'text-red-600 dark:text-red-400 ring-red-200/70 dark:ring-red-500/25'
    default:
      return 'text-gray-400 dark:text-gray-500 ring-gray-200/70 dark:ring-gray-600/50'
  }
}

/**
 * 切换主机：停留在当前资源页（如 containers → containers），只换 hostId。
 * 若当前不在主机作用域，则进入该主机的概览页。
 * 空命令（命令分隔线下的「返回总览」）→ 回全局总览。
 */
function onSelect(command: string) {
  if (command === '') {
    router.push({ name: 'overview' })
    return
  }
  if (command === host.currentId) return
  if (inHostScope.value) {
    router.push({ name: route.name as string, params: { ...route.params, hostId: command } })
  } else {
    router.push({ name: 'host-dashboard', params: { hostId: command } })
  }
}
</script>

<template>
  <el-dropdown trigger="click" @command="onSelect">
    <el-button text class="!px-2 !py-1.5 rounded-md hover:bg-gray-100 dark:hover:bg-gray-800 transition-colors">
      <template v-if="currentHost">
        <StatusDot :status="currentHost.status" size="sm" />
        <span class="text-xs font-medium text-gray-700 dark:text-gray-200 max-w-28 app-truncate">
          {{ currentHost.name }}
        </span>
      </template>
      <template v-else>
        <span class="i-carbon-cloud-satellite text-base text-gray-400" />
        <span class="text-xs text-gray-500 dark:text-gray-400">{{ t('host.selector.placeholder') }}</span>
      </template>
      <span class="i-carbon-chevron-down text-xs text-gray-400" />
    </el-button>

    <template #dropdown>
      <el-dropdown-menu class="min-w-52">
        <el-dropdown-item
          v-for="h in hosts"
          :key="h.id"
          :command="h.id"
          :disabled="h.id === host.currentId"
        >
          <span class="flex items-center gap-2.5">
            <StatusDot :status="h.status" size="sm" />
            <span class="text-sm app-truncate max-w-32">{{ h.name }}</span>
            <span
              class="app-chip ml-auto shrink-0 ring-1 ring-inset"
              :class="statusChip(h.status)"
            >
              {{ statusLabel(h.status) }}
            </span>
          </span>
        </el-dropdown-item>

        <el-dropdown-item v-if="hosts.length === 0" disabled>
          {{ t('host.selector.noHosts') }}
        </el-dropdown-item>

        <el-dropdown-item v-if="inHostScope" divided :command="''">
          <span class="flex items-center gap-2">
            <span class="i-carbon-arrow-left text-sm" />
            {{ t('nav.backToOverview') }}
          </span>
        </el-dropdown-item>
      </el-dropdown-menu>
    </template>
  </el-dropdown>
</template>