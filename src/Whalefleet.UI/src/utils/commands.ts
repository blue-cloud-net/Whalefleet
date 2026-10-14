/**
 * 命令面板的条目定义与检索（0.0.14）。
 *
 * ## 为什么条目构造与模糊检索拆成独立模块
 * 面板组件要负责焦点陷阱、键盘选择、滚动定位等**交互**；这些都很依赖 DOM，
 * 很难单测。而「有哪些命令、怎么把用户输入的 `imag` 匹配到「镜像」」是**纯逻辑**，
 * 恰恰是最容易出错的地方——错一个字符或漏一条命令，只有真打开面板才看得见。
 *
 * 因此本文件只产出「候选条目」与「排序后的命中」，不碰任何 DOM。
 */

/** 条目类别。影响条目在面板里的分组与图标色调。 */
export type CommandGroup = 'navigation' | 'action' | 'help'

/**
 * 一条命令。
 *
 * `keywords` 是**中英混排**的额外匹配词：用户搜「拉取」时，若只匹配 `label`
 * 就得看界面语言；写进 keywords 后中文与英文两种输入习惯都能命中。
 */
export interface CommandItem {
  /** 稳定标识；同时用作 `v-for` 的 key */
  id: string
  /** 展示文案的 i18n key（组件负责 `t()`） */
  label: string
  /** 分组 */
  group: CommandGroup
  /** Carbon 图标名（`i-carbon-*`） */
  icon: string
  /** 额外匹配词（不展示，仅参与检索） */
  keywords?: string[]
  /** 展示用的快捷键提示，如 `g i` */
  shortcutHint?: string
  /** 目标路由名（vue-router 的 `name`）；与 `action` 二选一 */
  routeName?: string
  /**
   * 目标路由是否需要 `hostId` 参数。
   *
   * 主机作用域的路由都带 `:hostId`，缺参数会让 vue-router 抛
   * "Missing required param"（白屏级别）。因此组件据此在**构造期**就把这些
   * 条目从全局作用域下列掉，而不是让用户点了再被守卫弹回总览。
   */
  requiresHost?: boolean
  /** 是否仅管理员可见 */
  requiresAdmin?: boolean
  /** 非路由动作（由调用方注入） */
  action?: () => void
}

/** 面板可注入的动作。 */
export interface CommandActions {
  createContainer?: () => void
  pullImage?: () => void
  showShortcuts?: () => void
  toggleSidebar?: () => void
}

/**
 * 默认命令集：路由 + 常用操作。
 *
 * ## 为什么不用遍历路由表自动生成
 * 自动遍历会把 `containers/:id/logs`、`containers/:id/inspect` 这类
 * **需要运行时参数**的路由也列进来——它们没法直接跳转，面板里点一下就是死路。
 * 同时「创建容器」「拉取镜像」这类动作根本不是路由，自动遍历拿不到。
 *
 * 因此这里手写清单：宁可漏一条，也不能列一条点不动的。
 */
