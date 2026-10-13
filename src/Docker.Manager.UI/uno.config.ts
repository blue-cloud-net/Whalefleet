import { defineConfig, presetIcons, presetTypography, presetUno } from 'unocss'

// UnoCSS：utility-first，与 Element Plus 互不重叠
//  - presetUno：原子化基础预设
//  - presetTypography：prose 风格排版
//  - presetIcons：Iconify 按需加载（默认使用 carbon 图标集）
export default defineConfig({
  // 主题开关由 <html class="dark"> 驱动（见 stores/theme.ts），必须用 class 模式。
  // 默认的 prefers-color-scheme 会让「手动选亮色但系统是暗色」时无法保持亮色，反之亦然。
  darkMode: 'class',
  presets: [
    presetUno(),
    presetTypography(),
    presetIcons({
      scale: 1.2,
      warn: true
    })
  ],
  theme: {
    colors: {
      // 三色主题：blue（默认）/ green / purple，由 useTheme 注入 CSS 变量覆盖
      brand: {
        50: 'rgb(var(--brand-50) / <alpha-value>)',
        100: 'rgb(var(--brand-100) / <alpha-value>)',
        200: 'rgb(var(--brand-200) / <alpha-value>)',
        300: 'rgb(var(--brand-300) / <alpha-value>)',
        400: 'rgb(var(--brand-400) / <alpha-value>)',
        500: 'rgb(var(--brand-500) / <alpha-value>)',
        600: 'rgb(var(--brand-600) / <alpha-value>)',
        700: 'rgb(var(--brand-700) / <alpha-value>)',
        800: 'rgb(var(--brand-800) / <alpha-value>)',
        900: 'rgb(var(--brand-900) / <alpha-value>)'
      }
    },
    fontFamily: {
      sans: '"Inter", "Helvetica Neue", Arial, "PingFang SC", "Microsoft YaHei", sans-serif',
      mono: '"JetBrains Mono", "Fira Code", "Cascadia Code", Consolas, monospace'
    },
    boxShadow: {
      xs: 'var(--shadow-xs)',
      sm: 'var(--shadow-sm)',
      md: 'var(--shadow-md)',
      lg: 'var(--shadow-lg)',
      brand: 'var(--shadow-brand)'
    },
    borderRadius: {
      sm: 'var(--radius-sm)',
      md: 'var(--radius-md)',
      lg: 'var(--radius-lg)',
      xl: 'var(--radius-xl)'
    }
  },
  shortcuts: [
    // ===== 布局骨架 =====
    {
      // 页面内容容器：限制最大宽度 + 统一内边距 + 纵向节奏
      'app-page': 'p-4 sm:p-6 lg:p-8 mx-auto w-full max-w-[1600px]',
      'app-stack': 'flex flex-col gap-4',
      'app-section': 'mt-6 sm:mt-8',

      // ===== 卡片 =====
      'app-card':
        'rounded-lg bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 shadow-xs transition-[box-shadow,border-color,transform] duration-200 ease-out',
      // 可交互卡片：悬浮抬升 + 品牌色描边
      'app-card-interactive':
        'app-card hover:shadow-md hover:border-brand-300 dark:hover:border-brand-700 hover:-translate-y-px',

      // ===== 导航 =====
      'app-nav-item':
        'w-full flex items-center gap-3 rounded-md text-sm transition-all duration-200 ease-out',
      'app-nav-idle':
        'text-gray-600 dark:text-gray-400 hover:bg-gray-100 dark:hover:bg-gray-800 hover:text-gray-900 dark:hover:text-gray-100',
      'app-nav-active':
        'bg-brand-50 text-brand-600 dark:bg-brand-500/15 dark:text-brand-400 font-medium',

      // ===== 表单元素 =====
      'app-stat-tile':
        'rounded-lg bg-gray-50 dark:bg-gray-800/70 px-3 py-3 ring-1 ring-inset ring-gray-200/70 dark:ring-gray-700/50',
      'app-chip':
        'inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs ring-1 ring-inset transition-colors duration-200',

      // ===== 文本 =====
      'app-page-title':
        'flex items-center gap-2.5 text-xl font-semibold tracking-tight text-gray-900 dark:text-gray-100',
      'app-page-subtitle': 'mt-1.5 text-sm leading-relaxed text-gray-500 dark:text-gray-400',

      // ===== 排版辅助 =====
      'app-truncate': 'min-w-0 truncate',
      'app-tabular': 'tabular-nums'
    }
  ],
  safelist: [
    'i-carbon-sun', 'i-carbon-moon', 'i-carbon-asleep',
    'i-carbon-user', 'i-carbon-settings', 'i-carbon-container-software',
    'i-carbon-image', 'i-carbon-data-volume', 'i-carbon-network-3',
    'i-carbon-menu', 'i-carbon-close', 'i-carbon-search',
    'i-carbon-warning-alt', 'i-carbon-information',
    // 状态与导航
    'i-carbon-checkmark', 'i-carbon-checkmark-filled',
    'i-carbon-warning', 'i-carbon-error', 'i-carbon-help',
    'i-carbon-dashboard', 'i-carbon-activity', 'i-carbon-cube',
    'i-carbon-chevron-right', 'i-carbon-chevron-down', 'i-carbon-arrow-right',
    'i-carbon-arrow-left', 'i-carbon-add', 'i-carbon-renew',
    'i-carbon-edit', 'i-carbon-trash-can', 'i-carbon-copy',
    'i-carbon-folder', 'i-carbon-folder-off', 'i-carbon-meter',
    'i-carbon-cloud-offline', 'i-carbon-cloud-satellite',
    'i-carbon-data-center-alt', 'i-carbon-user-avatar',
    'i-carbon-user-multiple', 'i-carbon-cluster', 'i-carbon-terminal',
    'i-carbon-settings-adjust', 'i-carbon-terminal', 'i-carbon-document',
    'i-carbon-side-panel-open', 'i-carbon-side-panel-close',
    'i-carbon-locked', 'i-carbon-chart-line', 'i-carbon-calendar'
  ]
})