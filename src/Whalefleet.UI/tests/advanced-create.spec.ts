import { describe, expect, it } from 'vitest'
import {
  MIN_MEMORY_LIMIT_BYTES,
  buildHealthCheck,
  hasCapabilityConflict,
  parseCapabilities,
  parseCpuCores,
  parseDevices,
  parseLogOptions,
  parseMemoryMiB,
  parseSysctls,
  parseUlimits
} from '../src/utils/advanced-create'

/**
 * 0.3.0 高级创建参数的行式解析。
 *
 * 与 `container-input.spec.ts` 同一个原则：**判错时不抛异常，而是悄悄建出一个
 * 与用户意图不同的容器**。因此这里逐条钉住「会被静默接受但含义不对」的输入。
 */
describe('parseCapabilities', () => {
  it('空输入返回 skip（不发字段，而不是发空数组）', () => {
    // ⚠️ 空数组会被守护进程当成「显式清空」，与「我没填」语义不同
    expect(parseCapabilities('')).toEqual({ kind: 'skip' })
    expect(parseCapabilities('  \n  ')).toEqual({ kind: 'skip' })
  })

  it('每行一个能力名', () => {
    expect(parseCapabilities('NET_ADMIN\nSYS_PTRACE')).toEqual({
      kind: 'ok',
      value: ['NET_ADMIN', 'SYS_PTRACE']
    })
  })

  it('拒绝空行、重复项与小写名', () => {
    // 空行几乎总是前端把一个空输入框也提交了；静默跳过会让用户以为勾的那项生效了
    expect(parseCapabilities('NET_ADMIN\n\nSYS_PTRACE')).toEqual({
      kind: 'error',
      reason: 'format'
    })
    expect(parseCapabilities('NET_ADMIN\nNET_ADMIN')).toEqual({
      kind: 'error',
      reason: 'duplicate'
    })
    expect(parseCapabilities('net_admin')).toEqual({ kind: 'error', reason: 'format' })
  })
})

describe('hasCapabilityConflict', () => {
  it('既增又删同一项时报冲突', () => {
    // Docker 不报错（两个列表都会发过去），生效哪一个取决于守护进程内部顺序
    expect(hasCapabilityConflict(['NET_ADMIN'], ['NET_ADMIN'])).toBe(true)
  })

  it('不重叠时无冲突', () => {
    expect(hasCapabilityConflict(['NET_ADMIN'], ['ALL'])).toBe(false)
    expect(hasCapabilityConflict([], ['ALL'])).toBe(false)
  })
})

describe('parseSysctls', () => {
  it('按第一个 = 切分，值里可以再有 =', () => {
    expect(parseSysctls('net.ipv4.ip_forward=1')).toEqual({
      kind: 'ok',
      value: { 'net.ipv4.ip_forward': '1' }
    })
  })

  it('值允许为空串（置空即恢复默认）', () => {
    expect(parseSysctls('vm.swappiness=')).toEqual({ kind: 'ok', value: { 'vm.swappiness': '' } })
  })

  it('拒绝缺等号、空键与重复键', () => {
    expect(parseSysctls('NOEQUALS')).toEqual({ kind: 'error', reason: 'format' })
    expect(parseSysctls('=1')).toEqual({ kind: 'error', reason: 'format' })
    expect(parseSysctls('a=1\na=2')).toEqual({ kind: 'error', reason: 'duplicate' })
  })
})

describe('parseUlimits', () => {
  it('只有名字时软硬都留空（由后端补齐）', () => {
    expect(parseUlimits('nofile')).toEqual({ kind: 'ok', value: [{ name: 'nofile' }] })
  })

  it('支持 name=soft 与 name=soft:hard', () => {
    expect(parseUlimits('nofile=1024')).toEqual({
      kind: 'ok',
      value: [{ name: 'nofile', soft: 1024 }]
    })
    expect(parseUlimits('nofile=1024:4096')).toEqual({
      kind: 'ok',
      value: [{ name: 'nofile', soft: 1024, hard: 4096 }]
    })
  })

  it('拒绝负数、空限制值与重复名称', () => {
    expect(parseUlimits('nofile=-1')).toEqual({ kind: 'error', reason: 'format' })
    expect(parseUlimits('nofile=')).toEqual({ kind: 'error', reason: 'format' })
    expect(parseUlimits('nofile=1\nnofile=2')).toEqual({ kind: 'error', reason: 'duplicate' })
  })
})

