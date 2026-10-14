import { describe, expect, it } from 'vitest'
import {
  formatEnvEntry,
  formatPortMapping,
  formatVolumeMount,
  isValidContainerPath,
  isValidEnvKey,
  isValidPort,
  parseEnvLine,
  parseEnvText,
  parsePortLine,
  parsePortText,
  parseVolumeLine,
  parseVolumeText,
  toPortPayload,
  toVolumePayload
} from '../src/utils/container-input'

/**
 * 容器创建的输入解析（0.0.14）。
 *
 * 这些函数决定「用户粘进来的命令行能不能原样建出容器」。判错时不会抛异常，
 * 而是悄悄建出一个与用户意图不同的容器——因此逐条钉住边界情形。
 */
describe('parseEnvLine / parseEnvText', () => {
  it('按第一个 = 切分，值里可以有等号', () => {
    // 连接串里全是等号：postgres://u:p@h/db?opt=1
    expect(parseEnvLine('DATABASE_URL=postgres://u:p@h/db?opt=1')).toEqual({
      key: 'DATABASE_URL',
      value: 'postgres://u:p@h/db?opt=1'
    })
  })

  it('值可以是空串（FOO= 是合法的「显式置空」）', () => {
    expect(parseEnvLine('DEBUG=')).toEqual({ key: 'DEBUG', value: '' })
  })

  it('没有 = / = 在首位 / 键名不合法都拒绝', () => {
    expect(parseEnvLine('NOEQUALS')).toBeNull()
    expect(parseEnvLine('=value')).toBeNull()
    expect(parseEnvLine('2FAST=x')).toBeNull()
    expect(parseEnvLine('WITH-DASH=x')).toBeNull()
  })

  it('报错时保留出错行之前的已解析内容', () => {
    // 这一点是批量粘贴编辑器不丢用户输入的前提（见 ParseResult 的注释）
    const result = parseEnvText('A=1\nBAD LINE\nC=3')
    expect(result.errorLine).toBe(2)
    expect(result.values).toEqual([{ key: 'A', value: '1' }])
  })

  it('空行被忽略但行号按原文计', () => {
    // 用户粘贴多行时难免带空行；「第 3 行」必须指原文的第 3 行
    const result = parseEnvText('A=1\n\nBROKEN')
    expect(result.errorLine).toBe(3)
  })
})

describe('parsePortLine', () => {
  it('只给容器端口 = 只暴露不绑定', () => {
    expect(parsePortLine('80')).toEqual({
      hostIp: null,
      hostPort: null,
      containerPort: 80,
      protocol: 'tcp'
    })
  })

  it('宿主端口:容器端口', () => {
    expect(parsePortLine('8080:80')).toEqual({
      hostIp: null,
      hostPort: 8080,
      containerPort: 80,
      protocol: 'tcp'
    })
  })

  it('宿主地址:宿主端口:容器端口', () => {
    expect(parsePortLine('127.0.0.1:8080:80')).toEqual({
      hostIp: '127.0.0.1',
      hostPort: 8080,
      containerPort: 80,
      protocol: 'tcp'
    })
  })

  it('协议后缀大写也能识别', () => {
    expect(parsePortLine('53:53/UDP')?.protocol).toBe('udp')
  })

  it(':80 等价于「宿主端口随机」', () => {
    expect(parsePortLine(':80')).toEqual({
      hostIp: null,
      hostPort: null,
      containerPort: 80,
      protocol: 'tcp'
    })
  })

  it('8080: 这类「容器端口位留空」被拒绝', () => {
    // 写它的人多半以为「容器 8080、宿主随机」，但按字段顺序它其实是
    // 「宿主 8080、容器为空」。Docker 自己会报错，这里也不猜。
    expect(parsePortLine('8080:')).toBeNull()
  })

  it('端口越界 / 非数字 / 协议不认识都拒绝', () => {
    expect(parsePortLine('0')).toBeNull()
    expect(parsePortLine('70000')).toBeNull()
    expect(parsePortLine('abc')).toBeNull()
    expect(parsePortLine('80/sctp')).toBeNull()
    expect(parsePortLine('1:2:3:4')).toBeNull()
  })
})

describe('parseVolumeLine', () => {
  it('POSIX 路径', () => {
    expect(parseVolumeLine('/host/data:/data')).toEqual({
      source: '/host/data',
      destination: '/data',
      readOnly: false
    })
  })

  it(':ro 后缀', () => {
    const result = parseVolumeLine('/etc/config:/config:ro')
    expect(result?.readOnly).toBe(true)
    expect(result?.destination).toBe('/config')
  })

  it('具名卷', () => {
    expect(parseVolumeLine('mydata:/var/lib/data')?.source).toBe('mydata')
  })

  it('Windows 盘符不会被冒号切坏', () => {
    // 按第一个冒号切会得到 source='C'，按最后一个切会丢掉目标路径的盘符
    expect(parseVolumeLine('C:\\data:/data')?.source).toBe('C:\\data')
    expect(parseVolumeLine('C:\\data:D:\\data')?.destination).toBe('D:\\data')
  })

  it('目标路径相对 / 缺来源都拒绝', () => {
    expect(parseVolumeLine('/host:relative/path')).toBeNull()
    expect(parseVolumeLine(':data')).toBeNull()
    expect(parseVolumeLine('justonepath')).toBeNull()
  })

  it('解析结果不携带挂载类型（由守护进程按来源自行判定）', () => {
    // 强制判定 bind/volume 会让某些写法因类型不符而失败；前端也不该伪造一个
    // 后端 `ContainerVolumeMapping` 并不存在的字段
    expect(Object.keys(parseVolumeLine('/host/data:/data') ?? {})).not.toContain('type')
  })
})

