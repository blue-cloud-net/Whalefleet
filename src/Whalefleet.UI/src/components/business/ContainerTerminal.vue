<script setup lang="ts">
import { computed, onBeforeUnmount, nextTick, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiRequestError } from '@/api'
import { createTerminalToken } from '@/api/container'
import { shouldUseMock } from '@/mocks/switch'
import { buildResizeFrame, buildTerminalSocketUrl, terminalCloseKey } from '@/utils/terminal-url'
import type { FitAddon } from '@xterm/addon-fit'
import type { MockTerminal } from '@/realtime/mock-terminal'
import type { Terminal } from '@xterm/xterm'

/**
 * 容器交互式终端面板（0.8.0）。
 *
 * ## 为什么 xterm 是动态 import
 * xterm 加两个 addon 约 300 KB。它只在「用户真的点开终端」时才有用，
 * 静态引入会让每个访问容器详情页的人都下载一遍。因此这里用 `await import(...)`
 * 把 xterm 拆成独立 chunk，**点开才加载**。
 *
 * ## 为什么先要令牌再连
 * 浏览器建 WebSocket 不能带自定义头，令牌只能进 URL；而 WebSocket 握手失败时
 * 前端拿不到响应体与状态码（只有一个 `close`，通常 1006）。
 * 所以「容器不存在 / 未运行 / 没权限」这些诊断必须由 `terminal-token` 这一步给出。
 *
 * ## 为什么不做自动重连
 * 终端是有状态的双向流：断线后容器里的那条 exec 会话已经结束，
 * 自动重连只会得到一个新的空 shell，而用户会以为「命令还在跑」。
 * 因此断线后显式显示「已断开」，由用户决定要不要重开。
 */

const props = defineProps<{
  /** 主机标识（主机名） */
  hostId: string
  /** 容器 ID */
  containerId: string
  /** 是否禁用入口（容器未运行等） */
  disabled?: boolean
  /** 禁用原因（i18n key）；`disabled` 为真时显示 */
  disabledReason?: string | null
}>()

const { t } = useI18n()

/** 可选的 shell（与后端 `Terminal:AllowedShells` 默认值一致）。 */
const SHELLS = ['/bin/sh', '/bin/bash', '/bin/ash', '/bin/zsh']

/** 连接状态。 */
type TerminalStatus = 'idle' | 'connecting' | 'connected' | 'closed'

const shell = ref('/bin/sh')
const command = ref('')
const showAdvanced = ref(false)
const status = ref<TerminalStatus>('idle')
const message = ref('')
/** 终端容器是否已渲染（xterm 需要一个有尺寸的宿主元素）。 */
const started = ref(false)
const terminalHost = ref<HTMLElement | null>(null)

const encoder = new TextEncoder()

let terminal: Terminal | null = null
let fitAddon: FitAddon | null = null
let socket: WebSocket | null = null
let mockTerminal: MockTerminal | null = null
let resizeObserver: ResizeObserver | null = null
let themeObserver: MutationObserver | null = null

/** 是否已建立过连接（决定「已断开」与「连接失败」两种文案）。 */
const everConnected = ref(false)

const busy = computed(() => status.value === 'connecting' || status.value === 'connected')

const openLabelKey = computed(() =>
  everConnected.value ? 'container.terminal.reconnect' : 'container.terminal.open'
)

const statusLabelKey = computed(() => `container.terminal.status.${status.value}`)

/** 深色主题的 xterm 调色板。 */
const DARK_THEME = {
  background: '#1b1f27',
  foreground: '#e5e7eb',
  cursor: '#7aa2f7',
  selectionBackground: '#3b4252',
  black: '#1b1f27',
  red: '#f7768e',
  green: '#9ece6a',
  yellow: '#e0af68',
  blue: '#7aa2f7',
  magenta: '#bb9af7',
  cyan: '#7dcfff',
  white: '#c0caf5'
}

/** 浅色主题的 xterm 调色板。 */
const LIGHT_THEME = {
  background: '#ffffff',
  foreground: '#1f2937',
  cursor: '#2563eb',
  selectionBackground: '#dbeafe',
  black: '#1f2937',
  red: '#b91c1c',
  green: '#15803d',
  yellow: '#a16207',
  blue: '#1d4ed8',
  magenta: '#7e22ce',
  cyan: '#0e7490',
  white: '#4b5563'
}

