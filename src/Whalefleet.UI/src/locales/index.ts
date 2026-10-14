import { createI18n } from 'vue-i18n'
import en from './en-US.json'
import zh from './zh-CN.json'

export type AppLocale = 'zh-CN' | 'en-US'

/**
 * 持久化键
 */
const STORAGE_KEY = 'whalefleet:locale'

/**
 * 从 localStorage 恢复语言；默认 zh-CN。
 * 缺失 key 时回退英文（en-US），与后端 i18n 约定一致。
 */
function resolveInitialLocale(): AppLocale {
  if (typeof window === 'undefined') return 'zh-CN'
  const saved = window.localStorage.getItem(STORAGE_KEY)
  if (saved === 'zh-CN' || saved === 'en-US') return saved
  return 'zh-CN'
}

export const SUPPORTED_LOCALES: AppLocale[] = ['zh-CN', 'en-US']

export const i18n = createI18n({
  legacy: false,
  globalInjection: true,
  locale: resolveInitialLocale(),
  fallbackLocale: 'en-US', // 缺失 key 统一回退英文
  messages: {
    'en-US': en,
    'zh-CN': zh
  }
})

/**
 * 切换语言并持久化。
 */
export function setLocale(locale: AppLocale): void {
  i18n.global.locale.value = locale
  if (typeof window !== 'undefined') {
    window.localStorage.setItem(STORAGE_KEY, locale)
    document.documentElement.lang = locale
  }
}