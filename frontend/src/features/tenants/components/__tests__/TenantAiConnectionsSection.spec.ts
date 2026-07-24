// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import TenantAiConnectionsSection from '../TenantAiConnectionsSection.vue'

const mocks = vi.hoisted(() => ({
  listTenantConnections: vi.fn(),
  createTenantConnection: vi.fn(),
  deleteTenantConnection: vi.fn(),
  verifyTenantConnection: vi.fn(),
}))

vi.mock('@/services/logicalModelsService', () => mocks)

function mountSection() {
  return mount(TenantAiConnectionsSection, { props: { tenantId: 'tenant-1' } })
}

describe('TenantAiConnectionsSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listTenantConnections.mockResolvedValue([
      {
        id: 'tc-1',
        displayName: 'Tenant Azure',
        providerKind: 'openAi',
        baseUrl: 'https://api.openai.com/v1',
        verification: { status: 'verified' },
        configuredModels: [],
      },
    ])
  })

  it('renders a row per tenant connection', async () => {
    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="tenant-conn-row"]')).toHaveLength(1)
    expect(wrapper.text()).toContain('Tenant Azure')
    expect(wrapper.text()).toContain('OpenAI')
  })

  it('creates a tenant connection with its models', async () => {
    mocks.createTenantConnection.mockResolvedValue({})
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="tenant-conn-add"]').trigger('click')
    await wrapper.find('[data-testid="tenant-conn-display-name"]').setValue('Shared Azure')
    await wrapper.find('[data-testid="tenant-conn-base-url"]').setValue('https://shared.openai.azure.com/')
    await wrapper.find('[data-testid="tenant-conn-api-key"]').setValue('secret-key')
    // Opening the form seeds one empty model row; fill its remote id.
    await wrapper.find('[data-testid="tenant-conn-model-id-0"]').setValue('gpt-4o')
    await wrapper.find('[data-testid="tenant-conn-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createTenantConnection).toHaveBeenCalledWith(
      'tenant-1',
      expect.objectContaining({
        displayName: 'Shared Azure',
        baseUrl: 'https://shared.openai.azure.com/',
        auth: { mode: 'apiKey', apiKey: 'secret-key' },
        configuredModels: [expect.objectContaining({ remoteModelId: 'gpt-4o', operationKinds: ['chat'] })],
      }),
    )
    expect(mocks.listTenantConnections).toHaveBeenCalledTimes(2)
  })

  it('verifies and deletes a connection', async () => {
    mocks.verifyTenantConnection.mockResolvedValue({ status: 'verified' })
    mocks.deleteTenantConnection.mockResolvedValue(undefined)
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="tenant-conn-verify"]').trigger('click')
    await flushPromises()
    expect(mocks.verifyTenantConnection).toHaveBeenCalledWith('tenant-1', 'tc-1')

    await wrapper.find('[data-testid="tenant-conn-delete"]').trigger('click')
    await flushPromises()
    expect(mocks.deleteTenantConnection).toHaveBeenCalledWith('tenant-1', 'tc-1')
  })
})
