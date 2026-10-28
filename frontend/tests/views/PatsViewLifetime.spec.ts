// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import PatsView from '@/features/pats/views/PatsView.vue'

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({ getAccessToken: () => 'test-access-token' }),
}))

afterEach(() => vi.unstubAllGlobals())

describe('Personal access token lifetime', () => {
  it('shows the configured lifetime before creation and the applied expiry with the new token', async () => {
    const expiresAt = '2027-01-25T12:00:00Z'
    vi.stubGlobal('fetch', vi.fn(async (input: string, options?: RequestInit) => new Response(
      JSON.stringify(input.endsWith('/policy')
        ? { maximumLifetimeDays: 90 }
        : options?.method === 'POST'
          ? { token: 'test-personal-token', expiresAt }
          : []),
      { status: 200, headers: { 'Content-Type': 'application/json' } },
    )))
    const wrapper = mount(PatsView)
    await flushPromises()

    expect(wrapper.text()).toContain('Tokens expire after at most 90 days.')
    await wrapper.get('#pat-label').setValue('Browser extension')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('.token-reveal code').text()).toBe('test-personal-token')
    expect(wrapper.get('[data-testid="generated-token-expiry"]').text()).toContain(new Date(expiresAt).toLocaleString())
    wrapper.unmount()
  })

  it('shows and revokes existing tokens when the policy request fails', async () => {
    const fetchMock = vi.fn(async (input: string, options?: RequestInit) => {
      if (input.endsWith('/policy')) return new Response(null, { status: 503 })
      if (options?.method === 'DELETE') return new Response(null, { status: 204 })
      return new Response(JSON.stringify([
        { id: 'existing', label: 'CI token', createdAt: '2026-01-01T00:00:00Z', expiresAt: null, lastUsedAt: null, isRevoked: false },
      ]), { status: 200, headers: { 'Content-Type': 'application/json' } })
    })
    vi.stubGlobal('fetch', fetchMock)
    const wrapper = mount(PatsView)
    await flushPromises()

    expect(wrapper.text()).toContain('CI token')
    expect(wrapper.text()).toContain('Could not load the token lifetime policy.')
    await wrapper.get('table button').trigger('click')
    await flushPromises()

    expect(fetchMock).toHaveBeenCalledWith(expect.stringMatching(/\/users\/me\/pats\/existing$/), expect.objectContaining({ method: 'DELETE' }))
    expect(wrapper.text()).not.toContain('CI token')
    wrapper.unmount()
  })
})
