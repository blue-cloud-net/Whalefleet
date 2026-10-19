import { describe, expect, it } from 'vitest'
import {
  THEME_MODES,
  nextThemeMode,
  themeModeIcon
} from '../src/utils/theme-mode'
import type { ThemeMode } from '../src/stores/theme'

/**
 * 主题模式轮转（顶栏那个单按钮）。
 *
 * 轮转是纯函数而非组件内联，是因为「点 N 次回到原点」和「顺序里 system 在哪一位」
 * 这两件事最容易在重构时被无意改掉——写死成 `(index + 1) % 3` 的组件没法单测。
 */
describe('nextThemeMode', () => {
  it('按「亮 → 暗 → 跟随系统」轮转', () => {
    expect(nextThemeMode('light')).toBe('dark')
    expect(nextThemeMode('dark')).toBe('system')
  })

  it('跟随系统是末位，再点回到亮色（构成闭环）', () => {
    expect(nextThemeMode('system')).toBe('light')
  })

  it('连点 3 次回到原点', () => {
    for (const start of ['light', 'dark', 'system'] as ThemeMode[]) {
      let mode = start
      for (let i = 0; i < 3; i++) mode = nextThemeMode(mode)
      expect(mode).toBe(start)
    }
  })

  it('未知模式回落到亮色（indexOf 的 -1 绕回下标 0），而不是返回 undefined', () => {
    // `(index + 1 + n) % n` 在 index = -1 时得 0，即「坏了的值」被当作亮色处理。
    // 这是刻意的：localStorage 里存着旧版本 / 手改过的值时，按钮不该变成空白。
    const values = THEME_MODES.map((m) => m.value)
    expect(nextThemeMode('sepia' as ThemeMode)).toBe('light')
    expect(values).toContain(nextThemeMode('sepia' as ThemeMode))
  })
})

describe('themeModeIcon', () => {
  it('每个已登记模式都有图标类名', () => {
    for (const m of THEME_MODES) {
      expect(themeModeIcon(m.value)).toBeTruthy()
    }
  })

  it('未知值回落到亮色图标，不返回空字符串（否则按钮渲染成空白）', () => {
    expect(themeModeIcon('sepia' as ThemeMode)).toBe('i-carbon-sun')
  })

  it('图标表达「当前模式」而非「下一个」——两者集合相同但取值随 mode 变化', () => {
    expect(themeModeIcon('light')).not.toBe(themeModeIcon('dark'))
    expect(themeModeIcon('dark')).not.toBe(themeModeIcon('system'))
  })
})

describe('THEME_MODES', () => {
  it('三个模式齐全且互不重复（新增模式时这张表就是契约）', () => {
    const values = THEME_MODES.map((m) => m.value)
    expect(values).toEqual(['light', 'dark', 'system'])
    expect(new Set(values).size).toBe(values.length)
  })

  it('跟随系统在末位：它是「不做决定」的兜底态，不该是默认选项', () => {
    expect(THEME_MODES[THEME_MODES.length - 1].value).toBe('system')
  })
})