/**
 * 当前应使用的调色板。
 *
 * ⚠️ **canvas 不继承 CSS 变量**：xterm 把颜色画进 canvas，`var(--brand-500)` 这类值
 * 连解析都解析不出来（会退化成默认的黑色）。因此这里给的是**字面量**，
 * 并在主题切换时重新 `options.theme = …`（见 `observeTheme`）。
 */
function currentTheme(): Record<string, string> {
  const dark = typeof document !== 'undefined'
    && document.documentElement.classList.contains('dark')

  return dark ? DARK_THEME : LIGHT_THEME
}

/**
 * 懒加载 xterm 并挂到宿主元素上。
 */
async function ensureTerminal(): Promise<void> {
  if (terminal) return

  started.value = true
  await nextTick()

  const [{ Terminal }, { FitAddon }, { WebLinksAddon }] = await Promise.all([
    import('@xterm/xterm'),
    import('@xterm/addon-fit'),
    import('@xterm/addon-web-links')
  ])

  // 样式随 chunk 一起懒加载：静态 import 会让这 6 KB 进主 CSS。
  await import('@xterm/xterm/css/xterm.css')

  if (!terminalHost.value) return

  const term = new Terminal({
    cursorBlink: true,
    fontSize: 13,
    fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
    scrollback: 5000,
    theme: currentTheme()
  })

  const fit = new FitAddon()
  term.loadAddon(fit)
  term.loadAddon(new WebLinksAddon())
  term.open(terminalHost.value)
  fit.fit()

  // 输入：连着真实连接就走二进制帧，mock 下交给假终端。
  term.onData((data) => {
    if (socket?.readyState === WebSocket.OPEN) {
      socket.send(encoder.encode(data))

      return
    }

    mockTerminal?.write(data)
  })

  terminal = term
  fitAddon = fit

  observeResize()
  observeTheme()
}

/**
 * 把当前尺寸告知对端。
 *
 * 尺寸不对的症状很具体：`top` / `vim` 这类全屏程序画出来的框是错位的，
 * 而普通命令的输出看不出问题——所以它不是可选的优化。
 */
function syncSize(): void {
  if (!terminal) return

  const frame = buildResizeFrame(terminal.cols, terminal.rows)

  if (frame === null) return

  if (socket?.readyState === WebSocket.OPEN) {
    socket.send(frame)

    return
  }

  mockTerminal?.resize(terminal.cols, terminal.rows)
}

/** 监听宿主尺寸变化（窗口缩放、侧边栏折叠都会触发）。 */
function observeResize(): void {
  if (!terminalHost.value || resizeObserver) return

  resizeObserver = new ResizeObserver(() => {
    fitAddon?.fit()
    syncSize()
  })
  resizeObserver.observe(terminalHost.value)
}

/** 监听主题切换并重新着色。 */
function observeTheme(): void {
  if (themeObserver) return

  themeObserver = new MutationObserver(() => {
    if (terminal) {
      terminal.options.theme = currentTheme()
    }
  })
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
}

/** 打开（或重开）终端。 */
async function open(): Promise<void> {
  if (busy.value || props.disabled) return

  status.value = 'connecting'
  message.value = ''

  try {
    await ensureTerminal()

    // ⚠️ DEV 门控必须写在**调用点**：写进 shouldUseMock 内部时 Vite 无法静态消除，
    // mock 代码会进生产产物（见 mocks/switch.ts 的说明）。
    if (import.meta.env.DEV && shouldUseMock()) {
      startMock()

      return
    }

    const token = await createTerminalToken(props.hostId, {
      containerId: props.containerId,
      shell: shell.value,
      command: command.value.trim().length > 0 ? command.value.trim() : undefined,
      columns: terminal?.cols,
      rows: terminal?.rows
    })

    connect(buildTerminalSocketUrl({
      hostId: props.hostId,
      token: token.terminalToken,
      columns: terminal?.cols,
      rows: terminal?.rows
    }))
  } catch (error) {
    status.value = 'closed'
    everConnected.value = false
    message.value = error instanceof ApiRequestError
      ? error.message
      : t('container.terminal.connectFailed')
  }
}

/**
 * 建立真实 WebSocket。
 *
 * @param url 完整地址（含令牌与初始尺寸）。
 */
