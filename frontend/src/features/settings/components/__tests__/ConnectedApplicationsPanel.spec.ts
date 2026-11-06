import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ConnectedApplicationsPanel from '../ConnectedApplicationsPanel.vue'

const { list, revoke } = vi.hoisted(() => ({ list: vi.fn(), revoke: vi.fn() }))
vi.mock('@/services/extensionAuthorizationService', () => ({
  listExtensionGrants: list,
  revokeExtensionGrant: revoke,
}))
const grant = { id: 'grant-a', application: 'ProPR Lens', grantedAt: '2026-11-05T00:00:00Z' }
beforeEach(() => { list.mockReset(); revoke.mockReset() })
describe('connected applications', () => {
  it('does not claim an empty grant list while loading or after an initial failure', async () => {
    list.mockRejectedValue(new Error('Applications could not be loaded.'))
    const wrapper = mount(ConnectedApplicationsPanel)
    expect(wrapper.text()).not.toContain('No applications have access.')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('could not be loaded')
    expect(wrapper.text()).not.toContain('No applications have access.')
  })
  it('allows retrying a failed initial list and only then shows the empty state', async () => {
    list.mockRejectedValueOnce(new Error('Unavailable')).mockResolvedValueOnce([])
    const wrapper = mount(ConnectedApplicationsPanel)
    await flushPromises()
    await wrapper.get('[data-testid="retry-applications"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('No applications have access.')
  })
  it('removes a successfully revoked application even if refreshing the list fails', async () => {
    list.mockResolvedValueOnce([grant]).mockRejectedValueOnce(new Error('Refresh unavailable'))
    revoke.mockResolvedValue(undefined)
    const wrapper = mount(ConnectedApplicationsPanel)
    await flushPromises()
    await wrapper.get('li button').trigger('click')
    await flushPromises()
    expect(revoke).toHaveBeenCalledWith('grant-a')
    expect(wrapper.find('li').exists()).toBe(false)
    expect(wrapper.get('[role="alert"]').text()).toContain('Refresh unavailable')
    expect(wrapper.text()).not.toContain('No applications have access.')
  })
  it('retains an application when revocation itself fails', async () => {
    list.mockResolvedValue([grant]); revoke.mockRejectedValue(new Error('Revocation unavailable'))
    const wrapper = mount(ConnectedApplicationsPanel)
    await flushPromises()
    await wrapper.get('li button').trigger('click')
    await flushPromises()
    expect(wrapper.get('li').text()).toContain('ProPR Lens')
    expect(list).toHaveBeenCalledTimes(1)
  })
})
