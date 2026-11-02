import { fileURLToPath, URL } from 'node:url'

import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import UnoCSS from 'unocss/vite'

// ⚠️ 端口**默认 30123**，可用 VITE_DEV_PORT 覆盖（Makefile 的 `make dev UI_PORT=…`）。
//    显式指定时启用 strictPort：端口被占用就**直接失败**，而不是静默顺延到 +1 ——
//    顺延会让「我开的明明是 30123」变成 30124，而日志与提示里仍写着 30123。
//    未指定时保持原行为（strictPort: false，允许顺延）。
const devPort = Number.parseInt(process.env.VITE_DEV_PORT ?? '', 10)
const resolvedDevPort = Number.isNaN(devPort) ? 30123 : devPort
const strictPort = process.env.VITE_DEV_PORT !== undefined

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue(), UnoCSS()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url))
    }
  },
  server: {
    host: '0.0.0.0',
    port: resolvedDevPort,
    strictPort,
    proxy: {
      // 开发期走 Vite 代理，保持浏览器同源，避免触发后端 CORS 白名单限制
      '/api': {
        target: process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:8080',
        changeOrigin: true
      },
      '/hubs': {
        target: process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:8080',
        changeOrigin: true,
        ws: true
      },
      '/ws': {
        target: process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:8080',
        changeOrigin: true,
        ws: true
      }
    }
  },
  build: {
    target: 'es2022',
    sourcemap: true,
    outDir: 'dist',
    assetsDir: 'assets',
    chunkSizeWarningLimit: 1500
  }
})