function connect(url: string): void {
  const ws = new WebSocket(url)
  ws.binaryType = 'arraybuffer'
  socket = ws

  ws.onopen = () => {
    status.value = 'connected'
    everConnected.value = true
    terminal?.focus()
    syncSize()
  }

  // 终端输出一律是二进制帧；中途不会出现文本帧（控制帧是**上行**方向）。
  ws.onmessage = (event: MessageEvent<ArrayBuffer>) => {
    terminal?.write(new Uint8Array(event.data))
  }

  ws.onclose = () => {
    // 主动关闭时 socket 已被置空，这里不应再改状态。
    if (socket !== ws) return

    const wasConnected = status.value === 'connected'
    socket = null
    status.value = 'closed'
    message.value = t(terminalCloseKey(wasConnected))
  }
}

/** 启动 mock 假终端（仅 `?mock=on` 且 DEV）。 */
function startMock(): void {
  void import('@/realtime/mock-terminal').then(({ createMockTerminal }) => {
    const fake = createMockTerminal()
    mockTerminal = fake

    fake.start(
      (chunk) => terminal?.write(chunk),
      () => {
        if (mockTerminal !== fake) return

        mockTerminal = null
        status.value = 'closed'
        message.value = t('container.terminal.closed')
      }
    )

    status.value = 'connected'
    everConnected.value = true
    terminal?.focus()
    syncSize()
  })
}

/** 主动断开（不显示「已断开」的提示：用户自己点的）。 */
function close(): void {
  const ws = socket
  socket = null
  ws?.close()

  mockTerminal?.dispose()
  mockTerminal = null

  status.value = 'idle'
  message.value = ''
}

onBeforeUnmount(() => {
  close()
  resizeObserver?.disconnect()
  themeObserver?.disconnect()
  resizeObserver = null
  themeObserver = null
  fitAddon = null
  terminal?.dispose()
  terminal = null
})
</script>

<template>
  <div class="flex flex-col gap-3">
    <!--
      控制条：shell 选择 + 打开/关闭。
      ⚠️ el-select 上的 w-* 必须带 `!`（AGENTS 陷阱 47）：Element Plus 把宽度写死成
      `var(--el-select-width)`，而 EP 样式表排在 uno.css 之后，同特异度下 EP 赢。
    -->
    <div class="flex flex-wrap items-center gap-2">
      <el-select v-model="shell" class="!w-40" :disabled="busy">
        <el-option v-for="item in SHELLS" :key="item" :label="item" :value="item" />
      </el-select>

      <el-button
        v-if="!busy"
        type="primary"
        :disabled="props.disabled"
        @click="open"
      >
        {{ t(openLabelKey) }}
      </el-button>

      <el-button v-else @click="close">
        {{ t('container.terminal.close') }}
      </el-button>

      <span class="text-xs text-text-secondary">{{ t(statusLabelKey) }}</span>

      <el-button
        v-if="!busy"
        text
        type="primary"
        @click="showAdvanced = !showAdvanced"
      >
        {{ t('container.terminal.advanced') }}
      </el-button>
    </div>

    <el-alert
      v-if="props.disabled"
      type="info"
      :closable="false"
      :title="props.disabledReason ? t(props.disabledReason) : t('container.terminal.notRunning')"
    />

    <el-alert type="warning" :closable="false" :title="t('container.terminal.warning')" />

    <!-- 自定义命令：折叠项。允许执行一条命令等价于「在 shell 里敲一遍」，默认不展示以免被误当成输入框。 -->
    <div v-if="showAdvanced && !busy" class="flex flex-col gap-2">
      <el-input
        v-model="command"
        :placeholder="t('container.terminal.commandPlaceholder')"
        :maxlength="2000"
        clearable
      />
      <span class="text-xs text-text-secondary">{{ t('container.terminal.commandHint') }}</span>
    </div>

    <!-- xterm 的宿主：必须有确定尺寸，否则 fit() 算不出行列数。 -->
    <div
      v-show="started"
      ref="terminalHost"
      class="terminal-host"
      :class="{ 'terminal-host--hidden': !started }"
    />

    <div v-if="message" class="text-xs text-text-secondary">{{ message }}</div>
  </div>
</template>

<style scoped>
/*
  行高 / 尺寸必须精确：xterm 依据宿主元素的实际像素尺寸计算行列数，
  用 UnoCSS 的 `h-80` 一类工具类在主题切换或字体加载完成时会抖动，
  因此这里把高度与字号写在 scoped CSS 里（与 LogViewport 的做法一致）。
*/
.terminal-host {
  height: 22rem;
  padding: 8px;
  overflow: hidden;
  border-radius: 6px;
  background: var(--app-terminal-bg, #1b1f27);
}

.terminal-host--hidden {
  display: none;
}
</style>
