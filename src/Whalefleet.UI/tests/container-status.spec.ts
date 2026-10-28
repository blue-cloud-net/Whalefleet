import { describe, expect, it } from 'vitest'
import {
  CONTAINER_DISPLAY_LABEL_KEY,
  CONTAINER_DISPLAY_TONE,
  CONTAINER_TONE_LABEL_KEY,
  containerDisplayState,
  containerTone,
  describePorts,
  formatBytes
} from '../src/utils/container-status'

/**
 * 容器状态映射与格式化。
 *
 * 这几条是「看一眼就知道对不对」的逻辑，但正是它们最容易在改动中被悄悄改错：
 * 颜色错一位（暂停被染成红色）只有真看到界面才会发现，而那时已经在走查流程里了。
 */
describe('containerTone', () => {
  it('运行中 → ok，已停止 / 已死亡 → down', () => {
    expect(containerTone('running')).toBe('ok')
    expect(containerTone('exited')).toBe('down')
    expect(containerTone('dead')).toBe('down')
  })

  it('created（建好但从未启动）→ down，不是「未知」', () => {
    // ⚠️ 回归用例：它曾被归进 idle → 徽标显示「未知」。
    // 但「建好了还没启动」是一个**我们明确知道**的状态，把它和
    // 「守护进程给了个不认识的状态」混在一起，会让人去查一个并不存在的异常。
    // 动作集也与已停止一致（可 start / remove，不能 stop / pause / kill）。
    expect(containerTone('created')).toBe('down')
  })

  it('过渡态独立成一档，不落进 ok 或 down', () => {
    // paused 的语义是「既没正常运行、也没出错退出」——
    // 归进 ok 会让用户以为服务在跑，归进 down 会让他去查日志。
    expect(containerTone('paused')).toBe('warn')
    expect(containerTone('restarting')).toBe('warn')
    expect(containerTone('removing')).toBe('warn')
  })

  it('大小写不敏感（守护进程的措辞不能假定）', () => {
    expect(containerTone('RUNNING')).toBe('ok')
    expect(containerTone('Paused')).toBe('warn')
  })

  it('未登记与空值一律「未知」，不猜颜色', () => {
    expect(containerTone('some-future-state')).toBe('idle')
    expect(containerTone(undefined)).toBe('idle')
    expect(containerTone(null)).toBe('idle')
    expect(containerTone('')).toBe('idle')
  })

  it('每一档都有文案 key（漏一个就会渲染出空白药丸）', () => {
    for (const tone of ['ok', 'down', 'warn', 'idle'] as const) {
      expect(CONTAINER_TONE_LABEL_KEY[tone]).toBeTruthy()
    }
  })
})

/**
 * 状态与健康的合成（0.6.0）。
 *
 * 合成逻辑是「一眼就知道对不对」但最容易改错的那种：
 * 把「先判 running」写成「先判健康」，停掉的容器就会显示成「健康」——
 * 而停掉的容器健康状态是 `none`，它会显示成「运行中」，更糟。
 */
describe('containerDisplayState', () => {
  it('运行中 + 健康检查通过 → 健康', () => {
    expect(containerDisplayState('running', 'healthy')).toBe('healthy')
  })

  it('运行中 + 健康检查失败 → 不健康', () => {
    expect(containerDisplayState('running', 'unhealthy')).toBe('unhealthy')
  })

  it('运行中 + 没配健康检查 → 运行中（不是「无」）', () => {
    expect(containerDisplayState('running', 'none')).toBe('running')
    expect(containerDisplayState('running', 'starting')).toBe('running')
    expect(containerDisplayState('running', null)).toBe('running')
  })

  it('已停止优先于健康：停掉的容器没有健康检查可言', () => {
    // 健康状态是 none，若先判健康就会误显示成「运行中」。
    expect(containerDisplayState('exited', 'none')).toBe('stopped')
    expect(containerDisplayState('exited', 'healthy')).toBe('stopped')
    expect(containerDisplayState('dead', null)).toBe('stopped')
    // 建好但从未启动：界面上读作「已停止」，而不是「未知」。
    expect(containerDisplayState('created', null)).toBe('stopped')
    expect(containerDisplayState('Created', 'none')).toBe('stopped')
  })

  it('过渡态仍是过渡态，不被健康覆盖', () => {
    expect(containerDisplayState('paused', 'healthy')).toBe('transition')
    expect(containerDisplayState('restarting', 'unhealthy')).toBe('transition')
  })

  it('大小写不敏感', () => {
    expect(containerDisplayState('RUNNING', 'healthy')).toBe('healthy')
    expect(containerDisplayState('Exited', null)).toBe('stopped')
  })

  it('未登记状态与空值 → 未知', () => {
    expect(containerDisplayState('some-future-state', 'healthy')).toBe('unknown')
    expect(containerDisplayState(undefined, null)).toBe('unknown')
    expect(containerDisplayState(null, null)).toBe('unknown')
    expect(containerDisplayState('', null)).toBe('unknown')
  })

  it('不健康是黄（warn）不是红（down）', () => {
    // 容器还在跑。红色会让人以为它已经挂了，把「重启它」变成「立刻处理」。
    expect(CONTAINER_DISPLAY_TONE.unhealthy).toBe('warn')
    expect(CONTAINER_DISPLAY_TONE.healthy).toBe('ok')
    expect(CONTAINER_DISPLAY_TONE.running).toBe('ok')
    expect(CONTAINER_DISPLAY_TONE.stopped).toBe('down')
  })

  it('每一档都有文案 key（漏一个就会渲染出空白药丸）', () => {
    for (const state of [
      'healthy',
      'unhealthy',
      'running',
      'stopped',
      'transition',
      'unknown'
    ] as const) {
      expect(CONTAINER_DISPLAY_LABEL_KEY[state]).toBeTruthy()
    }
  })
})

