// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import TenantProviderAllowListSection from '../TenantProviderAllowListSection.vue'
import type { TenantDto } from '@/services/tenantAdminService'

const getTenant = vi.fn()
const updateTenant = vi.fn()
const listTenantPermittedProviders = vi.fn()

vi.mock('@/services/tenantAdminService', () => ({
  getTenant: (...a: unknown[]) => getTenant(...a),
  updateTenant: (...a: unknown[]) => updateTenant(...a),
}))

vi.mock('@/services/aiConnectionsService', () => ({
  listTenantPermittedProviders: (...a: unknown[]) => listTenantPermittedProviders(...a),
}))

// The families the installation loaded, as the server describes them. The section holds no list of its own, so
// this is where the boxes come from.
const describedFamilies = (kinds: Array<[string, string]>) => ({
  isRestricted: false,
  providers: kinds.map(([providerKind, label]) => ({
    providerKind,
    label,
    isPermitted: true,
    protocolModes: [{ value: 'Auto', label: 'Auto' }],
    authModes: [{ value: `${providerKind}:ApiKey`, label: 'API Key' }],
    credentialFields: {},
    declaredFields: [],
  })),
})

const tenant = (allowed?: string[], hosts?: string[], unresolved?: string[]): TenantDto =>
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
    unresolvedAiProviderKinds: unresolved,
  }) as TenantDto

const section = () => mount(TenantProviderAllowListSection, { props: { tenantId: 't1' } })

describe('TenantProviderAllowListSection', () => {
  beforeEach(() => {
    getTenant.mockReset()
    updateTenant.mockReset()
    listTenantPermittedProviders.mockReset()
    getTenant.mockResolvedValue(tenant([]))
    listTenantPermittedProviders.mockResolvedValue(
      describedFamilies([
        ['azureOpenAi', 'Azure OpenAI / AI Foundry'],
        ['liteLlm', 'LiteLLM'],
      ]),
    )
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
    expect(wrapper.get<HTMLInputElement>('[data-testid="tenant-provider-liteLlm"]').element.checked).toBe(false)
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

  // An entry this build has no provider for permits no family, so a policy holding one and nothing ticked
  // refuses every provider. Reading the ticked boxes alone reported the opposite of what the server enforces.
  it('reports a policy of unresolved entries alone as permitting no provider family', async () => {
    getTenant.mockResolvedValue(tenant([], [], ['Acme.Llm']))
    const wrapper = section()
    await flushPromises()

    const summary = wrapper.get('[data-testid="tenant-provider-policy-summary"]').text()
    expect(summary).not.toContain('No restriction')
    expect(summary).toContain('No provider family is permitted')
    expect(wrapper.get('[data-testid="tenant-provider-policy-unresolved"]').text()).toContain('Acme.Llm')
  })

  it('does not report everything as permitted after saving a policy of unresolved entries alone', async () => {
    getTenant.mockResolvedValue(tenant([], [], ['Acme.Llm']))
    updateTenant.mockResolvedValue(tenant([], [], ['Acme.Llm']))
    const wrapper = section()
    await flushPromises()

    await wrapper.get('[data-testid="tenant-provider-policy-save"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-provider-policy-saved"]').text()).toBe('Provider policy saved.')
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

  // The load clears the previous policy before it asks, so a failed read leaves empty boxes, and an empty list
  // is how a restriction is lifted. One click on Save would remove the tenant's whole policy.
  it('shows no form and offers no save when the policy in force could not be read', async () => {
    getTenant.mockRejectedValue(new Error('The tenant could not be read.'))
    const wrapper = section()
    await flushPromises()

    expect(wrapper.get('[data-testid="tenant-provider-policy-error"]').text()).toContain('could not be read')
    expect(wrapper.find('[data-testid="tenant-provider-policy-save"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-endpoint-hosts"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-provider-policy-retry"]').exists()).toBe(true)
  })

  it('shows the form again once a retry succeeds', async () => {
    getTenant.mockRejectedValueOnce(new Error('The tenant could not be read.'))
    const wrapper = section()
    await flushPromises()

    getTenant.mockResolvedValue(tenant([], ['opencode.ai']))
    await wrapper.get('[data-testid="tenant-provider-policy-retry"]').trigger('click')
    await flushPromises()

    expect(wrapper.get<HTMLTextAreaElement>('[data-testid="tenant-endpoint-hosts"]').element.value).toBe('opencode.ai')
    expect(wrapper.find('[data-testid="tenant-provider-policy-save"]').exists()).toBe(true)
  })
})
