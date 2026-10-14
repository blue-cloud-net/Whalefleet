import { ref, watch } from 'vue'

/**
 * 跨组件的动作总线（0.0.14）。
 *
 * ## 为什么需要它
 * 「创建容器」「拉取镜像」的对话框状态目前是各视图的局部 `ref`——只有列表页
 * 知道怎么打开它。命令面板与快捷键在 `AppLayout` 里，却要触发这些对话框，
 * 若把 `createDialogVisible` 提到 store，视图就得反过来依赖全局状态，
 * 等于让「容器页负责创建对话框」这件事失焦。
 *
 * 折中做法就是这里：**视图仍持有自己的开关**，只在被外部触发时由总线推一下。
 *
 * ## 为什么用计数器而不是 boolean
 * boolean 有个致命场景：对话框正开着，用户在命令面板里又按了一次「创建容器」——
 * 布尔值已经是 `true`，赋值不会触发 watcher，动作被吞掉。计数器自增则每次
 * 都是一个不同的值，用户按几次就开几次（对话框已在栈顶时是安全的重复请求）。
 */
const counters = {
  createContainer: ref(0),
  pullImage: ref(0)
}

/** 触发「创建容器」。视图监听对应计数器并打开自己的对话框。 */
export function requestCreateContainer(): void {
  counters.createContainer.value += 1
}

/** 触发「拉取镜像」。 */
export function requestPullImage(): void {
  counters.pullImage.value += 1
}

/**
 * 订阅某个动作。
 *
 * @param action 动作名
 * @param handler 触发回调（对话框打开）
 */
export function onActionRequest(action: keyof typeof counters, handler: () => void): void {
  watch(counters[action], () => handler())
}
