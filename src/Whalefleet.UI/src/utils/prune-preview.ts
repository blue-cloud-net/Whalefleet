import type { DiskUsageBucket, DiskUsageItem } from '@/types/api'

/**
 * 清理预览的候选清单（0.7.0）。
 *
 * 纯函数、返回结构化数据而非文案——可以脱离组件单测，
 * 也避免同一套判定在镜像页与卷页各写一遍后逐渐漂移。
 */

/** 一条清理候选。 */
export interface PruneCandidate {
  /** 条目标识（镜像 ID / 卷名） */
  id: string
  /** 可读名称（镜像的第一个标签）；悬空镜像没有标签时为 `null` */
  name: string | null
  /** 占用字节数；守护进程未给出时为 `null` */
  size: number | null
}

/** 清理预览的结果。 */
export interface PrunePreview {
  /** 候选清单：按守护进程口径「未被引用」的条目 */
  candidates: PruneCandidate[]
  /** 用量未知、**未计入**候选的条目数 */
  unknownCount: number
  /** 桶级「可回收」字节数；守护进程未给出时为 `null` */
  reclaimable: number | null
  /** 桶级条目总数；守护进程未给出时为 `null` */
  totalCount: number | null
}

/** 预览选项。 */
export interface PrunePreviewOptions {
  /**
   * 是否只算悬空镜像（只对镜像有意义）。
   *
   * 守护进程的口径是「只清悬空镜像」= **一个标签都不剩**的那些。
   * df 的镜像条目里 `name` 就是第一个标签，没有标签时为 `null`——
   * 因此「`name` 为空」正好等价于「悬空」。
   */
  danglingOnly?: boolean
}

/**
 * 从磁盘用量算出清理的候选清单。
 *
 * @param bucket `GET /hosts/{hostId}/system/df` 里对应类别的用量桶
 * @param options 预览选项
 * @returns 预览结果
 *
 * ## 为什么「用量未知」要单独计数而不是当成可清理
 * `inUse` 为 `null` 表示守护进程没给出这个信息——**不是**「没人在用」。
 * 把它算进候选等于凭空承诺「这些会被删」，而守护进程未必会删；
 * 把它算作「保留」又会让清单偏小、用户以为漏了。
 * 正确做法是**不进候选，但要让人知道有这些条目存在**。
 */
export function buildPrunePreview(
  bucket: DiskUsageBucket | null,
  options: PrunePreviewOptions = {}
): PrunePreview {
  if (bucket === null) {
    return { candidates: [], unknownCount: 0, reclaimable: null, totalCount: null }
  }

  const candidates: PruneCandidate[] = []
  let unknownCount = 0

  for (const item of bucket.items) {
    if (item.inUse === null) {
      unknownCount += 1
      continue
    }

    if (item.inUse) {
      continue
    }

    if (options.danglingOnly && item.name !== null) {
      continue
    }

    candidates.push({ id: item.id, name: item.name, size: item.size })
  }

  return {
    candidates,
    unknownCount,
    reclaimable: bucket.reclaimable,
    totalCount: bucket.totalCount
  }
}

/**
 * 候选条目的展示名。
 *
 * @param candidate 候选条目
 * @returns 名称；没有可读名称时退回标识
 *
 * 悬空镜像没有标签，只能显示镜像 ID——那是一串 `sha256:…`，
 * 但它是**唯一**能让人认出「删的是哪一个」的东西。
 */
export function pruneCandidateLabel(candidate: PruneCandidate): string {
  return candidate.name ?? candidate.id
}

/**
 * 判断某个 df 条目是否会被「只清悬空」排除。
 *
 * @param item df 条目
 * @returns 带标签（即非悬空）时为 `true`
 *
 * 供「高级」提示使用：填了标签过滤后，界面要说明预览是全量候选。
 */
export function isTaggedImage(item: DiskUsageItem): boolean {
  return item.name !== null
}
