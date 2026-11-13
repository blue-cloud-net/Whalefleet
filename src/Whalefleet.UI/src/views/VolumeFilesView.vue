<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'
import FileBrowser from '@/components/business/FileBrowser.vue'
import { useAuthStore } from '@/stores/auth'

/**
 * 数据卷文件浏览页（0.14.0）。
 *
 * ## 卷没有「文件接口」
 * Docker 只给容器提供 `/archive`，卷只能靠**临时挂载**——后端为此创建一次性
 * 辅助容器（带专属标签，用完立即删除）。因此这一页的每次列举 / 下载
 * 都比容器内浏览多一次容器创建开销，**大目录请优先走容器路径**。
 *
 * ## 辅助镜像按需拉取
 * 镜像不存在时首次浏览会很慢（要拉一个小镜像）。界面不做额外提示：
 * 拉取是**一次性的**，之后再进就快了。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const hostId = computed(() => String(route.params.hostId ?? ''))
const volumeName = computed(() => String(route.params.name ?? ''))
const allowWrite = ref(auth.hasPermission('volume.write'))

/** 回卷列表。 */
function backToList(): void {
  void router.push({ name: 'host-volumes', params: { hostId: hostId.value } })
}
</script>

<template>
  <div class="app-page">
    <PageHeader
      icon="i-carbon-folder"
      :title="t('fileBrowser.volume.title')"
      :subtitle="volumeName"
      :title-tip="t('fileBrowser.volume.helperHint')"
    >
      <template #actions>
        <el-button @click="backToList">{{ t('nav.volumes') }}</el-button>
      </template>
    </PageHeader>

    <SectionCard>
      <FileBrowser
        :host-id="hostId"
        kind="volume"
        :target="volumeName"
        :allow-write="allowWrite"
      />
    </SectionCard>
  </div>
</template>
