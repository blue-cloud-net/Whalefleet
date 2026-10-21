import { describe, expect, it } from 'vitest'
import { dropBoundaryDuplicates } from '../src/composables/useContainerLogStream'
import type { ContainerLogLine } from '../src/types/api'

/**
 * 向上加载更早日志时的**边界去重**。
 *
 * ## 为什么需要去重
 * `until` 参数是**含边界**的（Docker 的语义是「时间戳 ≤ until 的都返回」）。
 * 于是「更早的一页」会把边界那一秒的行也带回来，而当前列表末尾已经有它们——
 * 不去掉就会在往上加载时看到重复行。
 *
 * ## 去重规则
 * 边界秒的行在「更早的一页」里是**连续的前缀**。设该前缀长度为 n，
 * 当前列表末尾已有 k 行同秒的，则这一页多带回了 n - k 行**更早**的同秒行——
 * 它们要保留；多出来的**最后 k 行**（与当前列表重复）要去掉。
 */
function line(timestamp: string | null, text: string, seq = 1): ContainerLogLine {
  return { seq, timestamp, line: text }
}

describe('dropBoundaryDuplicates', () => {
  const boundary = '2026-10-20T10:00:00Z'

  it('更早的一页里没有边界秒的行 → 原样返回', () => {
    const older = [line('2026-10-20T09:59:59Z', 'a'), line('2026-10-20T09:59:58Z', 'b')]

    expect(dropBoundaryDuplicates(older, boundary, 2)).toHaveLength(2)
  })

  it('边界秒的行全部重复 → 全部去掉', () => {
    // 更早的一页里边界秒只有 1 行，当前列表末尾也有 1 行 → 去掉
    const older = [line(boundary, 'dup')]

    expect(dropBoundaryDuplicates(older, boundary, 1)).toHaveLength(0)
  })

  it('边界秒有更多更早的行 → 保留比当前列表更早的那部分', () => {
    // 更早的一页里边界秒有 3 行，当前列表末尾只有 1 行 → 保留前 2 行
    const older = [
      line(boundary, 'older-1'),
      line(boundary, 'older-2'),
      line(boundary, 'dup')
    ]

    const result = dropBoundaryDuplicates(older, boundary, 1)

    expect(result).toHaveLength(2)
    expect(result[0]?.line).toBe('older-1')
    expect(result[1]?.line).toBe('older-2')
  })

  it('边界秒之后的行不受影响（不在前缀里）', () => {
    const older = [
      line(boundary, 'dup'),
      line('2026-10-20T10:00:01Z', 'later')
    ]

    const result = dropBoundaryDuplicates(older, boundary, 1)

    expect(result).toHaveLength(1)
    expect(result[0]?.line).toBe('later')
  })

  it('当前列表没有同秒行 → 不去重', () => {
    const older = [line(boundary, 'a'), line(boundary, 'b')]

    expect(dropBoundaryDuplicates(older, boundary, 0)).toHaveLength(2)
  })

  it('空页原样返回', () => {
    expect(dropBoundaryDuplicates([], boundary, 3)).toHaveLength(0)
  })
})
