import type { ThemeMode } from '@/stores/theme'

/**
 * 主题模式的轮转表：**亮色 → 暗色 → 跟随系统 → 亮色**，点一下走一格。
 *
 * 顶栏只有一个按钮时，三选一的下拉是多余的层级（开菜单 → 看三个图标 → 点一个 → 关菜单），
 * 而「我大多数时候只是想快速翻一下明暗」——轮转把三步压成一步。
 * 顺序刻意把**跟随系统放在末位**：它是「不做决定」的兜底态，不是默认选项；
 * 放首位会让第一次点击把它切走，反而偏离「我刚点的是亮色」的直觉。
 *
 * 图标表达**当前**模式而非下一个 —— 按钮显示的是「我现在是什么」，不是「我会变成什么」，
 * 两者混用时用户会在切换后才发现自己的预期错了。
 */
export const THEME_MODES: { value: ThemeMode; icon: string }[] = [
  { value: 'light', icon: 'i-carbon-sun' },
  { value: 'dark', icon: 'i-carbon-moon' },
  { value: 'system', icon: 'i-carbon-asleep' }
]

/** 当前模式对应的图标类名；未知值回落到亮色（空图标比抛异常好）。 */
export function themeModeIcon(mode: ThemeMode): string {
  return THEME_MODES.find((m) => m.value === mode)?.icon ?? 'i-carbon-sun'
}

/**
 * 下一次点击要切到的模式。
 *
 * `index + 1 + length` 里的 `+ length` 不是防溢出保险，而是**有意为之**：
 * 未知模式（localStorage 里存着旧值或被人手改过）时 `findIndex` 返回 -1，
 * 加上长度后绕回下标 0，当作亮色处理 —— 按钮不会因为一个脏值就渲染成空白。
 */
export function nextThemeMode(mode: ThemeMode): ThemeMode {
  const index = THEME_MODES.findIndex((m) => m.value === mode)

  return THEME_MODES[(index + 1 + THEME_MODES.length) % THEME_MODES.length].value
}