describe('parseDevices', () => {
  it('支持 host / host:container / host:container:perms', () => {
    expect(parseDevices('/dev/snd')).toEqual({
      kind: 'ok',
      value: [{ hostPath: '/dev/snd' }]
    })
    expect(parseDevices('/dev/snd:/dev/snd')).toEqual({
      kind: 'ok',
      value: [{ hostPath: '/dev/snd', containerPath: '/dev/snd' }]
    })
    expect(parseDevices('/dev/snd:/dev/snd:rw')).toEqual({
      kind: 'ok',
      value: [{ hostPath: '/dev/snd', containerPath: '/dev/snd', permissions: 'rw' }]
    })
  })

  it('拒绝非法权限字符与空路径', () => {
    // `rwx` 里的 x 不是合法的 cgroup 设备权限；放行会被守护进程拒成一句看不懂的错
    expect(parseDevices('/dev/snd:/dev/snd:rwx')).toEqual({ kind: 'error', reason: 'format' })
    expect(parseDevices(':/dev/snd')).toEqual({ kind: 'error', reason: 'format' })
  })
})

describe('buildHealthCheck', () => {
  it('未勾选时返回 skip（继承镜像的 HEALTHCHECK）', () => {
    expect(buildHealthCheck(false, '', {
      interval: '', timeout: '', startPeriod: '', retries: ''
    })).toEqual({ kind: 'skip' })
  })

  it('勾选但命令为空时报错（而不是发空 test 数组）', () => {
    expect(buildHealthCheck(true, '   ', {
      interval: '', timeout: '', startPeriod: '', retries: ''
    })).toEqual({ kind: 'error', reason: 'format' })
  })

  it('按空白拆命令并带上四项可选数值', () => {
    const parsed = buildHealthCheck(true, 'CMD-SHELL curl -f http://x/ || exit 1', {
      interval: '30', timeout: '5', startPeriod: '10', retries: '3'
    })

    expect(parsed).toEqual({
      kind: 'ok',
      value: {
        kind: 'custom',
        test: ['CMD-SHELL', 'curl', '-f', 'http://x/', '||', 'exit', '1'],
        intervalSeconds: 30,
        timeoutSeconds: 5,
        startPeriodSeconds: 10,
        retries: 3
      }
    })
  })

  it('拒绝非正数的间隔 / 重试次数', () => {
    // retries=0 在 Docker 里的含义是「永不判定失败」，那是不检查而不是很严格地检查
    expect(
      buildHealthCheck(true, 'CMD-SHELL true', {
        interval: '0', timeout: '', startPeriod: '', retries: ''
      })
    ).toEqual({ kind: 'error', reason: 'format' })

    expect(
      buildHealthCheck(true, 'CMD-SHELL true', {
        interval: '', timeout: '', startPeriod: '', retries: '0'
      })
    ).toEqual({ kind: 'error', reason: 'format' })
  })
})

describe('parseMemoryMiB', () => {
  it('空输入返回 skip', () => {
    expect(parseMemoryMiB('')).toEqual({ kind: 'skip' })
  })

  it('0 合法（创建时 0 就是「不限制」）', () => {
    // ⚠️ 与热更新相反：那里 0 是非法的静默无操作，这里 0 是本义的「不限制」
    expect(parseMemoryMiB('0')).toEqual({ kind: 'ok', value: 0 })
  })

  it('MiB 转字节，并守住 6 MiB 下限', () => {
    expect(parseMemoryMiB('512')).toEqual({ kind: 'ok', value: 512 * 1024 * 1024 })
    expect(parseMemoryMiB('6')).toEqual({ kind: 'ok', value: MIN_MEMORY_LIMIT_BYTES })
    expect(parseMemoryMiB('5')).toEqual({ kind: 'error', reason: 'format' })
  })

  it('拒绝负数与非整数', () => {
    expect(parseMemoryMiB('-1')).toEqual({ kind: 'error', reason: 'format' })
    expect(parseMemoryMiB('1.5')).toEqual({ kind: 'error', reason: 'format' })
  })
})

describe('parseCpuCores', () => {
  it('核数转十亿分之一核', () => {
    expect(parseCpuCores('1.5')).toEqual({ kind: 'ok', value: 1_500_000_000 })
    expect(parseCpuCores('0')).toEqual({ kind: 'ok', value: 0 })
    expect(parseCpuCores('1024')).toEqual({ kind: 'ok', value: 1024_000_000_000 })
  })

  it('拒绝超过 1024 核与负数', () => {
    expect(parseCpuCores('1025')).toEqual({ kind: 'error', reason: 'format' })
    expect(parseCpuCores('-1')).toEqual({ kind: 'error', reason: 'format' })
  })
})

describe('parseLogOptions', () => {
  it('按 key=value 每行一对解析', () => {
    expect(parseLogOptions('max-size=10m\nmax-file=3')).toEqual({
      kind: 'ok',
      value: { 'max-size': '10m', 'max-file': '3' }
    })
  })

  it('拒绝缺等号的行', () => {
    expect(parseLogOptions('max-size')).toEqual({ kind: 'error', reason: 'format' })
  })
})
