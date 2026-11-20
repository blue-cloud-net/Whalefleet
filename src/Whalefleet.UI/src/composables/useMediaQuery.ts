import { getCurrentScope, onScopeDispose, readonly, ref, type Ref } from 'vue'

/**
 * 响应式媒体查询：返回一个随视口宽度变化而更新的布尔值。
 *
 * ## 什么时候该用它，什么时候不该
 *
 * **该用**：当「窄屏要做的事」是**结构性的**（少渲染一个控件、换一种排列），
 * CSS 表达不了的时候。典型例子是 `el-pagination` 的 `layout` 属性——
 * 它决定渲染哪些子组件，CSS 只能藏不能少。
 *
 * **不该用**：能靠 flex 换行、`basis-full`、`order` 解决的纯排版问题。
 * 那些用 CSS 就够，多一层 JS 监听只会带来「首帧闪一下」和「窗口拖动时重排」。
 * 本仓库里的列表工具条、页头操作区走的都是 CSS 路线。
 *
 * ## 为什么自己写而不是用现成库
 *
 * 实现只有十几行，却能避免两个常见坑：
 *  - 旧浏览器的 `addListener` 兼容分支（现代浏览器只需 `addEventListener`）；
 *  - 组件卸载后仍监听 `change`（`onScopeDispose` 摘掉监听，否则泄漏）。
 *
 * ## 断点值与项目保持一致
 *
 * `sm` = 640px（见 `docs/ui-guidelines.md`）。`max-sm` 即「小于 640px」。
 * 注意 `max-sm`（≤639px）与 `sm`（≥640px）是**互斥**的两个区间，
 * 因此同一条规则上同时写 `max-sm:` 与 `sm:` 变体不会打架。
 */
export function useMediaQuery(query: string): Readonly<Ref<boolean>> {
  const matches = ref(false)

  // SSR / 测试环境可能没有 window 或 matchMedia（jsdom 的 matchMedia
  // 恒返回 false），此时保持默认值 false 即可——它意味着「按宽屏处理」。
  if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
    const mql = window.matchMedia(query)
    // 同步取初值：异步取会在首帧先渲染一遍宽屏布局再跳成窄屏（可见的闪动）。
    matches.value = mql.matches

    const onChange = (event: MediaQueryListEvent) => {
      matches.value = event.matches
    }

    mql.addEventListener('change', onChange)

    // 只在有 effect scope 时（组件 setup 内）注册清理；
    // 在 setup 之外调用会警告 "onScopeDispose() is called when there is no active effect scope"。
    if (getCurrentScope()) {
      onScopeDispose(() => mql.removeEventListener('change', onChange))
    }
  }

  return readonly(matches)
}

/**
 * 窄屏判定（< 640px）。
 *
 * 与 `docs/ui-guidelines.md` 的移动端约定、`styles/main.css` 里
 * `@media (max-width: 639px)` 的那一块是同一个断点。
 */
export function useIsNarrow(): Readonly<Ref<boolean>> {
  return useMediaQuery('(max-width: 639px)')
}
