import { describe, expect, it } from 'vitest'
import { groupDescription, groupLabel, isBuiltInGroupName } from '../src/utils/group-display'

/**
 * 分组展示逻辑的单元测试。
 *
 * 关键约定：**名字恰好等于内置标识时取 i18n 文案，其余原样显示**。
 * 这条例外规则是有意的——分组名是管理员可改的数据，数据不该被程序硬翻；
 * 但三个内置分组在界面上有固定含义，中文界面下显示「生产环境」比 `production` 更好懂。
 */
const t = (key: string): string =>
  (
    {
      'host.group.production': '生产环境',
      'host.group.staging': '测试环境',
      'host.group.local': '本机',
      'host.group.ungrouped': '未分组',
      'host.group.productionDesc': '线上生产负载'
    } as Record<string, string>
  )[key] ?? key

describe('内置分组识别', () => {
  it('三个内置标识被识别', () => {
    expect(isBuiltInGroupName('production')).toBe(true)
    expect(isBuiltInGroupName('staging')).toBe(true)
    expect(isBuiltInGroupName('local')).toBe(true)
  })

  it('自定义名字不算内置（大小写敏感：它就是数据）', () => {
    expect(isBuiltInGroupName('edge')).toBe(false)
    expect(isBuiltInGroupName('Production')).toBe(false)
  })
})

describe('分组展示名', () => {
  it('内置分组取 i18n 文案', () => {
    expect(groupLabel({ name: 'production' }, t)).toBe('生产环境')
  })

  it('自定义分组原样显示管理员输入的名字', () => {
    // 用户数据不翻译：把「预发布」硬翻成 "Pre-release" 只会更难懂
    expect(groupLabel({ name: '预发布' }, t)).toBe('预发布')
  })

  it('未分组用约定的 i18n key', () => {
    expect(groupLabel(null, t)).toBe('未分组')
  })
})

describe('分组描述', () => {
  it('后端有描述时以它为准（管理员写的说明最准确）', () => {
    expect(groupDescription({ name: 'production', description: '核心交易集群' }, t)).toBe(
      '核心交易集群'
    )
  })

  it('内置分组无描述时回退到内置文案', () => {
    expect(groupDescription({ name: 'production', description: null }, t)).toBe('线上生产负载')
  })

  it('自定义分组无描述时不硬塞文案', () => {
    // 给空描述的分组补一句模板话只会让界面更像模板、不像数据
    expect(groupDescription({ name: '预发布', description: null }, t)).toBeUndefined()
  })

  it('未分组没有描述', () => {
    expect(groupDescription(null, t)).toBeUndefined()
  })
})
