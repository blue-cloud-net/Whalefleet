import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { i18n } from '../src/locales'
import AuthorizationSettingsCard from '../src/components/business/AuthorizationSettingsCard.vue'
import {
  getAuthorizationSettings,
  updateAuthorizationSettings
} from '../src/api/authorization-settings'

vi.mock('../src/api/authorization-settings', () => ({
  getAuthorizationSettings: vi.fn(),
  updateAuthorizationSettings: vi.fn()
}))

describe('容器授权策略设置', () => {
  it('两种模式的撤销标题与后果提示互不相同', () => {
    expect(i18n.global.t('containerRbac.revokeStrictTitle')).not.toBe(
      i18n.global.t('containerRbac.revokeInheritedTitle')
    )
    expect(i18n.global.t('containerRbac.revokeStrict')).not.toBe(
      i18n.global.t('containerRbac.revokeInherited')
    )
  })
  beforeEach(() => vi.clearAllMocks())
  it('保存选择的模式并使用真实词条渲染', async () => {
    vi.mocked(getAuthorizationSettings).mockResolvedValue({
      containerPermissionMode: 'requireAssignment'
    })
    vi.mocked(updateAuthorizationSettings).mockResolvedValue({
      containerPermissionMode: 'requireAssignment'
    })
    const wrapper = mount(AuthorizationSettingsCard, { global: { plugins: [i18n, ElementPlus] } })
    await flushPromises()
    expect(wrapper.text()).toContain(i18n.global.t('containerRbac.requireAssignment'))
    await wrapper.find('button').trigger('click')
    await flushPromises()
    expect(updateAuthorizationSettings).toHaveBeenCalledWith({
      containerPermissionMode: 'requireAssignment'
    })
    wrapper.unmount()
  })
  it('无访问权限时不提供策略编辑入口', async () => {
    vi.mocked(getAuthorizationSettings).mockRejectedValue(new Error('forbidden'))
    const wrapper = mount(AuthorizationSettingsCard, { global: { plugins: [i18n, ElementPlus] } })
    await flushPromises()
    expect(wrapper.find('button').exists()).toBe(false)
    wrapper.unmount()
  })
})
