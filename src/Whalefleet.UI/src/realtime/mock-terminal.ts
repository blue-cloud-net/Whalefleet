/**
 * mock 模式下的假终端（0.8.0）。
 *
 * ## 为什么需要它
 * 终端走的是**原生 WebSocket**，与 SignalR 同理——MSW 只能拦 HTTP 请求，
 * 拦不住升级握手。因此 `?mock=on` 时必须有另一条通路，否则终端面板在 mock 下
 * 永远停在「连接中」，而组件里的状态机、关闭提示、resize 处理全都没法验证。
 *
 * ## 与真实终端的差异（刻意的）
 * - **没有容器**：只有三条内置命令（`help` / `clear` / `exit`），其余命令一律回
 *   「mock 终端不支持」。这里不是要模拟一个 shell，而是要让界面**走到每一个分支**。
 * - **同步回显**：真实终端的内容由 `docker exec` 产生，有网络延迟；
 *   这里立刻回显，避免 mock 下出现"看起来卡住"的假象。
 *
 * ⚠️ **调用点必须自己写 `import.meta.env.DEV`**（见 `mocks/switch.ts` 的说明）：
 * 本模块如果被无条件静态引入，就会把 mock 代码打进生产产物。
 */

/** 假终端的对外接口（与组件里真实 WebSocket 的用法一一对应）。 */
export interface MockTerminal {
  /**
   * 开始会话。
   *
   * @param onOutput 输出回调（组件把它写进 xterm）。
   * @param onClose 结束回调（组件据此显示「已断开」）。
   */
  start(onOutput: (chunk: string) => void, onClose: () => void): void
  /** 写入用户输入（等价于真实终端的 stdin）。 */
  write(data: string): void
  /** 调整尺寸（mock 下只记下来，不影响输出）。 */
  resize(columns: number, rows: number): void
  /** 结束会话（幂等）。 */
  dispose(): void
}

/** 提示符。 */
const PROMPT = '\r\n$ '

/** 内置命令的帮助文本。 */
const HELP = [
  '\r\nmock 终端：不会真的连到容器。',
  '  help   显示这段说明',
  '  clear  清屏',
  '  exit   关闭会话',
  '其它命令会返回「不支持」——它验证的是界面分支，不是 shell 语义。'
].join('\r\n')

/**
 * 创建假终端。
 *
 * @returns 假终端实例。
 */
export function createMockTerminal(): MockTerminal {
  let onOutput: ((chunk: string) => void) | null = null
  let onClose: (() => void) | null = null
  let line = ''
  let closed = false

  const emit = (chunk: string): void => {
    onOutput?.(chunk)
  }

  const close = (): void => {
    if (closed) return

    closed = true
    onClose?.()
  }

  const run = (): void => {
    const command = line.trim()
    line = ''

    if (command.length === 0) {
      emit(PROMPT)

      return
    }

    if (command === 'exit') {
      emit('\r\n再见。')
      close()

      return
    }

    if (command === 'clear') {
      emit('\u001b[2J\u001b[H$ ')

      return
    }

    emit(command === 'help' ? HELP : `${command}: mock 终端不支持该命令（试 help）`)
    emit(PROMPT)
  }

  return {
    start(output, closeHandler) {
      onOutput = output
      onClose = closeHandler

      emit('Whalefleet mock 终端（?mock=on）——输入 help 查看可用命令。')
      emit(PROMPT)
    },

    write(data) {
      if (closed) return

      for (const char of data) {
        if (char === '\r' || char === '\n') {
          run()
          continue
        }

        // Ctrl+C：丢掉当前行，回到提示符（与 shell 的行为一致）。
        if (char === '\u0003') {
          line = ''
          emit('^C')
          emit(PROMPT)
          continue
        }

        // Ctrl+D：EOF。
        if (char === '\u0004') {
          emit('\r\n')
          close()
          continue
        }

        // DEL / 退格：删掉一个字符并把光标退一格。
        if (char === '\u007f' || char === '\b') {
          if (line.length > 0) {
            line = line.slice(0, -1)
            emit('\b \b')
          }

          continue
        }

        if (char < ' ') continue

        line += char
        emit(char)
      }
    },

    resize() {
      // mock 下尺寸不影响输出：这里保留接口，让组件的 resize 分支也有东西可调。
    },

    dispose() {
      closed = true
      onOutput = null
      onClose = null
    }
  }
}
