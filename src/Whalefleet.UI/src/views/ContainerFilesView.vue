<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import FileBrowser from '@/components/business/FileBrowser.vue'
import { useAuthStore } from '@/stores/auth'

/**
 * 容器文件浏览页（0.14.0）。
 *
 * ## 为什么是独立页而不是详情页里的抽屉
 * 详情页是**快照**（一次取回、只读展示），文件浏览是**持续导航**
 * （进出目录、下载、上传）。混在一页里时，刷新详情会把浏览位置清掉，
 * 而用户会以为「目录被删了」。
 *
 * ## 写入权限按「后端说了算」处理
 * 这里只按**全局**有效权限决定是否禁用上传按钮（`container.write`）；
 * 主机 / 容器作用域的权限不在这份集合里，误禁用有权限的用户比让它去撞
 * `1502` 更糟——后端仍会兜底。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const hostId = computed(() => String(route.params.hostId ?? ''))
const containerId = computed(() => String(route.params.id ?? ''))
const allowWrite = ref(auth.hasPermission('container.write'))

/** 回容器列表。 */
function backToList(): void {
  void router.push({ name: 'host-containers', params: { hostId: hostId.value } })
}

/** 回容器详情。 */
function backToInspect(): void {
  void router.push({
    name: 'host-container-inspect',
    params: { hostId: hostId.value, id: containerId.value }
  })
}
</script>

<template>
  <div class="app-page">
    <PageHeader
      icon="i-carbon-folder"
      :title="t('fileBrowser.container.title')"
      :title-tip="t('fileBrowser.container.emptyHint')"
    >
      <template #actions>
        <el-button @click="backToInspect">{{ t('container.detail.title') }}</el-button>
        <el-button @click="backToList">{{ t('container.backToList') }}</el-button>
      </template>
    </PageHeader>

    <SectionCard>
      <FileBrowser
        :host-id="hostId"
        kind="container"
        :target="containerId"
        :allow-write="allowWrite"
      />
    </SectionCard>
  </div>
</template>
