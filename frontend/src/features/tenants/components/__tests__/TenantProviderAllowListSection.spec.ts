import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import TenantProviderAllowListSection from '../TenantProviderAllowListSection.vue'
import type { TenantDto } from '@/services/tenantAdminService'

const getTenant = vi.fn()
const updateTenant = vi.fn()

vi.mock('@/services/tenantAdminService', () => ({
  getTenant: (...a: unknown[]) => getTenant(...a),
  updateTenant: (...a: unknown[]) => updateTenant(...a),
}))

const tenant = (allowed?: string[], hosts?: string[]): TenantDto =>
  ({
    id: 't1',
    slug: 'acme',
    displayName: 'Acme',
    isActive: true,
    localLoginEnabled: true,
    isEditable: true,
    createdAt: '2026-07-01T00:00:00Z',
    updatedAt: '2026-07-01T00:00:00Z',
    allowedAiProviderKinds: allowed,
    allowedAiEndpointHosts: hosts,
  }) as TenantDto

const section = () => mount(TenantProviderAllowListSection, { props: { tenantId: 't1' } })

describe('TenantProviderAllowListSection', () => {
  beforeEach(() => {
    getTenant.mockReset()
    updateTenant.mockReset()
    getTenant.mockResolvedValue(tenant([]))
    updateTenant.mockImplementation(
      (_id: string, body: { allowedAiProviderKinds?: string[]; allowedAiEndpointHosts?: string[] }) =>
        Promise.resolve(tenant(body.allowedAiProviderKinds, body.allowedAiEndpointHosts)),
    )
  })

  // The reading of "nothing selected" is the whole risk in this screen, so the copy has to say which one it is.
  it('states that selecting nothing permits every provider', async () => {
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-provider-policy-summary"]').text()).toContain('No restriction')
  })

  it('pre-selects the policy the tenant already has', async () => {
    getTenant.mockResolvedValue(tenant(['azureOpenAi']))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get<HTMLInputElement>('[data-testid="tenant-provider-azureOpenAi"]').element.checked).toBe(true)
    expect(wrapper.get<HTMLInputElement>('[data-testid="tenant-provider-openAi"]').element.checked).toBe(false)
    expect(wrapper.get('[data-testid="tenant-provider-policy-summary"]').text()).toContain('only use the selected')
  })

  it('saves the selected families', async () => {
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-provider-liteLlm"]').trigger('change')
    await wrapper.get('[data-testid="tenant-provider-policy-save"]').trigger('click')
    await flushPromises()

    expect(updateTenant).toHaveBeenCalledWith('t1', {
      allowedAiProviderKinds: ['liteLlm'],
      allowedAiEndpointHosts: [],
    })
    expect(wrapper.get('[data-testid="tenant-provider-policy-saved"]').text()).toBe('Provider policy saved.')
  })

  // Clearing every box is how a restriction is lifted, so it has to be sent rather than read as "no change".
  it('sends an empty list when the last family is cleared', async () => {
    getTenant.mockResolvedValue(tenant(['liteLlm']))
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-provider-liteLlm"]').trigger('change')
    await wrapper.get('[data-testid="tenant-provider-policy-save"]').trigger('click')
    await flushPromises()

    expect(updateTenant).toHaveBeenCalledWith('t1', { allowedAiProviderKinds: [], allowedAiEndpointHosts: [] })
    expect(wrapper.get('[data-testid="tenant-provider-policy-saved"]').text()).toContain('Every provider and destination is permitted')
  })

  it('surfaces the server reason when saving fails', async () => {
    updateTenant.mockRejectedValue(new Error('Tenant policy is locked.'))
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-provider-policy-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-provider-policy-error"]').text()).toBe('Tenant policy is locked.')
  })

  // Where the traffic goes is the half a provider family cannot answer, so the host list is its own control and
  // saves alongside the families rather than as a separate action.
  it('saves the permitted endpoint hosts, one per line', async () => {
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-endpoint-hosts"]').setValue('api.openai.com\n  .openai.azure.com  \n\nopencode.ai')
    await wrapper.get('[data-testid="tenant-provider-policy-save"]').trigger('click')
    await flushPromises()

    expect(updateTenant).toHaveBeenCalledWith('t1', {
      allowedAiProviderKinds: [],
      allowedAiEndpointHosts: ['api.openai.com', '.openai.azure.com', 'opencode.ai'],
    })
  })

  it('states plainly when no destination is restricted', async () => {
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-endpoint-policy-summary"]').text()).toContain('any host')
  })

  it('pre-fills the hosts the tenant already permits', async () => {
    getTenant.mockResolvedValue(tenant([], ['opencode.ai']))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get<HTMLTextAreaElement>('[data-testid="tenant-endpoint-hosts"]').element.value).toBe('opencode.ai')
    expect(wrapper.get('[data-testid="tenant-endpoint-policy-summary"]').text()).toContain('opencode.ai')
  })
})
