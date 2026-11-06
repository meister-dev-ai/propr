import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import ExtensionConsentView from '../views/ExtensionConsentView.vue'

const { query, getConsent } = vi.hoisted(() => ({ query: {} as Record<string, string>, getConsent: vi.fn() }))
vi.mock('vue-router', () => ({ useRoute: () => ({ query }) }))
vi.mock('@/composables/useSession', () => ({ useSession: () => ({ username: 'alice' }) }))
vi.mock('@/services/extensionAuthorizationService', () => ({ getExtensionConsent: getConsent, decideExtensionConsent: vi.fn() }))
beforeEach(() => { vi.restoreAllMocks(); getConsent.mockReset(); for (const key of Object.keys(query)) delete query[key] })
it('shows actionable storage recovery without requesting consent or exposing browser errors', async () => {
  query.error = 'continuation_unavailable'
  vi.spyOn(sessionStorage, 'removeItem').mockImplementation(() => { throw new Error('private browser failure') })
  const wrapper = mount(ExtensionConsentView)
  await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toContain('session storage')
  expect(wrapper.text()).toContain('extension settings and select Sign in again')
  expect(wrapper.text()).not.toContain('private browser failure')
  expect(getConsent).not.toHaveBeenCalled()
})
it('explains how to recover from an unavailable or consumed consent request', async () => {
  query.request = 'protected-request'
  getConsent.mockRejectedValue(new Error('The sign-in request is unavailable.'))
  const wrapper = mount(ExtensionConsentView)
  await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toContain('unavailable')
  expect(wrapper.text()).toContain('extension settings and select Sign in again')
})
