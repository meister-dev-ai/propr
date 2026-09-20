// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import TenantAiConnectionsSection from '../TenantAiConnectionsSection.vue'

const mocks = vi.hoisted(() => ({
  listTenantConnections: vi.fn(),
  createTenantConnection: vi.fn(),
  deleteTenantConnection: vi.fn(),
  verifyTenantConnection: vi.fn(),
  listTenantPermittedProviders: vi.fn(),
}))

vi.mock('@/services/logicalModelsService', () => mocks)
vi.mock('@/services/aiConnectionsService', () => mocks)

function mountSection() {
  return mount(TenantAiConnectionsSection, { props: { tenantId: 'tenant-1' } })
}

describe('TenantAiConnectionsSection', () => {
  // The one field a family declares for a credential that is one key, which is the shape this form fills.
  const apiKeyField = { name: 'apiKey', label: 'API key', isSecret: true, isRequired: true }

  beforeEach(() => {
    vi.clearAllMocks()
    // The families this installation loaded, as the server describes them. The section holds no list of its
    // own, so the picker and the provider column both read from here.
    mocks.listTenantPermittedProviders.mockResolvedValue({
      isRestricted: false,
      providers: [
        {
          providerKind: 'openAi',
          label: 'OpenAI (non-Azure)',
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'openAi:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAi:ApiKey': [apiKeyField] },
          declaredFields: [],
        },
        {
          providerKind: 'azureOpenAi',
          label: 'Azure OpenAI / AI Foundry',
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'azureOpenAi:ApiKey', label: 'API Key' }],
          credentialFields: { 'azureOpenAi:ApiKey': [apiKeyField] },
          declaredFields: [],
        },
      ],
    })
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
    expect(wrapper.text()).toContain('OpenAI (non-Azure)')
  })

  // A connection stored against a family the installation no longer has still reads as that family, because
  // the stored identity is what an operator matches against an install or an allow-list.
  it('names a connection whose family the server does not describe by its stored identity', async () => {
    mocks.listTenantConnections.mockResolvedValue([
      {
        id: 'tc-2',
        displayName: 'Gone',
        providerKind: 'contosoLlm',
        baseUrl: 'https://llm.contoso.test/v1',
        verification: { status: 'unverified' },
        configuredModels: [],
      },
    ])

    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.text()).toContain('contosoLlm')
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
        // The first family the server offered, which the picker lands on.
        providerKind: 'openAi',
        auth: { mode: 'openAi:ApiKey', apiKey: 'secret-key' },
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
