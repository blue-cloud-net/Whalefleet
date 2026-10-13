import js from '@eslint/js'
import pluginVue from 'eslint-plugin-vue'
import tseslint from 'typescript-eslint'

/**
 * ESLint 扁平原生配置（flat config）。
 *
 * 范围刻意保守：只做「正确性」检查（未使用变量、可疑写法、Vue 必需规则），
 * 不引入格式化类规则——格式化交由 Prettier 负责，避免 `lint` 与 `format` 互相打架。
 */
export default tseslint.config(
  // public/mockServiceWorker.js 由 `msw init` 生成，文件头自带 eslint-disable，
  // 反倒会触发 "Unused eslint-disable directive" 警告。生成物不应进 lint。
  { ignores: ['dist/**', 'coverage/**', 'node_modules/**', 'public/mockServiceWorker.js'] },

  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...pluginVue.configs['flat/essential'],

  {
    // .vue 的 <script lang="ts"> 必须显式换成 TS 解析器，否则 TS 语法解析失败
    files: ['**/*.vue'],
    languageOptions: {
      parserOptions: { parser: tseslint.parser }
    }
  },

  {
    // 浏览器 + Node（vite / vitest 配置）环境下的全局变量。
    // 刻意不引 `globals` 包以保持依赖最小，故按需显式声明。
    languageOptions: {
      globals: {
        window: 'readonly',
        document: 'readonly',
        localStorage: 'readonly',
        sessionStorage: 'readonly',
        navigator: 'readonly',
        console: 'readonly',
        fetch: 'readonly',
        URL: 'readonly',
        setTimeout: 'readonly',
        clearTimeout: 'readonly',
        setInterval: 'readonly',
        clearInterval: 'readonly',
        requestAnimationFrame: 'readonly',
        process: 'readonly',
        __dirname: 'readonly'
      }
    }
  },

  {
    rules: {
      // 允许以 _ 开头的未使用参数/变量（接口实现与回调签名需要）
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }
      ]
    }
  }
)
