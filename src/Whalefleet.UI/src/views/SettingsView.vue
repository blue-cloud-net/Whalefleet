<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useThemeStore, type ThemeColor, type ThemeMode } from '@/stores/theme'
import { usePreferencesStore, PAGE_SIZE_ALL, PAGE_SIZE_OPTIONS } from '@/stores/preferences'
import { setLocale, type AppLocale } from '@/locales'
import PageHeader from '@/components/common/PageHeader.vue'
import SectionCard from '@/components/common/SectionCard.vue'

const { t, locale } = useI18n()
const theme = useThemeStore()
const prefs = usePreferencesStore()

const themeModes: { value: ThemeMode; label: string }[] = [
  { value: 'light', label: 'theme.mode.light' },
  { value: 'dark', label: 'theme.mode.dark' },
  { value: 'system', label: 'theme.mode.system' }
]

const themeColors: { value: ThemeColor; label: string; swatch: string }[] = [
  { value: 'blue', label: 'theme.color.blue', swatch: 'bg-[rgb(64_158_255)]' },
  { value: 'green', label: 'theme.color.green', swatch: 'bg-[rgb(46_156_86)]' },
  { value: 'purple', label: 'theme.color.purple', swatch: 'bg-[rgb(122_64_184)]' }
]

/**
 * 语言选项。
 *
 * 标签是各语言的**自称**，不走 i18n：把「简体中文」翻译成 "Chinese" 会让中文用户
 * 在英文界面里找不到自己的语言，而这类语言选择器在所有产品里都是自称。
 */
const locales: { value: AppLocale; label: string }[] = [
  { value: 'zh-CN', label: '简体中文' },
  { value: 'en-US', label: 'English' }
]

function onLocaleChange(next: AppLocale) {
  setLocale(next)
}
</script>

<template>
  <div>
    <PageHeader icon="i-carbon-settings-adjust" :title="t('settings.title')" :subtitle="t('settings.subtitle')" narrow />

    <div class="max-w-3xl space-y-4">
      <!-- 外观 -->
      <SectionCard :title="t('settings.appearance')">
        <div class="space-y-5">
          <div class="flex items-center justify-between gap-4 flex-wrap">
            <span class="text-sm text-content-body">{{ t('theme.mode') }}</span>
            <el-radio-group
              :model-value="theme.mode"
              size="small"
              @update:model-value="(v: any) => theme.setMode(v as ThemeMode)"
            >
              <el-radio-button v-for="m in themeModes" :key="m.value" :value="m.value">
                {{ t(m.label) }}
              </el-radio-button>
            </el-radio-group>
          </div>

          <div class="flex items-center justify-between gap-4 flex-wrap">
            <span class="text-sm text-content-body">{{ t('theme.color') }}</span>
            <!-- 色板选择比单选按钮更直观 -->
            <div class="flex items-center gap-2">
              <button
                v-for="c in themeColors"
                :key="c.value"
                class="flex items-center gap-2 rounded-full pl-1.5 pr-3 py-1 transition-all duration-200"
                :class="[
                  theme.color === c.value
                    ? 'bg-brand-500/12 ring-1 ring-brand-500/30'
                    : 'ring-1 ring-line-subtle hover:ring-line-strong'
                ]"
                @click="theme.setColor(c.value)"
              >
                <span class="w-5 h-5 rounded-full transition-transform duration-200" :class="c.swatch" />
                <span class="text-xs text-content-body">{{ t(c.label) }}</span>
              </button>
            </div>
          </div>
        </div>
      </SectionCard>

      <!-- 语言 -->
      <SectionCard :title="t('settings.locale')">
        <div class="flex items-center justify-between gap-4 flex-wrap">
          <span class="text-sm text-content-body">{{ t('settings.language') }}</span>
          <!--
            下拉框而不是并排单选：语言是「选一个」的封闭集合，选项文字长短不一
            （简体中文 / English），并排按钮会把右侧撑成一块不对称的按钮组；
            下拉框宽度固定，将来加语种也不用重排布局。
          -->
          <el-select
            :model-value="locale"
            size="small"
            class="w-40"
            @update:model-value="(v: any) => onLocaleChange(v as AppLocale)"
          >
            <el-option
              v-for="item in locales"
              :key="item.value"
              :value="item.value"
              :label="item.label"
            />
          </el-select>
        </div>
      </SectionCard>

      <!-- 列表偏好 -->
      <SectionCard :title="t('settings.list')">
        <div class="flex items-center justify-between gap-4 flex-wrap">
          <span class="text-sm text-content-body">{{ t('settings.pageSize') }}</span>
          <el-radio-group
            :model-value="prefs.pageSize"
            size="small"
            @update:model-value="(v: any) => prefs.setPageSize(v as number)"
          >
            <el-radio-button v-for="n in PAGE_SIZE_OPTIONS" :key="n" :value="n">
              {{ n }}
            </el-radio-button>
            <!-- 与列表页的每页条数下拉是同一个值（同一个 store），故这里也提供「全部」 -->
            <el-radio-button :value="PAGE_SIZE_ALL">{{ t('common.pageSizeAll') }}</el-radio-button>
          </el-radio-group>
        </div>
        <p class="mt-3 text-xs text-content-faint">
          {{ t('settings.pageSizeHint') }}
        </p>
      </SectionCard>

      <!--
        实时更新（0.5.0）。

        三个开关**互相独立**、默认全开：
        - 容器列表实时刷新：容器一停 / 一死就立刻刷新列表
        - 容器详情页指标：订阅资源采样
        - 铃铛通知：容器异常退出时弹提示

        独立而不是一个总开关，是因为它们的代价不同：指标订阅会占一条到守护进程的长连接，
        列表刷新几乎没有成本，通知则会产生 toast 打扰。
      -->
      <SectionCard :title="t('settings.realtime')" :description="t('settings.realtimeHint')">
        <div class="space-y-4">
          <div class="flex items-center justify-between gap-4 flex-wrap">
            <span class="text-sm text-content-body">{{ t('settings.realtimeContainers') }}</span>
            <el-switch v-model="prefs.realtimeContainers" />
          </div>

          <div class="flex items-center justify-between gap-4 flex-wrap">
            <span class="text-sm text-content-body">{{ t('settings.realtimeStats') }}</span>
            <el-switch v-model="prefs.realtimeStats" />
          </div>

          <div class="flex items-center justify-between gap-4 flex-wrap">
            <span class="text-sm text-content-body">{{ t('settings.realtimeNotifications') }}</span>
            <el-switch v-model="prefs.realtimeNotifications" />
          </div>
        </div>
      </SectionCard>
    </div>
  </div>
</template>