describe('格式化往返', () => {
  it('端口格式化后可被自己重新解析', () => {
    const mapping = { hostIp: null, hostPort: 8080, containerPort: 80, protocol: 'udp' as const }
    expect(parsePortLine(formatPortMapping(mapping))).toEqual(mapping)
  })

  it('挂载格式化后可被自己重新解析', () => {
    const mount = { source: '/a', destination: '/b', readOnly: true }
    expect(parseVolumeLine(formatVolumeMount(mount))).toEqual(mount)
  })

  it('环境变量格式化保留空值', () => {
    expect(formatEnvEntry({ key: 'A', value: '' })).toBe('A=')
  })
})

describe('校验谓词', () => {
  it('端口范围含端点', () => {
    expect(isValidPort(1)).toBe(true)
    expect(isValidPort(65535)).toBe(true)
    expect(isValidPort(0)).toBe(false)
    expect(isValidPort(65536)).toBe(false)
    expect(isValidPort(80.5)).toBe(false)
  })

  it('环境变量名允许字母数字下划线与前导下划线', () => {
    expect(isValidEnvKey('_PRIVATE')).toBe(true)
    expect(isValidEnvKey('A1_b')).toBe(true)
    expect(isValidEnvKey('1A')).toBe(false)
    expect(isValidEnvKey('A-B')).toBe(false)
  })

  it('容器内路径要求绝对', () => {
    expect(isValidContainerPath('/data')).toBe(true)
    expect(isValidContainerPath('D:\\data')).toBe(true)
    expect(isValidContainerPath('data')).toBe(false)
    expect(isValidContainerPath('./data')).toBe(false)
  })
})

describe('parsePortText / parseVolumeText 的行号语义', () => {
  it('端口第 2 行出错时报 2', () => {
    expect(parsePortText('80\n70000\n443').errorLine).toBe(2)
  })

  it('挂载第 3 行出错时报 3', () => {
    expect(parseVolumeText('/a:/a\n/b:/b\nbroken').errorLine).toBe(3)
  })

  it('全部合法时 errorLine 为 null', () => {
    expect(parsePortText('80\n443:8443').errorLine).toBeNull()
    expect(parseEnvText('A=1\nB=2').errorLine).toBeNull()
  })
})

describe('请求体转换', () => {
  it('只暴露不绑定时不发 hostPort', () => {
    // 关键：`null` 是「宿主端口随机」这一明确意图的编码，传给后端却与
    // 「字段未提供」难以区分；后端文档写的是「省略表示只暴露不绑定」
    const payload = toPortPayload(parsePortLine('80')!)
    expect(payload).toEqual({ containerPort: 80 })
    expect('hostPort' in payload).toBe(false)
  })

  it('不绑网卡时不发 hostIp', () => {
    expect(toPortPayload(parsePortLine('8080:80')!)).toEqual({ containerPort: 80, hostPort: 8080 })
  })

  it('绑定网卡时带上 hostIp', () => {
    expect(toPortPayload(parsePortLine('127.0.0.1:8080:80')!)).toEqual({
      containerPort: 80,
      hostPort: 8080,
      hostIp: '127.0.0.1'
    })
  })

  it('udp 映射 protocol 落到请求体的 type 字段', () => {
    // 命名不同（解析用 protocol / 请求体用 type）是唯一一处改名，只在这里转换
    expect(toPortPayload(parsePortLine('53:53/udp')!).type).toBe('udp')
  })

  it('tcp 不写 type（后端省略即 tcp）', () => {
    expect(toPortPayload(parsePortLine('80')!)).not.toHaveProperty('type')
  })

  it('挂载映射三个字段，且不伪造 type', () => {
    const mount = parseVolumeLine('/host/data:/data:ro')!
    expect(toVolumePayload(mount)).toEqual({ source: '/host/data', destination: '/data', readOnly: true })
  })

  it('文本 → 解析 → 请求体的端到端往返', () => {
    const { values } = parsePortText('127.0.0.1:8080:80/udp\n443')

    expect(values.map(toPortPayload)).toEqual([
      { containerPort: 80, hostPort: 8080, hostIp: '127.0.0.1', type: 'udp' },
      { containerPort: 443 }
    ])
  })
})
