import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { i18n } from '../src/locales'
import PruneDialog from '../src/components/business/PruneDialog.vue'

/**
 * 清理对话框的「三种范围必须长得不一样」（0.7.0 起）。
 *
 * ## 为什么守这个
 * 「清理悬空镜像」与「清理所有未使用镜像」删掉的东西**完全不同**：
 * 前者只删**没有标签**的镜像，后者（等价 `docker image prune -a`）
 * 连**有标签但没被容器用上**的镜像一起删。
 *
 * 早先两种范围共用同一个标题与同一句副标题，于是：
 * - 两个入口点开的是**同一扇窗**，用户分不清自己在哪个范围；
 * - 副标题只说了「没有被任何容器使用」——那是 `all` 的口径，
 *   套到 `dangling` 上就变成了**谎报**：让人以为带标签的镜像也会被删。
 *
 * 后者尤其危险：它让「只清悬空」看起来比实际更激进，用户可能因此
 * 放弃手动挑选的余地。
 *
 * ## 为什么用真实 i18n
 * 桩掉 `t()` 就查不出「副标题写的是哪一档」——而这正是本文件要守的东西。
 */

// 预览要拉 `system/df`；这里桩掉，避免测试真的发网络请求。
vi.mock('../src/api/host', () => ({
  getHostDiskUsage: vi.fn(async () => ({
    images: {
      totalCount: 0,
      activeCount: 0,
      totalSize: 0,
      reclaimable: 0,
      items: []
    },
    containers: null,
    volumes: null,
    buildCache: null
  }))
}))

describe('清理对话框的标题与副标题按范围区分', () => {
  /** 挂载一个指定范围的对话框。 */
  /**
   * 取出对话框的标题与副标题文本。
   *
   * ⚠️ **要等两次 `nextTick`**：`el-dialog` 先渲染遮罩与骨架，
   * 下一轮才把内容挂上去。只等一次的话 `.el-dialog__title` 还不存在。
   */
  async function readHeader(
    scope: 'dangling' | 'all' | 'volumes'
  ): Promise<{ title: string; subtitle: string }> {
    const wrapper = mount(PruneDialog, {
      global: { plugins: [i18n, ElementPlus] },
      props: { hostId: 'local', scope, modelValue: true }
    })

    await wrapper.vm.$nextTick()
    await wrapper.vm.$nextTick()

    const title = wrapper.find('.el-dialog__title').text()
    // 副标题是预览区里第一个段落
    const subtitle = wrapper.find('.el-dialog__body p').text()

    wrapper.unmount()

    return { title, subtitle }
  }

  it('三个范围的标题互不相同', async () => {
    const titles = await Promise.all(
      (['dangling', 'all', 'volumes'] as const).map(async (scope) => (await readHeader(scope)).title)
    )

    expect(new Set(titles).size).toBe(3)
  })

  it('三个范围的副标题互不相同', async () => {
    const subtitles = await Promise.all(
      (['dangling', 'all', 'volumes'] as const).map(
        async (scope) => (await readHeader(scope)).subtitle
      )
    )

    expect(new Set(subtitles).size).toBe(3)
  })

  it('「清理悬空镜像」的副标题说的是「没有标签」，而不是「没被容器使用」', async () => {
    const { title, subtitle } = await readHeader('dangling')

    expect(title).toContain('悬空镜像')
    // ⚠️ 关键断言：悬空的口径是「没有标签」。
    // 若这里退化成「没有被任何容器使用」，就与 `all` 档无法区分，
    // 而实际删除范围也变窄了——文案与行为同时对不上。
    expect(subtitle).toContain('没有标签')
  })

  it('「清理所有未使用镜像」的副标题说的是「没有被任何容器使用」', async () => {
    const { title, subtitle } = await readHeader('all')

    expect(title).toContain('未使用镜像')
    expect(subtitle).toContain('没有被任何容器使用')
  })

  it('数据卷档说的是「没有被任何容器引用」', async () => {
    const { title, subtitle } = await readHeader('volumes')

    expect(title).toContain('数据卷')
    expect(subtitle).toContain('没有被任何容器引用')
  })
})
