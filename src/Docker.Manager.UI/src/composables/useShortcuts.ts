import { onBeforeUnmount } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter, type RouteLocationRaw } from 'vue-router'

/**
 * 全局快捷键（0.0.14）。
 *
 * ## 语法约定：`g` 前缀（chord 式）
 * 单一字母键（`c` / `i` / `v`）会跟**输入法候选、切窗口、系统快捷键**打架，
 * 而无头工具类产品（VS Code / Linear / Raycast）一致选择 `g` 作前缀：
 * 按下 `g` 后 1.5 秒内按第二个键，没按就超时作废。
 * 这套约定已在开发者群体里形成肌肉记忆，学习成本为零。
 *
 * ## 三个必须遵守的前提
 *
 * 1. **绝不在可编辑元素里劫持**。用户在搜索框里打 `g` 是要打字母。
 *    `isTypingTarget()` 是这套快捷键能否共存的**唯一**前提。
 *
 * 2. **不能带修饰键**。`Ctrl/Cmd/Alt + 单键` 已被浏览器与操作系统占满，
 *    抢了会让用户「复制粘贴失灵」，这比没有快捷键糟糕得多。
 *
 * 3. **超时必须可撤销**。`g` 按下后若用户改主意继续打字，第二个键不能被
 *    误当成 chord 的后半段——因此 `g` 之后的非白名单按键一律清空 pending。
 */

/** chord 超时（毫秒）。1.5s 是 VS Code 的取值：够慢能反应过来，够快不折磨。 */
const CHORD_TIMEOUT_MS = 1500

/** 一个可编辑元素。 */
function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false

  // isContentEditable 覆盖没有 <textarea>/<input> 形态的可编辑区域（如 tiptap）
  if (target.isContentEditable) return true

  const tag = target.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
}

/** 是否带修饰键（Ctrl / Meta / Alt）。带了就一律放行给浏览器与系统。 */
function hasModifier(event: KeyboardEvent): boolean {
  return event.ctrlKey || event.metaKey || event.altKey
}

/** 单键 chord 定义。 */
export interface Shortcut {
  /** 触发后的显示文案（i18n key） */
  labelKey: string
  /** 目标路由 */
  to: RouteLocationRaw
}

/** chord 表：`g` 之后的第二键 → 目标。 */
const G_CHORDS: Record<string, Shortcut> = {
  c: { labelKey: 'shortcut.gotoContainers', to: { name: 'host-containers' } },
  i: { labelKey: 'shortcut.gotoImages', to: { name: 'host-images' } },
  v: { labelKey: 'shortcut.gotoVolumes', to: { name: 'host-volumes' } },
  n: { labelKey: 'shortcut.gotoNetworks', to: { name: 'host-networks' } },
  d: { labelKey: 'shortcut.gotoDashboard', to: { name: 'host-dashboard' } },
  s: { labelKey: 'shortcut.gotoStatus', to: { name: 'host-status' } }
}

/** chord 的声明顺序 = 帮助弹窗里的展示顺序。 */
const CHORD_ORDER = ['c', 'i', 'v', 'n', 'd', 's'] as const

/**
 * 快捷键的启用条件与回调。
 *
 * 主机作用域的跳转（`g c` / `g i` …）**必须**有 `hostId`，否则
 * `{ name: 'host-containers' }` 会因为缺参数而抛 vue-router 的
 * "Missing required param" —— 那是一个足以让整页白屏的异常。
 * 因此全局作用域下这些键直接不注册，而不是注册完再靠守卫弹回总览。
 */
export interface ShortcutOptions {
  /** 当前是否有可跳转的主机上下文 */
  hasHostContext: () => boolean
  /** 打开命令面板（Ctrl+K / Cmd+K） */
  openCommandPalette: () => void
  /** 打开快捷键帮助（`?`） */
  showHelp: () => void
  /** 折叠 / 展开侧边栏（`g b`） */
  toggleSidebar: () => void
}

/** 帮助弹窗的一行。 */
export interface ShortcutRow {
  /** 键位展示串，如 `G C` */
  keys: string
  /** 说明（已解析文案） */
  description: string
  /** 分组标题（已解析文案） */
  group: string
}

/** `useShortcuts` 的返回值。 */
export interface ShortcutHandle {
  /** 手动解绑（`onBeforeUnmount` 也会自动调用，通常不需要手动调） */
  dispose: () => void
  /** 帮助弹窗渲染用的行；与实际注册的键同源，不会漂移 */
  rows: ShortcutRow[]
}

/**
 * 注册全局快捷键。在组件的 `setup` 里调用一次即可。
 *
 * 监听挂在 `window` 上而不是外层 div：`el-dialog` 打开时焦点在对话框内，
 * 挂在容器上的监听会漏掉那些事件。
 */
