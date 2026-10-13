<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useThemeStore, type ThemeColor, type ThemeMode } from '@/stores/theme'
import { usePreferencesStore, PAGE_SIZE_OPTIONS } from '@/stores/preferences'
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
            <span class="text-sm text-gray-600 dark:text-gray-300">{{ t('theme.mode') }}</span>
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
            <span class="text-sm text-gray-600 dark:text-gray-300">{{ t('theme.color') }}</span>
            <!-- 色板选择比单选按钮更直观 -->
            <div class="flex items-center gap-2">
              <button
                v-for="c in themeColors"
                :key="c.value"
                class="flex items-center gap-2 rounded-full pl-1.5 pr-3 py-1 transition-all duration-200"
                :class="[
                  theme.color === c.value
                    ? 'bg-brand-500/12 ring-1 ring-brand-500/30'
                    : 'ring-1 ring-gray-200 dark:ring-gray-700 hover:ring-gray-300 dark:hover:ring-gray-600'
                ]"
                @click="theme.setColor(c.value)"
              >
                <span class="w-5 h-5 rounded-full transition-transform duration-200" :class="c.swatch" />
                <span class="text-xs text-gray-600 dark:text-gray-300">{{ t(c.label) }}</span>
              </button>
            </div>
          </div>
        </div>
      </SectionCard>

      <!-- 语言 -->
      <SectionCard :title="t('settings.locale')">
        <div class="flex items-center justify-between gap-4 flex-wrap">
          <span class="text-sm text-gray-600 dark:text-gray-300">{{ t('settings.language') }}</span>
          <el-radio-group
            :model-value="locale"
            size="small"
            @update:model-value="(v: any) => onLocaleChange(v as AppLocale)"
          >
            <el-radio-button value="zh-CN">简体中文</el-radio-button>
            <el-radio-button value="en-US">English</el-radio-button>
          </el-radio-group>
        </div>
      </SectionCard>

      <!-- 列表偏好 -->
      <SectionCard :title="t('settings.list')">
        <div class="flex items-center justify-between gap-4 flex-wrap">
          <span class="text-sm text-gray-600 dark:text-gray-300">{{ t('settings.pageSize') }}</span>
          <el-radio-group
            :model-value="prefs.pageSize"
            size="small"
            @update:model-value="(v: any) => prefs.setPageSize(v as number)"
          >
            <el-radio-button v-for="n in PAGE_SIZE_OPTIONS" :key="n" :value="n">
              {{ n }}
            </el-radio-button>
          </el-radio-group>
        </div>
        <p class="mt-3 text-xs text-gray-400 dark:text-gray-500">
          {{ t('settings.pageSizeHint') }}
        </p>
      </SectionCard>
    </div>
  </div>
</template>