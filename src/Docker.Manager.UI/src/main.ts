import { createApp, watch } from 'vue'
import { createPinia } from 'pinia'
import piniaPluginPersistedstate from 'pinia-plugin-persistedstate'
import ElementPlus from 'element-plus'
import elementZhCn from 'element-plus/es/locale/lang/zh-cn'
import elementEn from 'element-plus/es/locale/lang/en'

import 'virtual:uno.css'
import '@unocss/reset/tailwind.css'
import 'element-plus/dist/index.css'
import '@/styles/main.css'

import App from './App.vue'
import { router } from './router'
import { i18n } from './locales'
import { useThemeStore } from './stores/theme'
import { useAuthStore } from './stores/auth'
import { purgeLegacyHostStorage } from './stores/host'

const app = createApp(App)

// ===== Pinia（含持久化插件）=====
const pinia = createPinia()
pinia.use(piniaPluginPersistedstate)
app.use(pinia)

app.use(router)
app.use(i18n)
app.use(ElementPlus)

// ===== 主题：启动即同步到 <html>，避免首屏闪烁 =====
useThemeStore().applyToDocument()

// ===== 登录态恢复（0.0.4 接入真实 refresh 后再补校验）=====
useAuthStore().bootstrap()

// ===== 清理主机 store 的历史持久化数据（该 store 已改为不持久化）=====
purgeLegacyHostStorage()

// ===== Element Plus 内置文案跟随 i18n locale =====
const elLocales: Record<string, typeof elementZhCn> = {
  'zh-CN': elementZhCn,
  'en-US': elementEn
}

const providerRef = { value: elLocales[i18n.global.locale.value] ?? elementEn }

watch(
  () => i18n.global.locale.value,
  (val) => {
    providerRef.value = elLocales[val] ?? elementEn
  }
)

// 把 Element Plus locale 挂到全局，供 App.vue 的 el-config-provider 读取
app.provide('elementLocale', {
  get value() {
    return providerRef.value
  }
})

app.mount('#app')