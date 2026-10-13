import { createApp, watch } from 'vue'
import { createPinia } from 'pinia'
import piniaPluginPersistedstate from 'pinia-plugin-persistedstate'
import ElementPlus from 'element-plus'
import elementZhCn from 'element-plus/es/locale/lang/zh-cn'
import elementEn from 'element-plus/es/locale/lang/en'

import 'virtual:uno.css'
import '@unocss/reset/tailwind.css'
import 'element-plus/dist/index.css'
// Element Plus 的暗色主题是**独立可选包**：`<html class="dark">` 本身不会让它变色，
// 必须额外引入 dark/css-vars.css，否则下拉菜单 / 弹窗 / 按钮组等浮层在暗色下仍是白底。
import 'element-plus/theme-chalk/dark/css-vars.css'
// 放在最后：本文件里的令牌与 Element Plus 变量桥接要覆盖库自带值
import '@/styles/main.css'

import App from './App.vue'
import { router } from './router'
import { i18n } from './locales'
import { useThemeStore } from './stores/theme'
import { useAuthStore } from './stores/auth'
import { purgeLegacyHostStorage } from './stores/host'
import { shouldUseMock } from './mocks/switch'

const app = createApp(App)

// ===== Pinia（含持久化插件）=====
const pinia = createPinia()
pinia.use(piniaPluginPersistedstate)
app.use(pinia)

// ===== mock worker =====
//
// ⚠️ **必须在 `app.use(router)` 之前**，不只是 `app.mount()` 之前。
//
// Vue Router 4 的 `install()` 会**立刻触发首次导航**，而路由守卫里
// `await host.ensureLoaded()` 会打出第一个业务请求。若 mock 还没就绪，
// 这个请求就会漏到 Vite 代理 → 后端未启动时返回 `500 text/plain`，
// 页面显示「主机列表加载失败」，但 MOCK 角标却是亮的——极难定位。
//
// 门控是 `import.meta.env.DEV && (...)`：Vite 在生产构建时把 DEV 静态替换为
// false，整段分支连同动态 import 一起被摇掉——不依赖「运行时判断」来保证不进产物。
//
// 开关有两个来源：环境变量（需重启 dev server）与运行时开关（localStorage / URL，
// 免重启）。见 `src/mocks/switch.ts`。
// 门控**必须写在调用点**：`import.meta.env.DEV` 被 Vite 常量折叠为 false 后，
// 整个 if 分支连同 `await import('./mocks/browser')` 一起被摇掉。
// （若把 DEV 判断藏进 shouldUseMock() 内部，动态 import 仍会打进产物——实测多出 260KB。）
const mockEnabled = import.meta.env.DEV && shouldUseMock()
if (mockEnabled) {
  const { startMockWorker } = await import('./mocks/browser')
  await startMockWorker()
}

// ⚠️ 顺序敏感：放在 mock 之后（见上方说明）
app.use(router)
app.use(i18n)
app.use(ElementPlus)

// ===== 主题：启动即同步到 <html>，避免首屏闪烁 =====
useThemeStore().applyToDocument()

// ===== 登录态恢复 =====
// 令牌由持久化插件还原；这里**不**发起预刷新：多标签页共用 storage，
// 各自预刷会被服务端判为重放（刷新令牌一次性）而撤销全部会话。
// 续期按需发生——等请求真收到 401 时由 api/index.ts 静默刷新。
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