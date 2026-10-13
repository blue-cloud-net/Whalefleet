<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useTheme } from '@/composables/useTheme'
import type { ThemeColor, ThemeMode } from '@/stores/theme'

const { t } = useI18n()
const { mode, color, setMode, setColor } = useTheme()

const MODES: { value: ThemeMode; icon: string }[] = [
  { value: 'light', icon: 'i-carbon-sun' },
  { value: 'dark', icon: 'i-carbon-moon' },
  { value: 'system', icon: 'i-carbon-asleep' }
]

const COLORS: { value: ThemeColor; dot: string }[] = [
  { value: 'blue', dot: 'bg-[rgb(64_158_255)]' },
  { value: 'green', dot: 'bg-[rgb(46_156_86)]' },
  { value: 'purple', dot: 'bg-[rgb(122_64_184)]' }
]

const modeIcon = computed(() => MODES.find((m) => m.value === mode.value)?.icon ?? 'i-carbon-sun')
const activeDot = computed(() => COLORS.find((c) => c.value === color.value)?.dot ?? COLORS[0].dot)
</script>

<template>
  <div class="flex items-center gap-0.5">
    <!-- 模式切换 -->
    <el-dropdown trigger="click">
      <el-button text class="!px-2" :title="t('theme.mode')" :aria-label="t('theme.mode')">
        <span class="text-lg" :class="modeIcon" />
      </el-button>
      <template #dropdown>
        <el-dropdown-menu>
          <el-dropdown-item
            v-for="m in MODES"
            :key="m.value"
            :class="{ 'text-brand-600 dark:text-brand-400 font-medium': m.value === mode }"
            @click="setMode(m.value)"
          >
            <span class="flex items-center gap-2.5">
              <span class="text-base" :class="m.icon" />
              <span>{{ t(`theme.mode.${m.value}`) }}</span>
              <span v-if="m.value === mode" class="i-carbon-checkmark text-sm ml-auto text-brand-500" />
            </span>
          </el-dropdown-item>
        </el-dropdown-menu>
      </template>
    </el-dropdown>

    <!-- 主题色 -->
    <el-dropdown trigger="click">
      <el-button text class="!px-2" :title="t('theme.color')" :aria-label="t('theme.color')">
        <span
          class="w-4 h-4 rounded-full ring-2 ring-offset-2 ring-offset-white dark:ring-offset-gray-900 transition-all duration-200"
          :class="[activeDot, `ring-current/25`]"
        />
      </el-button>
      <template #dropdown>
        <el-dropdown-menu>
          <el-dropdown-item v-for="c in COLORS" :key="c.value" @click="setColor(c.value)">
            <span class="flex items-center gap-2.5">
              <span class="w-3.5 h-3.5 rounded-full shrink-0" :class="c.dot" />
              <span>{{ t(`theme.color.${c.value}`) }}</span>
              <span
                v-if="c.value === color"
                class="i-carbon-checkmark text-sm ml-auto text-brand-500"
              />
            </span>
          </el-dropdown-item>
        </el-dropdown-menu>
      </template>
    </el-dropdown>
  </div>
</template>