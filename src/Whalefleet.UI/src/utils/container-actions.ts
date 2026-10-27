/**
 * 容器生命周期动作的**可用性矩阵**（0.7.0）。
 *
 * ## 为什么把判定放在这里而不是各页面各写一遍
 *
 * 「这个容器现在能不能暂停」这个问题至少要在三个地方被回答：
 * 列表页的行内按钮、列表页的批量按钮、详情页的操作区。
 * 三处各写一遍 `state === 'running'` 的组合，迟早会漏掉一个状态——
 * 而漏掉的后果不是「少一个按钮」，而是**给出了一个点了会报错的按钮**
 * （守护进程回 409，用户看到的是「操作失败」）。
 *
 * 本模块是这唯一的判定处，且**只做判定、不碰 i18n**：
 * 它返回 i18n key，界面再翻译——这样它可以脱离组件单测。
 */

/** 容器的状态文本（与后端 `ContainerState.Status` 一致）。 */
export type ContainerStateText =
  | 'running'
  | 'exited'
  | 'paused'
  | 'restarting'
  | 'created'
  | 'dead'
  | 'removing'
  | (string & {})

/** 容器生命周期动作。 */
export type ContainerAction =
  | 'start'
  | 'stop'
  | 'kill'
  | 'restart'
  | 'pause'
  | 'resume'
  | 'remove'

/**
 * 动作 → 允许执行该动作的状态集合。
 *
 * ⚠️ **判定依据是 Docker 的语义，不是界面的好看程度**：
 * - `stop` / `restart` / `kill` 只对**在运行**的容器有意义（暂停中的容器同样算「还活着」）。
 * - `pause` 只对运行中的容器有意义（暂停一个已暂停的容器没有意义）。
 * - `resume` 只对**已暂停**的容器有意义。
 * - `start` 只对**没在跑**的容器有意义。
 * - `remove` 对任何状态都成立（这是它唯一与其它动作不同的地方）。
 */
const ELIGIBLE_STATES: Record<ContainerAction, readonly ContainerStateText[]> = {
  start: ['created', 'exited', 'dead'],
  stop: ['running', 'paused', 'restarting'],
  kill: ['running', 'paused', 'restarting'],
  restart: ['running', 'paused', 'restarting'],
  pause: ['running'],
  resume: ['paused'],
  remove: []
}

/** 「删除」对任何状态都成立，因此单独处理。 */
function isEligible(action: ContainerAction, state: ContainerStateText): boolean {
  if (action === 'remove') return true

  const allowed = ELIGIBLE_STATES[action]

  return allowed.includes(state)
}

/**
 * 容器当前是否处于「活动」状态（运行中 / 已暂停 / 重启中 / 正在删除）。
 *
 * ⚠️ **不能用 `state === 'running'` 代替**：已暂停的容器在 Docker 语义里**仍在运行**
 * （`docker ps` 也把它列在运行类里），而它同样需要 `force` 才能删。
 * 这个判定被删除动作的 `force` 参数依赖，判错的后果是「删运行中的容器报 409」。
 *
 * ⚠️ 它与「某个动作能不能做」**不是一回事**：暂停中的容器算「活动」，
 * 但对它做「停止」是没有意义的（暂停的进程收不到 SIGTERM）。
 * 因此这里单独列一份，而不是复用 <c>ELIGIBLE_STATES.stop</c>。
 */
export function isActive(state: ContainerStateText): boolean {
  return state === 'running' || state === 'paused' || state === 'restarting' || state === 'removing'
}

/**
 * 某个动作对某个状态是否可用。
 *
 * @param action 动作。
 * @param state 容器状态文本。
 */
export function canPerform(action: ContainerAction, state: ContainerStateText): boolean {
  return isEligible(action, state)
}

/**
 * 一批容器里，有几个满足该动作的前置条件。
 *
 * 批量按钮的禁用规则：**一个都不满足**才禁用。
 *
 * ⚠️ **不是「全部满足」才启用**：选 10 个容器、其中 3 个在跑而 7 个已停时，
 * 「启动」对那 7 个有效、对那 3 个是空操作——后端按幂等处理，
 * 因此按钮应当可用（用户想启动的是那 7 个）。
 * 而「暂停」在这种情况下完全无效（没有运行中的容器可暂停），才该禁用。
 */
export function eligibleCount(
  action: ContainerAction,
  states: readonly ContainerStateText[]
): number {
  return states.filter((state) => canPerform(action, state)).length
}

/**
 * 批量动作的禁用原因（返回 i18n key）。
 *
 * @param action 动作。
 * @param states 选中容器的状态列表。
 * @returns 可用时返回 `null`；不可用时返回可翻译的 key。
 */
export function batchDisabledReason(
  action: ContainerAction,
  states: readonly ContainerStateText[]
): string | null {
  if (states.length === 0) return 'container.batch.selectFirst'
  if (eligibleCount(action, states) > 0) return null

  return `container.batch.noEligible.${action}`
}

/**
 * 单个动作的**禁用原因**（返回 i18n key），用于行内按钮与详情页按钮。
 *
 * 与批量版的区别：单行只有一个容器，不可用时**一定**有原因，
 * 因此这里返回的是「为什么现在不能做」而不是「先选几个」。
 */
export function actionDisabledReason(
  action: ContainerAction,
  state: ContainerStateText
): string | null {
  if (canPerform(action, state)) return null

  return `container.action.disabled.${action}`
}
