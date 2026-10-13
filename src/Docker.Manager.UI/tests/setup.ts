import { config } from '@vue/test-utils'

// 全局桩：Element Plus 在 jsdom 下部分组件需要 ResizeObserver
globalThis.ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
}

config.global.stubs = {
  transition: false
}