export function useShortcuts(options: ShortcutOptions): ShortcutHandle {
  const router = useRouter()
  const { t } = useI18n()

  /** 已按下 `g`，等待第二个键 */
  let pendingG = false
  let chordTimer: ReturnType<typeof setTimeout> | null = null

  function clearPending(): void {
    pendingG = false
    if (chordTimer !== null) {
      clearTimeout(chordTimer)
      chordTimer = null
    }
  }

  function onKeydown(event: KeyboardEvent): void {
    // 1. 输入中：一律放行。这条必须在最前面——它优先于「用户想在输入框里打 g」
    //    与「用户想用 Ctrl+K 粘贴」的两种意图。
    if (isTypingTarget(event.target)) {
      clearPending()
      return
    }

    // 2. Esc 撤销待定的 chord（常见于「我按错键了」）。放在修饰键判断之前，
    //    因为 Esc 无修饰键、不冲突，先处理可以顺带清掉 pending 状态。
    if (event.key === 'Escape' && pendingG) {
      clearPending()
      return
    }

    // 3. Ctrl/Cmd+K：命令面板。这是「不抢修饰键」原则的唯一例外——
    //    Ctrl/Cmd+K 是浏览器与系统都**没有**占用的组合（Cmd+K 常被搜索引擎插件占，
    //    Ctrl+K 在 Firefox 是聚焦搜索栏，在 Chrome 是聚焦地址栏的搜索建议）。
    //    即便被占用，冲突的也只是「唤起搜索」这一低频动作，不会打断用户打字。
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') {
      event.preventDefault()
      clearPending()
      options.openCommandPalette()
      return
    }

    // 4. 其余带修饰键的组合一律交还浏览器（复制 / 粘贴 / 撤销 / 保存…）
    if (hasModifier(event)) return

    // 5. `?` —— 快捷键帮助。Shift+/ 在多数布局下产生 `?`，
    //    因此这里按 key 判断而不是按 code（code 会因布局而异）。
    if (event.key === '?') {
      event.preventDefault()
      clearPending()
      options.showHelp()
      return
    }

    const key = event.key.toLowerCase()

    // 6. chord 的后半段
    if (pendingG) {
      // 第二个键无论是什么都先清空 pending：用户按了 `g` 又改主意打别的字母，
      // 那个字母**不能**被当成跳转——误跳转比不跳转更糟。
      clearPending()

      // `g b` 折叠侧边栏。它与「跳到卷」的 `g v` 是两件事，分开处理避免抢键
      // （b = 面板，比 v = 卷更符合直觉，且不在导航 chord 表里）。
      if (key === 'b' && options.hasHostContext()) {
        event.preventDefault()
        options.toggleSidebar()
        return
      }

      const chord = G_CHORDS[key]
      if (chord && options.hasHostContext()) {
        event.preventDefault()
        void router.push(chord.to)
      }
      return
    }

    // 7. chord 的前半段
    if (key === 'g' && options.hasHostContext()) {
      event.preventDefault()
      pendingG = true
      chordTimer = setTimeout(clearPending, CHORD_TIMEOUT_MS)
    }
  }

  window.addEventListener('keydown', onKeydown)

  function dispose(): void {
    window.removeEventListener('keydown', onKeydown)
    clearPending()
  }

  onBeforeUnmount(dispose)

  return { dispose, rows: buildRows(options, t) }
}

/**
 * 由**同一份 chord 表**生成帮助内容。
 *
 * 刻意不从常量手抄一份文案：抄一份就意味着加键时容易忘记加说明，
 * 而帮助里少一条键比界面里少一条键更容易让人放弃使用快捷键。
 */
function buildRows(options: ShortcutOptions, t: (key: string) => string): ShortcutRow[] {
  const globalRows: ShortcutRow[] = [
    { keys: 'Ctrl K', description: t('shortcut.commandPalette'), group: t('shortcut.group.global') },
    { keys: '?', description: t('shortcut.help'), group: t('shortcut.group.global') }
  ]

  // 主机关联键仅在有主机上下文时列出：全局作用域下它们不注册。
  // 帮助里列着按了没反应的键，比不列更糟。
  if (!options.hasHostContext()) return globalRows

  return [
    ...globalRows,
    {
      keys: 'G B',
      description: t('shortcut.toggleSidebar'),
      group: t('shortcut.group.global')
    },
    ...CHORD_ORDER.map((key) => ({
      keys: `G ${key.toUpperCase()}`,
      description: t(G_CHORDS[key].labelKey),
      group: t('shortcut.group.navigation')
    }))
  ]
}

/** 导出 chord 键列表，供单测断言「帮助内容与注册表一致」。 */
export const SHORTCUT_CHORD_KEYS: string[] = [...CHORD_ORDER]