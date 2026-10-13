<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useTheme } from '@/composables/useTheme'
import { useAuthStore } from '@/stores/auth'
import { setLocale, type AppLocale } from '@/locales'
import LangSwitcher from '@/components/common/LangSwitcher.vue'
import ThemeSwitcher from '@/components/common/ThemeSwitcher.vue'

const { t, locale } = useI18n()
const router = useRouter()
const route = useRoute()
const auth = useAuthStore()
const { mode, color, resolvedMode, setMode, setColor } = useTheme()

const formRef = ref()
const loading = ref(false)

const form = reactive({
  username: '',
  password: ''
})

const rules = {
  username: [{ required: true, message: t('auth.login.username'), trigger: 'blur' }],
  password: [{ required: true, message: t('auth.login.password'), trigger: 'blur' }]
}

/**
 * 0.0.5 阶段：登录按钮为静态占位，填任意内容即可进入（用于打通路由守卫验证）。
 * 0.0.4 后端 POST /api/v1/auth/login 就绪后替换为真实调用。
 */
async function onSubmit() {
  await formRef.value?.validate()
  loading.value = true
  // 占位：构造一个假 token 以便演示守卫
  auth.setTokens('placeholder-access-token', 'placeholder-refresh-token')
  auth.setUser({
    id: 'u-0',
    username: form.username || 'admin',
    role: 'admin'
  })
  loading.value = false
  const redirect = route.query.redirect as string | undefined
  router.push(redirect || '/overview')
}
</script>

<template>
  <!--
    登录页：品牌色径向光晕 + 居中卡片。
    光晕使用 --brand-* 变量，因此切换三色主题时整页氛围同步变化。
  -->
  <div class="min-h-screen grid place-items-center p-4 relative overflow-hidden">
    <!-- 背景光晕 -->
    <div
      class="absolute inset-0 -z-10 bg-[radial-gradient(ellipse_80%_60%_at_50%_-10%,rgb(var(--brand-500)/0.16),transparent_70%)]"
      aria-hidden="true"
    />

    <div class="w-full max-w-sm relative">
      <!-- 品牌区 -->
      <div class="flex flex-col items-center gap-3 mb-8">
        <span
          class="w-14 h-14 rounded-2xl grid place-items-center text-brand-500 shadow-brand bg-brand-500/10 ring-1 ring-brand-500/20"
        >
          <span class="i-carbon-cube text-8" />
        </span>
        <div class="text-center">
          <h1 class="text-xl font-semibold tracking-tight text-gray-900 dark:text-gray-100">
            {{ t('auth.login.title') }}
          </h1>
          <p class="mt-1 text-xs text-gray-500 dark:text-gray-400">{{ t('app.tagline') }}</p>
        </div>
      </div>

      <!-- 表单卡片 -->
      <div class="app-card p-6 sm:p-7 shadow-lg">
        <el-form
          ref="formRef"
          :model="form"
          :rules="rules"
          label-position="top"
          @submit.prevent="onSubmit"
        >
          <el-form-item prop="username" :label="t('auth.login.username')">
            <el-input v-model="form.username" size="large" autocomplete="username">
              <template #prefix><span class="i-carbon-user text-gray-400" /></template>
            </el-input>
          </el-form-item>

          <el-form-item prop="password" :label="t('auth.login.password')">
            <el-input
              v-model="form.password"
              type="password"
              size="large"
              autocomplete="current-password"
              show-password
              @keyup.enter="onSubmit"
            >
              <template #prefix><span class="i-carbon-locked text-gray-400" /></template>
            </el-input>
          </el-form-item>

          <el-button
            type="primary"
            size="large"
            class="!w-full !mt-1 shadow-brand"
            :loading="loading"
            @click="onSubmit"
          >
            {{ t('auth.login.submit') }}
          </el-button>
        </el-form>

        <!-- 主题快捷切换：登录页也应能切主题，便于验收 -->
        <div class="mt-5 pt-4 border-t border-gray-200 dark:border-gray-700 flex items-center justify-between">
          <div class="flex items-center gap-2">
            <button
              v-for="c in [
                { key: 'blue', cls: 'bg-[rgb(64_158_255)]' },
                { key: 'green', cls: 'bg-[rgb(46_156_86)]' },
                { key: 'purple', cls: 'bg-[rgb(122_64_184)]' }
              ]"
              :key="c.key"
              class="w-5 h-5 rounded-full transition-transform duration-200 hover:scale-110"
              :class="[
                c.cls,
                color === c.key
                  ? 'ring-2 ring-offset-2 ring-gray-300 dark:ring-offset-gray-900 dark:ring-gray-500'
                  : ''
              ]"
              :aria-label="t(`theme.color.${c.key}`)"
              @click="setColor(c.key as 'blue' | 'green' | 'purple')"
            />
          </div>

          <button
            class="text-xs text-gray-500 dark:text-gray-400 hover:text-brand-600 dark:hover:text-brand-400 transition-colors inline-flex items-center gap-1"
            @click="setMode(resolvedMode === 'dark' ? 'light' : 'dark')"
          >
            <span :class="resolvedMode === 'dark' ? 'i-carbon-sun' : 'i-carbon-moon'" />
            {{ resolvedMode === 'dark' ? t('theme.mode.light') : t('theme.mode.dark') }}
          </button>
        </div>
      </div>

      <!-- 语言切换 -->
      <div class="mt-4 flex justify-center">
        <LangSwitcher :model-value="locale as AppLocale" @update:model-value="setLocale" />
      </div>
    </div>
  </div>
</template>