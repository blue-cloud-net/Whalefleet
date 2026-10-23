import { describe, expect, it } from 'vitest'
import { buildPrunePreview, pruneCandidateLabel } from '@/utils/prune-preview'
import type { DiskUsageBucket } from '@/types/api'

/**
 * 清理预览的候选清单（0.7.0）。
 *
 * ## 为什么这层值得单测
 * 预览是清理**唯一的安全阀**：端点的请求里没有「要删哪些」，
 * 选哪些由守护进程决定。因此「哪些条目会出现在清单里」的判定一旦偏了，
 * 用户看到的就与实际发生的不一致——而清理不可逆。
 *
 * 三条判定各自有坑：
 * 1. `inUse === null` 是「**不知道**」，不是「没人在用」——不能进候选；
 * 2. `inUse === false` 才是候选；
 * 3. 「只清悬空」= **一个标签都不剩**，而 df 的镜像条目里 `name` 就是第一个标签。
 */
describe('buildPrunePreview', () => {
  function bucket(items: DiskUsageBucket['items']): DiskUsageBucket {
    return {
      totalCount: items.length,
      activeCount: items.filter((item) => item.inUse === true).length,
      totalSize: null,
      reclaimable: null,
      items
    }
  }

  it('未被引用的条目进候选', () => {
    const preview = buildPrunePreview(
      bucket([
        { id: 'a', name: 'nginx-html', size: 100, inUse: false },
        { id: 'b', name: 'db-data', size: 200, inUse: true }
      ])
    )

    expect(preview.candidates.map((item) => item.id)).toEqual(['a'])
    expect(preview.unknownCount).toBe(0)
  })

  it('「用量未知」既不进候选也不计入未知数以外的地方', () => {
    const preview = buildPrunePreview(
      bucket([
        { id: 'a', name: 'nfs-shared', size: null, inUse: null },
        { id: 'b', name: 'nginx-html', size: 100, inUse: false }
      ])
    )

    // 不知道 = 不能承诺它会被删 → 不进候选
    expect(preview.candidates.map((item) => item.id)).toEqual(['b'])
    // 但必须让人知道有这一条
    expect(preview.unknownCount).toBe(1)
  })

  it('「只清悬空」排除带标签的镜像', () => {
    const items: DiskUsageBucket['items'] = [
      { id: 'dangling', name: null, size: 10, inUse: false },
      { id: 'tagged', name: 'alpine:3.20', size: 20, inUse: false }
    ]

    const danglingOnly = buildPrunePreview(bucket(items), { danglingOnly: true })
    expect(danglingOnly.candidates.map((item) => item.id)).toEqual(['dangling'])

    const all = buildPrunePreview(bucket(items), { danglingOnly: false })
    expect(all.candidates.map((item) => item.id)).toEqual(['dangling', 'tagged'])
  })

  it('桶为 null 时给空清单而不是抛错', () => {
    const preview = buildPrunePreview(null)

    expect(preview.candidates).toEqual([])
    expect(preview.unknownCount).toBe(0)
    expect(preview.reclaimable).toBeNull()
    expect(preview.totalCount).toBeNull()
  })

  it('候选展示名优先用可读名称，没有则退回标识', () => {
    expect(pruneCandidateLabel({ id: 'sha256:abc', name: 'nginx:latest', size: 1 })).toBe(
      'nginx:latest'
    )
    // 悬空镜像没有标签 → 只能显示 ID
    expect(pruneCandidateLabel({ id: 'sha256:abc', name: null, size: 1 })).toBe('sha256:abc')
  })
})
