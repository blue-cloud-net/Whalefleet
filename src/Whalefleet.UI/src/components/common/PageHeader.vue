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
            class="app-page-icon text-brand-500 shrink-0 leading-none"
          >
            <span class="text-xl" :class="icon" aria-hidden="true" />
          </span>

          <span class="app-truncate">{{ title }}</span>

          <!--
            **ⓘ 才是 tooltip 的触发点，标题文字不弹提示**。
            早先是「标题整体悬浮出提示、ⓘ 只是个装饰」：读者把鼠标扫过标题时
            脸上会莫名其妙冒出一条说明，而那个说明属于旁边的小图标——
            触发区与它描述的东西对不上，越读越糊涂。
            ⓘ 是通用的「这里有说明」符号，把光标放到它上面出提示才符合直觉；
            `cursor-help` 再补一层「这东西可以悬停」的暗示。
          -->
          <el-tooltip
            v-if="titleTip"
            :content="titleTip"
            placement="bottom-start"
            :show-after="TIP_DELAY_MS"
          >
            <span
              class="i-carbon-information shrink-0 cursor-help text-sm leading-none text-content-faint transition-colors hover:text-content-strong"
            />
          </el-tooltip>
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