describe('describePorts', () => {
  it('有宿主端口时同时给出两侧', () => {
    const result = describePorts([
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' }
    ])

    expect(result).toHaveLength(1)
    // 通配地址不进展示文本：它只表达「监听所有网卡」，
    // 写成 `0.0.0.0:8080→80/tcp` 会让人以为「访问 0.0.0.0 就能连上」。
    expect(result[0].text).toBe('8080→80/tcp')
  })

  it('绑定了具体地址时把地址写进展示文本', () => {
    const result = describePorts([
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '192.168.1.10' }
    ])

    expect(result[0].text).toBe('192.168.1.10:8080→80/tcp')
  })

  it('只暴露不映射的端口不显示', () => {
    // 守护进程会把镜像 EXPOSE 过的端口一并报回来，即使它没映射到宿主。
    // MySQL 官方镜像 EXPOSE 了 3306 与 33060（X Protocol），
    // 而实际只映射 3306——那一行 `33060/tcp` 是纯噪声：
    // 没映射到宿主的端口从宿主机外根本连不上，列出来只会让人以为「这里还有个服务」。
    const result = describePorts([
      { privatePort: 443, publicPort: null, type: 'tcp', ip: null }
    ])

    expect(result).toHaveLength(0)
  })

  it('已映射与未映射混在一起时只留已映射的那些', () => {
    const result = describePorts([
      { privatePort: 3306, publicPort: 3306, type: 'tcp', ip: '0.0.0.0' },
      { privatePort: 33060, publicPort: null, type: 'tcp', ip: null }
    ])

    expect(result).toHaveLength(1)
    expect(result[0].text).toBe('3306→3306/tcp')
  })

  it('保留协议', () => {
    const result = describePorts([
      { privatePort: 53, publicPort: 53, type: 'udp', ip: '0.0.0.0' }
    ])

    expect(result[0].text).toBe('53→53/udp')
  })

  it('IPv4 与 IPv6 绑同一个端口时合并成一行', () => {
    // `-p 8080:80` 在真实 Docker 上就是两条记录（0.0.0.0 与 ::）。
    // 逐条渲染时同一个端口占两行，用户会以为是两个端口。
    const result = describePorts([
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' },
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '::' }
    ])

    expect(result).toHaveLength(1)
    expect(result[0].text).toBe('8080→80/tcp')
  })

  it('同一端口绑了多个具体地址时只显示首个，其余记在 extraIps', () => {
    const result = describePorts([
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '192.168.1.10' },
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '192.168.1.11' }
    ])

    expect(result).toHaveLength(1)
    expect(result[0].text).toBe('192.168.1.10:8080→80/tcp')
    expect(result[0].extraIps).toEqual(['192.168.1.11'])
  })

  it('不同宿主端口算不同组', () => {
    // `-p 8080:80 -p 8081:80` 是两个不同的映射，不能被合并掉一个。
    const result = describePorts([
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' },
      { privatePort: 80, publicPort: 8081, type: 'tcp', ip: '0.0.0.0' }
    ])

    expect(result).toHaveLength(2)
  })

  it('TCP 生成链接，UDP 不生成', () => {
    // UDP 没有浏览器能直接打开的语义，做成链接只会让人点开发现打不开。
    const tcp = describePorts([{ privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' }])
    const udp = describePorts([{ privatePort: 53, publicPort: 53, type: 'udp', ip: '0.0.0.0' }])

    expect(tcp[0].url).toBe('http://0.0.0.0:8080')
    expect(udp[0].url).toBeNull()
  })

  it('IPv4 与 IPv6 同时绑定时链接优先用 IPv4', () => {
    const result = describePorts([
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '::' },
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' }
    ])

    expect(result[0].url).toBe('http://0.0.0.0:8080')
  })

  it('IPv6 地址的链接要加方括号', () => {
    const result = describePorts([
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '::1' }
    ])

    expect(result[0].url).toBe('http://[::1]:8080')
  })

  it('按宿主端口升序排列', () => {
    const result = describePorts([
      { privatePort: 8080, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' },
      { privatePort: 80, publicPort: 8080, type: 'tcp', ip: '0.0.0.0' }
    ])

    expect(result.map((item) => item.text)).toEqual(['8080→80/tcp', '8080→8080/tcp'])
  })
})

describe('formatBytes', () => {
  const units = { b: 'B', kib: 'KiB', mib: 'MiB', gib: 'GiB' }

  it('按量级选单位', () => {
    expect(formatBytes(512, units)).toBe('512 B')
    expect(formatBytes(2048, units)).toBe('2.0 KiB')
    expect(formatBytes(5 * 1024 * 1024, units)).toBe('5.0 MiB')
    expect(formatBytes(3 * 1024 * 1024 * 1024, units)).toBe('3.0 GiB')
  })

  it('null / undefined 给占位符而不是 0 B', () => {
    // 「未限制」与「限制为 0 字节」是两件完全不同的事，后者根本跑不起来。
    expect(formatBytes(null, units)).toBe('—')
    expect(formatBytes(undefined, units)).toBe('—')
  })

  it('负数与 NaN 不抛异常，给占位符', () => {
    expect(formatBytes(-1, units)).toBe('—')
    expect(formatBytes(Number.NaN, units)).toBe('—')
  })
})
