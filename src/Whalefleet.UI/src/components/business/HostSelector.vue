<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useHostStore } from '@/stores/host'
import { hostTone, HOST_TONE_CHIP, HOST_TONE_LABEL_KEY } from '@/utils/host-status'
import StatusDot from '@/components/common/StatusDot.vue'

const route = useRoute()
const router = useRouter()
const { t } = useI18n()
const host = useHostStore()

const currentHost = computed(() => host.currentHost)
const hosts = computed(() => host.hosts)
const inHostScope = computed(() => route.meta.scope === 'host')

/** 状态语义与配色统一走 utils/host-status（此前这里是第三份重复实现） */
function statusLabel(status?: string): string {
  return t(HOST_TONE_LABEL_KEY[hostTone(status)])
}

function statusChip(status?: string): string {
  return HOST_TONE_CHIP[hostTone(status)]
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
    <!--
      `gap-1.5` 是刻意加的：状态点只有 6px，紧贴主机名时读起来像名字的偏旁，
      一旦截断（`max-w-28`）点与「…」还会粘在一起。这里给三者
      「状态点 / 名称 / 展开箭头」各自留出呼吸空间。
    -->
    <el-button
      text
      class="!px-2 !py-1.5 rounded-md gap-1.5 hover:bg-surface-hover transition-colors"
    >
      <template v-if="currentHost">
        <StatusDot :status="currentHost.status" size="sm" />
        <span class="text-xs font-medium text-content-body max-w-28 app-truncate">
          {{ currentHost.name }}
        </span>
      </template>
      <template v-else>
        <span class="i-carbon-cloud-satellite text-base text-content-faint" />
        <span class="text-xs text-content-muted">{{ t('host.selector.placeholder') }}</span>
      </template>
      <span class="i-carbon-chevron-down text-xs text-content-faint" />
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
            <span class="app-chip ml-auto shrink-0" :class="statusChip(h.status)">
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