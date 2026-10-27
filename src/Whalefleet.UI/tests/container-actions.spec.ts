import { describe, expect, it } from 'vitest'
import {
  actionDisabledReason,
  batchDisabledReason,
  canPerform,
  eligibleCount,
  isActive,
  type ContainerAction
} from '../src/utils/container-actions'

/**
 * 容器生命周期动作的可用性矩阵（0.7.0）。
 *
 * ## 为什么这值得单测
 * 这张矩阵同时被三个界面消费（列表行内按钮、列表批量按钮、详情页操作区）。
 * 判错的后果不是「少一个按钮」，而是**给出了一个点了会报错的按钮**：
 * 守护进程对非法操作回 409，界面表现为「操作失败」——
 * 而用户完全看不出是自己点错了状态。
 */
describe('容器动作的可用性矩阵', () => {
  it('启动只对没在跑的容器有意义', () => {
    expect(canPerform('start', 'exited')).toBe(true)
    expect(canPerform('start', 'created')).toBe(true)
    expect(canPerform('start', 'dead')).toBe(true)

    expect(canPerform('start', 'running')).toBe(false)
    expect(canPerform('start', 'paused')).toBe(false)
  })

  it('停止 / 重启 / 终止对运行类容器有意义（暂停中也算「还活着」）', () => {
    for (const action of ['stop', 'restart', 'kill'] as ContainerAction[]) {
      expect(canPerform(action, 'running'), `${action} on running`).toBe(true)
      expect(canPerform(action, 'paused'), `${action} on paused`).toBe(true)
      expect(canPerform(action, 'restarting'), `${action} on restarting`).toBe(true)

      expect(canPerform(action, 'exited'), `${action} on exited`).toBe(false)
      expect(canPerform(action, 'created'), `${action} on created`).toBe(false)
    }
  })

  it('暂停只对运行中的容器有意义，继续只对已暂停的有意义', () => {
    expect(canPerform('pause', 'running')).toBe(true)
    expect(canPerform('pause', 'paused')).toBe(false)
    expect(canPerform('pause', 'exited')).toBe(false)

    expect(canPerform('resume', 'paused')).toBe(true)
    expect(canPerform('resume', 'running')).toBe(false)
    expect(canPerform('resume', 'exited')).toBe(false)
  })

  it('删除对任何状态都成立', () => {
    for (const state of ['running', 'paused', 'exited', 'created', 'dead', 'restarting']) {
      expect(canPerform('remove', state), `remove on ${state}`).toBe(true)
    }
  })
})

describe('isActive（是否需要 force 才能删）', () => {
  it('暂停中的容器算「活动」——它在 Docker 语义里仍在运行', () => {
    expect(isActive('running')).toBe(true)
    expect(isActive('paused')).toBe(true)
    expect(isActive('restarting')).toBe(true)
    expect(isActive('removing')).toBe(true)

    expect(isActive('exited')).toBe(false)
    expect(isActive('created')).toBe(false)
    expect(isActive('dead')).toBe(false)
  })
})

describe('批量动作的启用规则', () => {
  it('一个都不满足才禁用', () => {
    // 选中的全是已停止的容器 → 「启动」可用
    expect(eligibleCount('start', ['exited', 'exited'])).toBe(2)
    expect(batchDisabledReason('start', ['exited', 'exited'])).toBeNull()

    // 选中的全是运行中的容器 → 「暂停」可用
    expect(batchDisabledReason('pause', ['running', 'running'])).toBeNull()
  })

  it('部分满足时按钮仍可用（对不满足的那些是空操作，后端按幂等处理）', () => {
    // 3 个在跑 + 7 个已停 → 「启动」对那 7 个有效
    expect(eligibleCount('start', ['running', 'running', 'running', ...Array(7).fill('exited')])).toBe(7)
    expect(batchDisabledReason('start', ['running', 'exited'])).toBeNull()
  })

  it('全部不满足时禁用，并给出「没有可操作的容器」的原因', () => {
    const reason = batchDisabledReason('pause', ['exited', 'exited'])

    expect(reason).not.toBeNull()
    expect(reason).toBe('container.batch.noEligible.pause')
  })

  it('没选任何容器时禁用，原因是「先选几个」', () => {
    expect(batchDisabledReason('start', [])).toBe('container.batch.selectFirst')
  })
})

describe('单个动作的禁用原因', () => {
  it('可用时没有原因', () => {
    expect(actionDisabledReason('start', 'exited')).toBeNull()
    expect(actionDisabledReason('resume', 'paused')).toBeNull()
  })

  it('不可用时给出「为什么现在不能做」', () => {
    expect(actionDisabledReason('resume', 'running')).toBe('container.action.disabled.resume')
    expect(actionDisabledReason('pause', 'exited')).toBe('container.action.disabled.pause')
  })
})