export function buildCommands(actions: CommandActions = {}): CommandItem[] {
  return [
    // ===== 全局作用域 =====
    {
      id: 'nav-overview',
      label: 'nav.overview',
      group: 'navigation',
      icon: 'i-carbon-dashboard',
      routeName: 'overview',
      keywords: ['总览', 'overview', 'home', 'all servers']
    },
    {
      id: 'nav-settings',
      label: 'nav.settings',
      group: 'navigation',
      icon: 'i-carbon-settings-adjust',
      routeName: 'settings',
      keywords: ['设置', 'settings', 'preferences', '主题', 'theme']
    },
    {
      id: 'nav-hosts',
      label: 'nav.hosts',
      group: 'navigation',
      icon: 'i-carbon-data-center',
      routeName: 'hosts',
      requiresAdmin: true,
      keywords: ['主机', 'hosts', 'server', '管理', 'manage']
    },
    {
      id: 'nav-users',
      label: 'nav.users',
      group: 'navigation',
      icon: 'i-carbon-user-multiple',
      routeName: 'users',
      requiresAdmin: true,
      keywords: ['用户', 'users', 'account', '账号']
    },

    // ===== 主机作用域（需要 hostId）=====
    {
      id: 'nav-status',
      label: 'nav.status',
      group: 'navigation',
      icon: 'i-carbon-activity',
      routeName: 'host-status',
      requiresHost: true,
      keywords: ['状态', 'status', 'health']
    },
    {
      id: 'nav-containers',
      label: 'nav.containers',
      group: 'navigation',
      icon: 'i-carbon-container-software',
      routeName: 'host-containers',
      requiresHost: true,
      keywords: ['容器', 'containers', 'docker ps', 'ps']
    },
    {
      id: 'nav-images',
      label: 'nav.images',
      group: 'navigation',
      icon: 'i-carbon-image',
      routeName: 'host-images',
      requiresHost: true,
      keywords: ['镜像', 'images', 'docker images']
    },
    {
      id: 'nav-volumes',
      label: 'nav.volumes',
      group: 'navigation',
      icon: 'i-carbon-data-volume',
      routeName: 'host-volumes',
      requiresHost: true,
      keywords: ['数据卷', 'volumes', 'volume']
    },
    {
      id: 'nav-networks',
      label: 'nav.networks',
      group: 'navigation',
      icon: 'i-carbon-network-3',
      routeName: 'host-networks',
      requiresHost: true,
      keywords: ['网络', 'networks', 'network']
    },
    {
      id: 'nav-stacks',
      label: 'nav.stacks',
      group: 'navigation',
      icon: 'i-carbon-layers',
      routeName: 'host-stacks',
      requiresHost: true,
      keywords: ['编排', 'stacks', 'compose']
    },
    {
      id: 'nav-events',
      label: 'nav.events',
      group: 'navigation',
      icon: 'i-carbon-events',
      routeName: 'host-events',
      requiresHost: true,
      keywords: ['事件', 'events', 'audit']
    },

    // ===== 动作 =====
    {
      id: 'act-create-container',
      label: 'command.createContainer',
      group: 'action',
      icon: 'i-carbon-add',
      requiresHost: true,
      shortcutHint: 'G C',
      keywords: ['创建容器', 'create container', 'new container', 'run'],
      action: actions.createContainer
    },
    {
      id: 'act-pull-image',
      label: 'command.pullImage',
      group: 'action',
      icon: 'i-carbon-cloud-download',
      requiresHost: true,
      keywords: ['拉取镜像', 'pull image', 'docker pull', 'download'],
      action: actions.pullImage
    },
    {
      id: 'act-toggle-sidebar',
      label: 'command.toggleSidebar',
      group: 'action',
      icon: 'i-carbon-side-panel-close',
      keywords: ['侧边栏', 'sidebar', '折叠', 'collapse'],
      action: actions.toggleSidebar
    },
    {
      id: 'act-shortcuts',
      label: 'command.shortcuts',
      group: 'help',
      icon: 'i-carbon-keyboard',
      shortcutHint: '?',
      keywords: ['快捷键', 'shortcuts', 'keyboard', '帮助', 'help', 'keybindings'],
      action: actions.showShortcuts
    }
  ]
}

/**
 * 检索用的扁平条目。
 *
 * Fuse 需要一个**统一**的搜索字段，所以把 label / keywords 拼成一个合成串，
 * 而不是配多 key 加权——后者对命令面板这种极短查询反而不稳：
 * 用户搜「镜」时，命中 label 还是 keywords 的权重都不该让另一条更长但
 * 更不相关的条目排到前面去。
 */
export interface SearchableCommand extends CommandItem {
  /** 已由 `t()` 解析的展示文案 */
  displayLabel: string
  /** 参与匹配的合成串：展示文案 + keywords */
  haystack: string
}

/**
 * 检索排序。
 *
 * 阈值 `0.35` 偏宽松：命令面板的查询普遍很短（`img`、`镜`），
 * Fuse 默认的严格阈值会让这类输入**一条都搜不出来**——面板空着比给几个
 * 可能无关的结果更让人困惑。宽松 + 上限若干条，把选择成本压在可接受范围内。
 *
 * 空查询时**原样返回**（按声明顺序），而不是走 Fuse：
 * Fuse 对空串会给所有条目一个相同的低分，排序结果等于随机，
 * 用户每次打开看到的顺序都不一样。
 *
 * ## 为什么要 `async`
 * fuse.js 以**动态 import** 引入：它是模糊匹配的实现，静态导入会把它
 * 塞进主 chunk，而面板只在用户按下 Ctrl+K 时才可能打开——为了让一个
 * 偶尔用的功能拖慢首屏不值得。代价是检索变成异步，调用方需要 `await`。
 *
 * @param items 已解析展示文案的候选条目
 * @param query 用户输入；空白时返回全部
 * @param limit 最多返回条数
 */
export async function searchCommands(
  items: SearchableCommand[],
  query: string,
  limit = 8
): Promise<SearchableCommand[]> {
  const trimmed = query.trim()

  if (trimmed.length === 0) return items.slice(0, limit)

  const { default: Fuse } = await import('fuse.js')

  const fuse = new Fuse(items, {
    keys: ['haystack'],
    // 0.35：见上方说明——短查询下严格匹配会搜不出东西
    threshold: 0.35,
    // 忽略位置：面板的查询是「片段」而非「前缀」，
    // 用户搜「image」期望命中「Pull image」，而不是要求它出现在开头
    ignoreLocation: true,
    minMatchCharLength: 1
  })

  return fuse.search(trimmed, { limit }).map((result) => result.item)
}
