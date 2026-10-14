import { readdirSync, readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { defineConfig, presetIcons, presetTypography, presetUno } from 'unocss'

const require = createRequire(import.meta.url)

/**
 * 校验图标名在 Carbon 里真实存在。
 *
 * Carbon 没有 `-alt` 变体：`data-center-alt` / `server` / `cluster` 全都不存在。
 * UnoCSS 的 presetIcons 对查不到的图标**只打印一条 warn、不中断构建**，
 * 于是开发期毫无察觉，运行时表现为「底色方块在、图标不见」——
 * 总览页「主机总数」磁贴就是这样丢的图标，排查了很久才定位到是名字错了。
 *
 * 这里把校验前移到配置加载期：不存在的图标直接抛错，dev/build 当场失败。
 *
 * 顺带去重：safelist 由「路由扫描 + 模板扫描 + 手写兜底」三路汇成，
 * 同一图标被三路各收一次是常态，而重复条目会让下面的报错把同一个名字列好几遍。
 */
function assertIconsExist(icons: string[]): string[] {
  const carbon = require('@iconify-json/carbon/icons.json') as { icons: Record<string, unknown> }
  const unique = [...new Set(icons)]
  const missing = unique.filter((name) => !(name.replace(/^i-carbon-/, '') in carbon.icons))
  if (missing.length > 0) {
    throw new Error(
      `[uno.config] safelist 中存在 Carbon 没有的图标：\n  - ${missing.join('\n  - ')}\n` +
        `请改用 Carbon 真实存在的名字（node -e "const i=require('@iconify-json/carbon/icons.json');` +
        `console.log(Object.keys(i.icons).filter(n=>n.includes('<关键词>')))" 可查询）。`
    )
  }
  return unique
}

/**
 * 从路由表收集侧边栏图标。
 *
 * 侧边栏图标写作路由 `meta.icon` 的**动态字符串**（SidebarNav 里 `:class="item.icon"`），
 * UnoCSS 的静态提取器扫不到，图标 CSS 就不会被生成 —— 表现为
 * 「图标一开始是空的 / 首次渲染缺失」。因此必须显式 safelist。
 *
 * 这里直接从 `src/router/index.ts` 读出来自动并入，避免「加路由忘加 safelist」
 * 这类静默漂移（历史 bug：i-carbon-layers / i-carbon-events / i-carbon-data-center
 * 三个菜单图标缺失）。
 */
function collectRouteIcons(): string[] {
  const routerFile = fileURLToPath(new URL('./src/router/index.ts', import.meta.url))
  const source = readFileSync(routerFile, 'utf-8')
  const icons = new Set<string>()
  for (const match of source.matchAll(/icon:\s*'(i-carbon-[\w-]+)'/g)) {
    icons.add(match[1])
  }
  return [...icons]
}

/**
 * 从 `src` 下所有 `.vue` / `.ts` 中收集出现的 `i-carbon-*` 图标名。
 *
 * ## 为什么 safelist 之外的模板也要纳管
 * 历史上有过两处图标名写错（`i-carbon-data-center-alt`，以及多打一个连字符的
 * `i-carbon-meter--alt`）。`assertIconsExist` 只校验 safelist **自己**，
 * 而模板里写死的图标压根不在 safelist 里——于是这类错误没有任何编译期信号，
 * 运行时表现为「底色方块在、图标不见」，只能靠肉眼发现。
 *
 * ## ⚠️ 必须先剥掉注释，否则会误伤正确代码
 * 上面那两个错名如今作为**注释**留在原处（记录「早先写错了」）。
 * 正则若不过滤注释，就会把注释里的错名当成活代码扫出来，dev/build 当场抛错——
 * 一个会误杀正确代码的检查比没有检查更糟，所以这里先剥三类注释。
 *
 * 动态拼接（`` `i-carbon-${x}` ``）不会误判：`i-carbon-` 后紧跟 `$`，
 * 不满足 `[a-z0-9-]+` 至少一个字符，压根匹配不上。
 */
function collectSourceIcons(): string[] {
  const srcDir = fileURLToPath(new URL('./src', import.meta.url))
  const icons = new Set<string>()

  const walk = (dir: string): void => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name)

      if (entry.isDirectory()) {
        if (entry.name === 'node_modules') continue

        walk(full)
        continue
      }

      if (!/\.(vue|ts)$/.test(entry.name)) continue

      const source = readFileSync(full, 'utf-8')
        .replace(/<!--[\s\S]*?-->/g, '') // 模板注释
        .replace(/\/\*[\s\S]*?\*\//g, '') // 块注释
        // 行注释：前导组排除 `https://` 这类 URL 里的「//」，
        // 否则一行里 URL 之后的真图标会被连带吃掉
        .replace(/(^|[^:])\/\/[^\n]*/g, '$1')

      for (const match of source.matchAll(/i-carbon-[a-z0-9-]+/g)) {
        icons.add(match[0])
      }
    }
  }

  walk(srcDir)

  return [...icons]
}

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
      },
      // ===== 语义色 =====
      // 全部指向 main.css 的令牌。组件用 bg-surface-card / text-content-muted /
      // border-line-subtle 这类带语义的类名，亮暗切换只发生在令牌层。
      surface: {
        page: 'var(--surface-page)',
        card: 'var(--surface-card)',
        'card-hover': 'var(--surface-card-hover)',
        sunken: 'var(--surface-sunken)',
        hover: 'var(--surface-hover)',
        elevated: 'var(--surface-elevated)'
      },
      content: {
        strong: 'rgb(var(--text-strong) / <alpha-value>)',
        body: 'rgb(var(--text-body) / <alpha-value>)',
        muted: 'rgb(var(--text-muted) / <alpha-value>)',
        faint: 'rgb(var(--text-faint) / <alpha-value>)'
      },
      line: {
        subtle: 'var(--border-subtle)',
        strong: 'var(--border-strong)'
      },
      // 状态色用 RGB 三元组形式，才能支持 ring-status-ok/30 这类透明度写法
      status: {
        ok: 'rgb(var(--status-ok) / <alpha-value>)',
        'ok-soft': 'rgb(var(--status-ok-soft) / <alpha-value>)',
        down: 'rgb(var(--status-down) / <alpha-value>)',
        'down-soft': 'rgb(var(--status-down-soft) / <alpha-value>)',
        warn: 'rgb(var(--status-warn) / <alpha-value>)',
        'warn-soft': 'rgb(var(--status-warn-soft) / <alpha-value>)',
        idle: 'rgb(var(--status-idle) / <alpha-value>)',
        'idle-soft': 'rgb(var(--status-idle-soft) / <alpha-value>)'
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
        'rounded-lg bg-surface-card border border-line-subtle shadow-xs transition-[box-shadow,border-color,background-color] duration-200 ease-out',
      // 可交互卡片：悬浮抬升 + 品牌色描边 + 表面提亮。
      // 表面提亮是暗色下的主要反馈信号——那里的 shadow-md 几乎不可见。
      'app-card-interactive':
        'app-card hover:shadow-md hover:border-brand-300 hover:bg-surface-card-hover hover:-translate-y-px',

      // ===== 导航 =====
      'app-nav-item':
        'w-full flex items-center gap-3 rounded-md text-sm transition-all duration-200 ease-out',
      'app-nav-idle':
        'text-content-body hover:bg-surface-hover hover:text-content-strong',
      'app-nav-active':
        'bg-brand-50 text-brand-600 dark:bg-brand-500/15 dark:text-brand-400 font-medium',

      // ===== 语义文本 / 分隔线 =====
      // 替代散落各处的 `text-gray-500 dark:text-gray-400` 与 `border-gray-200 dark:border-gray-700`
      'app-text-muted': 'text-content-muted',
      'app-text-faint': 'text-content-faint',
      'app-divider': 'border-line-subtle',

      // ===== 表单元素 =====
      'app-stat-tile':
        'rounded-lg bg-surface-sunken px-3 py-3 ring-1 ring-inset ring-line-subtle',
      'app-chip':
        'inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs ring-1 ring-inset transition-colors duration-200',

      // ===== 文本 =====
      'app-page-title':
        'flex items-center gap-2.5 text-xl font-semibold tracking-tight text-content-strong',
      'app-page-subtitle': 'mt-1.5 text-sm leading-relaxed text-content-muted',

      // ===== 排版辅助 =====
      'app-truncate': 'min-w-0 truncate',
      'app-tabular': 'tabular-nums',

      // ===== 详情页表格（0.0.11）=====
      // 不用 el-table：详情的挂载 / 网络只有几行，引一整套表格组件（排序、分列、虚拟滚动）
      // 换不来任何好处，反而让样式受 EP 主题摆布。
      'app-table-wrap': 'overflow-x-auto',
      'app-table': 'w-full text-left text-sm border-collapse',
      'app-table-head': 'text-xs font-medium text-content-muted border-b border-line-subtle',
      'app-table-cell': 'py-2 pr-4 align-top border-b border-line-subtle/60',

      // ===== 等宽 / 预格式化文本（0.0.11）=====
      // `break-all` 不能省：容器日志与环境变量里的长串（连接串、token）会把卡片撑破。
      'app-log': 'max-h-[60vh] overflow-auto px-4 py-3 sm:px-5 font-mono text-xs leading-relaxed whitespace-pre-wrap break-all',
      'app-log-list': 'flex flex-col gap-1'
    }
  ],
  // 一律过一遍存在性校验：Carbon 无 `-alt` 变体，写错名字只会静默丢图标（见 assertIconsExist）
  safelist: assertIconsExist([
    // ===== 路由 meta.icon 自动同步（见 collectRouteIcons）=====
    // 侧边栏图标以动态字符串出现，必须显式 safelist，否则不会生成 CSS
    ...collectRouteIcons(),
    // ===== 模板里出现的图标自动同步（见 collectSourceIcons）=====
    ...collectSourceIcons(),
    'i-carbon-sun', 'i-carbon-moon', 'i-carbon-asleep',
    'i-carbon-user', 'i-carbon-settings', 'i-carbon-container-software',
    'i-carbon-image', 'i-carbon-data-volume', 'i-carbon-network-3',
    'i-carbon-network-4', 'i-carbon-layers', 'i-carbon-events',
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
    'i-carbon-data-center', 'i-carbon-user-avatar',
    'i-carbon-user-multiple', 'i-carbon-meter-alt', 'i-carbon-terminal',
    'i-carbon-settings-adjust', 'i-carbon-document',
    // 表格行内 IconActionButton 的图标。icon 是**动态字符串**，静态提取器未必扫得到；
    // collectSourceIcons 已能自动兜住，这里保留手写项只为一眼可查（重复项会被去重）
    'i-carbon-play', 'i-carbon-stop', 'i-carbon-restart', 'i-carbon-document-view',
    'i-carbon-password',
    'i-carbon-side-panel-open', 'i-carbon-side-panel-close',
    'i-carbon-locked', 'i-carbon-chart-line', 'i-carbon-calendar'
  ])
})