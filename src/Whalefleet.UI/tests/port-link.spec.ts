import { describe, expect, it } from 'vitest'
import { buildPortUrl, ipRank, isWildcardIp } from '../src/utils/port-link'

/**
 * 端口链接的拼装。
 *
 * 这段逻辑的错误**在界面上看不出来**：链接照常渲染、照常可点，
 * 只是点开之后地址不对。因此只能靠单测钉住。
 */
describe('buildPortUrl', () => {
  it('IPv4 直接拼', () => {
    expect(buildPortUrl('192.168.1.10', 8080)).toBe('http://192.168.1.10:8080')
  })

  it('IPv6 必须加方括号', () => {
    // 不加方括号的话 `http://::1:8080` 会被解析成别的东西——
    // 症状是链接点开报「无法访问此网站」，而地址栏看着挺正常。
    expect(buildPortUrl('::1', 8080)).toBe('http://[::1]:8080')
  })

  it('通配地址照用不改写', () => {
    // 用户明确要求「用指定 IP，即使 0.0.0.0」：
    // 把 0.0.0.0 换成别的东西会让链接指向另一台机器。
    expect(buildPortUrl('0.0.0.0', 8080)).toBe('http://0.0.0.0:8080')
  })

  it('固定 http，不猜协议', () => {
    // 端口映射里没有任何协议信息（是不是 HTTPS 只有容器里才知道），
    // 猜错的话点开是「无法连接」，用户会误以为服务挂了。
    expect(buildPortUrl('10.0.0.1', 443)).toBe('http://10.0.0.1:443')
  })
})

describe('ipRank', () => {
  it('IPv4 家族优先于 IPv6 家族', () => {
    // `-p 8080:80` 会被 Docker 记成 0.0.0.0 与 :: 两条，
    // 两条都指「所有网卡」，但 IPv4 才是绝大多数场景真能访问到的那个。
    expect(ipRank('0.0.0.0')).toBeLessThan(ipRank('::'))
    expect(ipRank('192.168.1.10')).toBeLessThan(ipRank('::1'))
  })

  it('具体地址优先于通配地址', () => {
    expect(ipRank('192.168.1.10')).toBeLessThan(ipRank('0.0.0.0'))
    expect(ipRank('::1')).toBeLessThan(ipRank('::'))
  })

  it('未指定排在最后', () => {
    expect(ipRank(null)).toBeGreaterThan(ipRank('0.0.0.0'))
    expect(ipRank('')).toBeGreaterThan(ipRank('0.0.0.0'))
    expect(ipRank('   ')).toBeGreaterThan(ipRank('0.0.0.0'))
  })
})

describe('isWildcardIp', () => {
  it('通配与未指定都算通配', () => {
    expect(isWildcardIp('0.0.0.0')).toBe(true)
    expect(isWildcardIp('::')).toBe(true)
    expect(isWildcardIp(null)).toBe(true)
  })

  it('具体地址不是通配', () => {
    expect(isWildcardIp('192.168.1.10')).toBe(false)
    expect(isWildcardIp('::1')).toBe(false)
  })
})
