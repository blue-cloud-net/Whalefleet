import { describe, expect, it } from 'vitest'
import {
  DOCKER_HUB_SERVER_ADDRESS,
  describeRepositoryTest,
  findRepositoryForReference,
  repositoryAddressOf,
  stripImageTag
} from '../src/utils/repository-display'

/**
 * Registry 展示逻辑的守卫。
 *
 * 这些用例钉的是**与后端 `ImageReference` 一致的判据**。它们看着琐碎，
 * 但每一条都对应一个真实误判：
 * - `library/nginx` 被当成主机 `library` → 推送时去连一个不存在的主机
 * - `harbor.local:5000/app` 的 `:5000` 被当成标签 → 地址段变成 `harbor.local`
 * - `docker.io` 与 `registry-1.docker.io` 混用 → Docker Hub 的凭据永远匹配不上
 */
describe('repositoryAddressOf', () => {
  it('无前缀的仓库归 Docker Hub', () => {
    expect(repositoryAddressOf('nginx')).toBe(DOCKER_HUB_SERVER_ADDRESS)
    expect(repositoryAddressOf('nginx:latest')).toBe(DOCKER_HUB_SERVER_ADDRESS)
  })

  it('官方镜像命名空间不是主机（首段无点无冒号）', () => {
    // 这条最容易错：`library` 看起来像一个主机名，但它只是 Docker Hub 的命名空间
    expect(repositoryAddressOf('library/nginx:1.27')).toBe(DOCKER_HUB_SERVER_ADDRESS)
    expect(repositoryAddressOf('myorg/app:v1')).toBe(DOCKER_HUB_SERVER_ADDRESS)
  })

  it('带端口的主机：端口不会被当成标签', () => {
    expect(repositoryAddressOf('harbor.local:5000/app:v1')).toBe('harbor.local:5000')
    expect(repositoryAddressOf('127.0.0.1:5000/app')).toBe('127.0.0.1:5000')
  })

  it('localhost 无端口也算主机', () => {
    expect(repositoryAddressOf('localhost/app:v1')).toBe('localhost')
    expect(repositoryAddressOf('LOCALHOST/app')).toBe('localhost')
  })

  it('结果一律小写（后端按 COLLATE NOCASE 查库）', () => {
    expect(repositoryAddressOf('Harbor.Local:5000/app:v1')).toBe('harbor.local:5000')
  })

  it('空输入返回空串而不是 Docker Hub', () => {
    // 返回 Docker Hub 会让「还没填引用」显示成「将推送到 Docker Hub」，属于凭空断言
    expect(repositoryAddressOf('')).toBe('')
    expect(repositoryAddressOf('   ')).toBe('')
  })
})

describe('stripImageTag', () => {
  it('去掉标签但保留端口', () => {
    expect(stripImageTag('harbor.local:5000/app:v1')).toBe('harbor.local:5000/app')
    expect(stripImageTag('harbor.local:5000/app')).toBe('harbor.local:5000/app')
    expect(stripImageTag('nginx:1')).toBe('nginx')
  })

  it('纯数字标签也要去掉（不能按「冒号右侧是数字」判断端口）', () => {
    expect(stripImageTag('nginx:1')).toBe('nginx')
    expect(stripImageTag('registry:5000/nginx')).toBe('registry:5000/nginx')
  })

  it('尾部孤立冒号被清掉', () => {
    expect(stripImageTag('nginx:')).toBe('nginx')
  })
})

describe('describeRepositoryTest', () => {
  it('连通且认证通过 → ok', () => {
    expect(describeRepositoryTest({ reachable: true, authenticated: true })).toEqual({
      tone: 'ok',
      messageKey: 'repository.test.ok'
    })
  })

  it('连通但未认证 → warn（不是 error：匿名仓库本来就不需要凭据）', () => {
    expect(describeRepositoryTest({ reachable: true, authenticated: false }).tone).toBe('warn')
  })

  it('不连通 → error，与「认证失败」是不同的处置', () => {
    const verdict = describeRepositoryTest({ reachable: false, authenticated: false })
    expect(verdict.tone).toBe('error')
    expect(verdict.messageKey).toBe('repository.test.unreachable')
  })

  it('矛盾组合（未连通却声称已认证）按不连通处理', () => {
    // 把矛盾数据解释成成功，比显示一个保守的失败危险得多
    expect(describeRepositoryTest({ reachable: false, authenticated: true }).tone).toBe('error')
  })
})

describe('findRepositoryForReference', () => {
  const repositories = [
    { id: 'a', serverAddress: 'harbor.local:5000' },
    { id: 'b', serverAddress: 'registry.example.com' }
  ]

  it('按地址段命中（大小写不敏感）', () => {
    expect(findRepositoryForReference('harbor.local:5000/app:v1', repositories)?.id).toBe('a')
    expect(findRepositoryForReference('Registry.Example.com/app:v1', repositories)?.id).toBe('b')
  })

  it('无前缀的引用不会命中任何私有配置（它归 Docker Hub）', () => {
    expect(findRepositoryForReference('nginx:latest', repositories)).toBeNull()
  })

  it('空引用返回 null', () => {
    expect(findRepositoryForReference('', repositories)).toBeNull()
  })
})
