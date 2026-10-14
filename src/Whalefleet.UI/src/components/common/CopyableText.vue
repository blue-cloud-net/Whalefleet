<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useI18n } from 'vue-i18n'

/**
 * 复制按钮（0.0.14）。
 *
 * ## 为什么不用 el-button 直接写
 * 「复制到剪贴板」这件事里有三处容易做错的地方，散落在各视图里必然出现一处漏：
 * 1. **降级**：`navigator.clipboard` 只在 HTTPS / localhost 下可用，
 *    http 部署的实例上它是 `undefined`——不判空直接 `undefined.writeText()` 会抛。
 * 2. **反馈**：复制成功要有一个可见信号，否则用户不知道成没成，会反复点。
 * 3. **非安全上下文回退**：老办法 `document.execCommand('copy')` 仍然可用。
 *
 * 抽出来之后这三点只实现一次，`ContainerInspectView` / `ImageInspectView` /
 * 命令面板都能直接用。
 *
 * @param value 要复制的文本（通常是 ID / 连接串）
 */
const props = withDefaults(
  defineProps<{
    value: string
    /** 是否同时把文本本体展示出来（默认只给一个图标按钮） */
    inline?: boolean
    /** 图标按钮尺寸 */
    size?: 'sm' | 'md'
  }>(),
  { inline: false, size: 'sm' }
)

const { t } = useI18n()

/**
 * 已复制状态用值而非布尔量。
 *
 * 需要和「点击后立刻又点一次」区分：布尔量在第二次点击时会因为状态还是
 * `true` 而不重新计时，按钮上的对勾会一直亮着——用户无法确认第二次复制有没有生效。
 * 用时间戳可以在任意时刻重新计时。
 */
const copiedAt = ref(0)

const justCopied = computed(() => Date.now() - copiedAt.value < 2000)

let timer: ReturnType<typeof setTimeout> | null = null

/** 是否处于「无剪贴板 API」的环境（http + 非 localhost）。 */
const clipboardUnavailable =
  typeof navigator === 'undefined' || typeof navigator.clipboard?.writeText !== 'function'

function finish(): void {
  copiedAt.value = Date.now()

  // 清掉上一次的对勾计时，否则旧 timer 会把状态提前改回 false
  if (timer !== null) clearTimeout(timer)
  timer = setTimeout(() => {
    copiedAt.value = 0
    timer = null
  }, 2000)
}

/**
 * 非安全上下文回退：临时 textarea + execCommand。
 *
 * 已废弃，但**没有替代品**——clipboard API 在 http 下就是不可用，
 * 而自托管 Whalefleet 很常见地被部署在 http 内网地址上。
 * 因此这条路径必须留着，否则那批用户会完全无法复制。
 */
function fallbackCopy(): boolean {
  if (typeof document === 'undefined' || typeof document.execCommand !== 'function') return false

  const area = document.createElement('textarea')
  area.value = props.value
  // 不能用 display:none——不可见的元素无法被选中文本，execCommand 会失败
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.opacity = '0'
  document.body.appendChild(area)
  area.select()

  try {
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    document.body.removeChild(area)
  }
}

async function copy(): Promise<void> {
  if (clipboardUnavailable) {
    if (fallbackCopy()) finish()
    return
  }

  try {
    await navigator.clipboard.writeText(props.value)
    finish()
  } catch {
    // 用户拒绝了剪贴板权限时 navigator.clipboard 也会 reject，
    // 这时退到 execCommand 通常还能成功
    if (fallbackCopy()) finish()
  }
}

onBeforeUnmount(() => {
  if (timer !== null) clearTimeout(timer)
})
</script>

<template>
  <span class="inline-flex items-center gap-1.5 min-w-0">
    <code v-if="inline" class="app-truncate font-mono text-xs text-content-body">{{ value }}</code>

    <button
      type="button"
      class="app-copy-btn"
      :class="size === 'sm' ? 'w-6 h-6' : 'w-7 h-7'"
      :title="justCopied ? t('common.copied') : t('common.copy')"
      :aria-label="justCopied ? t('common.copied') : t('common.copy')"
      @click.stop="copy"
    >
      <span
        class="inline-block transition-transform duration-150"
        :class="justCopied ? 'i-carbon-checkmark text-status-ok' : 'i-carbon-copy text-content-faint'"
      />
    </button>
  </span>
</template>

<style scoped>
/* 图标按钮：一圈淡边框 + 悬停提亮。不用 el-button 是为了把它嵌进代码 / 值旁边
   时不引入 EP 的按钮高度与内边距。 */
.app-copy-btn {
  display: inline-grid;
  place-items: center;
  border-radius: var(--radius-sm);
  border: 1px solid var(--border-subtle);
  background: var(--surface-sunken);
  color: inherit;
  cursor: pointer;
  flex-shrink: 0;
  transition: background-color 0.2s ease, border-color 0.2s ease;
}

.app-copy-btn:hover {
  background: var(--surface-hover);
  border-color: var(--border-strong);
}
</style>