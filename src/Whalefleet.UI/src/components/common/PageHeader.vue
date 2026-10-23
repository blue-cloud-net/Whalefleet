<script setup lang="ts">
import { TIP_DELAY_MS } from '@/utils/tooltip'

/**
 * 页面标题区：图标 + 标题 + 描述 + 右侧操作区。
 *
 * 十个页面都在重复「图标 + h1 + 副标题」的三件套，抽出来后：
 *  - 间距节奏统一（标题 20px、副标题 mt-1.5）
 *  - 移动端副标题可折叠，避免顶栏下方堆叠过高
 *
 * ## 描述有两种放法
 * - `subtitle`：**常驻**在标题下方（总览、主机管理等页面用）
 * - `titleTip`：**悬浮**在标题上（资源列表页用）
 *
 * 列表页选 `titleTip` 的原因：容器 / 镜像 / 卷 / 网络四页的描述都是
 * 「这一页是干什么的」而不是「这一页现在怎么样」——后者的信息量往往已经在
 * 页面主体里了。常驻一行只是把标题栏往下推，还占掉一屏里最值钱的位置。
 * 收起成悬浮提示后，标题栏矮一截，列表能多露几行。
 */
withDefaults(
  defineProps<{
    icon?: string
    title: string
    subtitle?: string
    /** 标题悬浮提示；给了就不显示副标题 */
    titleTip?: string
    /** 副标题最大宽度，防止长文撑满整行 */
    narrow?: boolean
  }>(),
  { narrow: false }
)
</script>

<template>
  <header class="mb-5 sm:mb-6">
    <div class="flex items-start justify-between gap-4 flex-wrap">
      <div class="min-w-0">
        <h1 class="app-page-title">
          <span
            v-if="icon"
            class="text-2xl text-brand-500 shrink-0 leading-none"
            :class="icon"
            aria-hidden="true"
          />
          <!--
            标题整体是 tooltip 的触发区：只把 ⓘ 做成触发点的话，
            用户不会想到「那个小图标能点」，标题文字本身反而点了没反应。
          -->
          <el-tooltip
            v-if="titleTip"
            :content="titleTip"
            placement="bottom-start"
            :show-after="TIP_DELAY_MS"
          >
            <span class="app-truncate">{{ title }}</span>
          </el-tooltip>
          <span v-else class="app-truncate">{{ title }}</span>
          <!-- ⓘ：说明「标题可以悬浮」——没有它，提示就成了没人知道的彩蛋 -->
          <span
            v-if="titleTip"
            class="i-carbon-information shrink-0 text-sm leading-none text-content-faint"
            aria-hidden="true"
          />
          <slot name="badge" />
        </h1>

        <p v-if="subtitle" class="app-page-subtitle" :class="{ 'max-w-3xl': narrow }">
          {{ subtitle }}
        </p>
      </div>

      <!-- 右侧操作区：筛选器 / 主按钮等 -->
      <div v-if="$slots.actions" class="flex items-center gap-2 shrink-0">
        <slot name="actions" />
      </div>
    </div>

    <!-- 标题区下方的次级内容（如统计条、标签页） -->
    <slot name="extra" />
  </header>
</template>