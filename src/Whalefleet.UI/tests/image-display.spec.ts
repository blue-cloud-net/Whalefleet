import { describe, expect, it } from 'vitest'
import {
  DANGLING_IMAGE_LABEL,
  isDangling,
  primaryImageName,
  shortDigest,
  shortImageId,
  splitImageReference,
  stackedTagCount
} from '../src/utils/image-display'

/**
 * 镜像展示逻辑的单元测试。
 *
 * 重点在 `splitImageReference`：它复刻的是 Docker 的**标签判据**
 * （冒号在最后一个斜杠之后），而「冒号后是纯数字就当标签」那种看似更聪明的写法
 * 会把 `registry:5000/nginx` 判成「已含标签 `5000/nginx`」——
 * 后端因此会收到 `image=registry` + `tag=5000/nginx`，拉取结果完全不是用户想要的。
 */
describe('镜像主展示名', () => {
  it('取第一个标签作为主名', () => {
    expect(primaryImageName(['nginx:latest', 'nginx:1.27'])).toBe('nginx:latest')
  })

  it('悬挂镜像显示占位名而不是空串', () => {
    expect(primaryImageName([])).toBe(DANGLING_IMAGE_LABEL)
    expect(primaryImageName(null)).toBe(DANGLING_IMAGE_LABEL)
    expect(primaryImageName(undefined)).toBe(DANGLING_IMAGE_LABEL)
  })

  it('标签数为 0 或 1 时不显示「+N」', () => {
    expect(stackedTagCount([])).toBe(0)
    expect(stackedTagCount(['nginx:latest'])).toBe(0)
    expect(stackedTagCount(['nginx:latest', 'nginx:1.27', 'nginx:1'])).toBe(2)
  })

  it('悬挂镜像的判定只看标签数量', () => {
    expect(isDangling([])).toBe(true)
    expect(isDangling(['alpine:3.20'])).toBe(false)
    expect(isDangling(null)).toBe(true)
  })
})

describe('镜像 ID 的短形态', () => {
  it('去掉 sha256: 前缀并截到 12 位', () => {
    expect(shortImageId('sha256:3f2c0b1a9d8e7f6c5b4a39281706f5e4')).toBe('3f2c0b1a9d8e')
  })

  it('短 ID 原样返回，不补零也不截断', () => {
    expect(shortImageId('abc123')).toBe('abc123')
  })

  it('空值返回空串（不由展示层决定占位符）', () => {
    expect(shortImageId(null)).toBe('')
    expect(shortImageId(undefined)).toBe('')
  })

  it('摘要只保留 @ 之后的哈希', () => {
    expect(shortDigest('nginx@sha256:0f2c1b3a4d5e6f708192a3b4c5d6e7f8')).toBe('0f2c1b3a4d5e')
  })

  it('没有 @ 时按普通 ID 处理', () => {
    expect(shortDigest('sha256:0f2c1b3a4d5e6f708192a3b4c5d6e7f8')).toBe('0f2c1b3a4d5e')
  })
})

describe('仓库:标签 的拆解（与 Docker 的判据一致）', () => {
  it('无标签时只回 image，由后端补 latest', () => {
    expect(splitImageReference('nginx')).toEqual({ image: 'nginx' })
  })

  it('含标签时拆成两条参数', () => {
    expect(splitImageReference('nginx:1.27')).toEqual({ image: 'nginx', tag: '1.27' })
  })

  it('⭐ 纯数字标签仍算标签（不能按「冒号后是数字」判定）', () => {
    expect(splitImageReference('nginx:1')).toEqual({ image: 'nginx', tag: '1' })
  })

  it('⭐ 带端口的仓库地址不算「已含标签」', () => {
    expect(splitImageReference('registry.local:5000/nginx')).toEqual({
      image: 'registry.local:5000/nginx'
    })
  })

  it('带端口的仓库地址后再跟标签，只拆最后一段', () => {
    expect(splitImageReference('registry.local:5000/team/nginx:1.27')).toEqual({
      image: 'registry.local:5000/team/nginx',
      tag: '1.27'
    })
  })

  it('结尾是空标签时按「未填标签」处理', () => {
    expect(splitImageReference('nginx:')).toEqual({ image: 'nginx' })
  })

  it('去掉首尾空白', () => {
    expect(splitImageReference('  nginx:alpine  ')).toEqual({ image: 'nginx', tag: 'alpine' })
  })

  it('空输入回空 image，由调用方给出必填提示', () => {
    expect(splitImageReference('')).toEqual({ image: '' })
    expect(splitImageReference('   ')).toEqual({ image: '' })
  })
})
