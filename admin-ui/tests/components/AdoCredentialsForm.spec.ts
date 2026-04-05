// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockListAdoOrganizationScopes = vi.fn()
const mockCreateAdoOrganizationScope = vi.fn()
const mockUpdateAdoOrganizationScope = vi.fn()
const mockDeleteAdoOrganizationScope = vi.fn()
const mockPut = vi.fn()
const mockDelete = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ PUT: mockPut, DELETE: mockDelete })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('@/services/adoDiscoveryService', () => ({
  listAdoOrganizationScopes: mockListAdoOrganizationScopes,
  createAdoOrganizationScope: mockCreateAdoOrganizationScope,
  updateAdoOrganizationScope: mockUpdateAdoOrganizationScope,
  deleteAdoOrganizationScope: mockDeleteAdoOrganizationScope,
}))

async function importAdoCredentialsForm() {
  const mod = await import('@/components/AdoCredentialsForm.vue')
  return mod.default
}

describe('AdoCredentialsForm', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockListAdoOrganizationScopes.mockResolvedValue([])
  })

  it('renders tenantId, clientId, and secret inputs', async () => {
    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: false },
    })
    expect(wrapper.find('input[name="tenantId"]').exists()).toBe(true)
    expect(wrapper.find('input[name="clientId"]').exists()).toBe(true)
    const secretInput = wrapper.find('input[name="secret"]')
    expect(secretInput.exists()).toBe(true)
    expect((secretInput.element as HTMLInputElement).type).toBe('password')
  })

  it('secret input is never pre-populated', async () => {
    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: true },
    })
    const secretInput = wrapper.find('input[name="secret"]')
    expect((secretInput.element as HTMLInputElement).value).toBe('')
  })

  it('calls PUT /clients/{clientId}/ado-credentials with form data on save', async () => {
    mockPut.mockResolvedValue({ response: { ok: true } })
    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: false },
    })
    await wrapper.find('input[name="tenantId"]').setValue('tenant-abc')
    await wrapper.find('input[name="clientId"]').setValue('client-abc')
    await wrapper.find('input[name="secret"]').setValue('my-secret')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(mockPut).toHaveBeenCalledWith(
      '/clients/{clientId}/ado-credentials',
      expect.objectContaining({
        params: { path: { clientId: 'client-1' } },
        body: { tenantId: 'tenant-abc', clientId: 'client-abc', secret: 'my-secret' },
      })
    )
  })

  it('calls DELETE on Clear button and emits credentials-cleared', async () => {
    mockDelete.mockResolvedValue({ response: { ok: true } })
    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: true },
    })
    await wrapper.find('button.clear-btn').trigger('click')
    await flushPromises()
    expect(mockDelete).toHaveBeenCalledWith(
      '/clients/{clientId}/ado-credentials',
      expect.objectContaining({ params: { path: { clientId: 'client-1' } } })
    )
    expect(wrapper.emitted('credentials-cleared')).toBeTruthy()
  })

  it('loads and renders configured organization scopes on mount', async () => {
    mockListAdoOrganizationScopes.mockResolvedValue([
      {
        id: 'scope-1',
        clientId: 'client-1',
        organizationUrl: 'https://dev.azure.com/acme',
        displayName: 'Acme Org',
        isEnabled: true,
        verificationStatus: 'verified',
      },
    ])

    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: true },
    })

    await flushPromises()

    expect(mockListAdoOrganizationScopes).toHaveBeenCalledWith('client-1')
    expect(wrapper.text()).toContain('Acme Org')
    expect(wrapper.text()).toContain('https://dev.azure.com/acme')
    expect(wrapper.text()).toContain('Verified')
  })

  it('creates an organization scope from the inline scope form', async () => {
    mockCreateAdoOrganizationScope.mockResolvedValue({
      id: 'scope-1',
      clientId: 'client-1',
      organizationUrl: 'https://dev.azure.com/acme',
      displayName: 'Acme Org',
      isEnabled: true,
      verificationStatus: 'unknown',
    })

    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: true },
    })

    await flushPromises()
    await wrapper.find('input[name="organizationUrl"]').setValue('https://dev.azure.com/acme')
    await wrapper.find('input[name="organizationDisplayName"]').setValue('Acme Org')
    await wrapper.find('button.save-scope-btn').trigger('click')
    await flushPromises()

    expect(mockCreateAdoOrganizationScope).toHaveBeenCalledWith('client-1', {
      organizationUrl: 'https://dev.azure.com/acme',
      displayName: 'Acme Org',
    })
    expect(wrapper.text()).toContain('Acme Org')
  })

  it('toggles a scope enabled state through patch update', async () => {
    mockListAdoOrganizationScopes.mockResolvedValue([
      {
        id: 'scope-1',
        clientId: 'client-1',
        organizationUrl: 'https://dev.azure.com/acme',
        displayName: 'Acme Org',
        isEnabled: true,
        verificationStatus: 'verified',
      },
    ])
    mockUpdateAdoOrganizationScope.mockResolvedValue({
      id: 'scope-1',
      clientId: 'client-1',
      organizationUrl: 'https://dev.azure.com/acme',
      displayName: 'Acme Org',
      isEnabled: false,
      verificationStatus: 'verified',
    })

    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: true },
    })

    await flushPromises()
    await wrapper.find('button.toggle-scope-btn').trigger('click')
    await flushPromises()

    expect(mockUpdateAdoOrganizationScope).toHaveBeenCalledWith('client-1', 'scope-1', {
      organizationUrl: 'https://dev.azure.com/acme',
      displayName: 'Acme Org',
      isEnabled: false,
    })
    expect(wrapper.text()).toContain('Disabled')
  })

  it('deletes a configured organization scope', async () => {
    mockListAdoOrganizationScopes.mockResolvedValue([
      {
        id: 'scope-1',
        clientId: 'client-1',
        organizationUrl: 'https://dev.azure.com/acme',
        displayName: 'Acme Org',
        isEnabled: true,
        verificationStatus: 'verified',
      },
    ])
    mockDeleteAdoOrganizationScope.mockResolvedValue(undefined)

    const AdoCredentialsForm = await importAdoCredentialsForm()
    const wrapper = mount(AdoCredentialsForm, {
      props: { clientId: 'client-1', hasCredentials: true },
    })

    await flushPromises()
    await wrapper.find('button.delete-scope-btn').trigger('click')
    await flushPromises()

    expect(mockDeleteAdoOrganizationScope).toHaveBeenCalledWith('client-1', 'scope-1')
    expect(wrapper.findAll('.scope-item')).toHaveLength(0)
  })
})
