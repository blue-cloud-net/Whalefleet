import { describe, expect, it } from 'vitest'
import { buildResizeFrame, buildTerminalSocketUrl, terminalCloseKey } from '../src/utils/terminal-url'

/**
 * 终端地址与协议帧的拼装（0.8.0）。
 *
 * 这段逻辑的错误**在界面上看不出来**：面板照常打开，只是连不上（或画歪）。
 * 而连不上的原因浏览器只会给一个 `close` 事件（通常 1006），拿不到任何细节。
 * 因此只能靠单测把每个坑钉住。
 */
describe('终端 WebSocket 地址', () => {
  const httpLocation = { protocol: 'http:', host: 'localhost:30123' }

  it('http 页面用 ws:', () => {
    const url = buildTerminalSocketUrl({ hostId: 'local', token: 'abc' }, httpLocation)

    expect(url.startsWith('ws://localhost:30123/ws/hosts/local/containers/exec?')).toBe(true)
  })

  it('https 页面必须用 wss:（否则浏览器直接拦掉且不给有用的错）', () => {
    const url = buildTerminalSocketUrl(
      { hostId: 'local', token: 'abc' },
      { protocol: 'https:', host: 'docker.example.com' }
    )

    expect(url.startsWith('wss://docker.example.com/')).toBe(true)
  })

  it('hostId 编码一次，令牌交给 URLSearchParams（不做二次编码）', () => {
    const url = buildTerminalSocketUrl(
      { hostId: 'prod 01', token: 'eyJhbGciOi.eyJzdWIiOi-abc_def' },
      httpLocation
    )

    expect(url).toContain('/ws/hosts/prod%2001/containers/exec')

    // JWT 的字符集（`.` / `-` / `_`）在 query 里无需转义，应原样出现
    expect(url).toContain('token=eyJhbGciOi.eyJzdWIiOi-abc_def')

    // 二次编码的典型症状：`%` 变成 `%25`（见 AGENTS.md 陷阱 12 的同类错误）
    expect(url).not.toContain('%2520')
  })

  it('尺寸只在为正数时才带上（守护进程对 0 会直接回 400）', () => {
    const withSize = buildTerminalSocketUrl(
      { hostId: 'local', token: 'abc', columns: 120, rows: 30 },
      httpLocation
    )
    const withoutSize = buildTerminalSocketUrl(
      { hostId: 'local', token: 'abc', columns: 0, rows: -1 },
      httpLocation
    )

    expect(withSize).toContain('cols=120')
    expect(withSize).toContain('rows=30')
    expect(withoutSize).not.toContain('cols=')
    expect(withoutSize).not.toContain('rows=')
  })
})

describe('resize 控制帧', () => {
  it('是文本帧 JSON（与二进制终端数据区分开）', () => {
    expect(buildResizeFrame(120, 30)).toBe('{"type":"resize","cols":120,"rows":30}')
  })

  it('非正数 / 非有限值返回 null（调用方跳过发送）', () => {
    expect(buildResizeFrame(0, 30)).toBeNull()
    expect(buildResizeFrame(120, 0)).toBeNull()
    expect(buildResizeFrame(Number.NaN, 30)).toBeNull()
    expect(buildResizeFrame(Number.POSITIVE_INFINITY, 30)).toBeNull()
  })
})

describe('结束文案', () => {
  it('连上过 → 会话已断开；没连上 → 连接失败', () => {
    expect(terminalCloseKey(true)).toBe('container.terminal.closed')
    expect(terminalCloseKey(false)).toBe('container.terminal.connectFailed')
  })
})
