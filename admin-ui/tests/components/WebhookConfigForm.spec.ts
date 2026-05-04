// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const CLIENT_ID = '00000000-0000-0000-0000-000000000001'
const SCOPE_ID = '00000000-0000-0000-0000-000000000101'

const listOrganizationScopesMock = vi.fn()
const listProjectsMock = vi.fn()
const listCrawlFiltersMock = vi.fn()
const createWebhookConfigurationMock = vi.fn()
const updateWebhookConfigurationMock = vi.fn()
const listProviderActivationStatusesMock = vi.fn()

vi.mock('@/services/providerActivationService', () => ({
  formatProviderFamily: (providerFamily: string) => providerFamily === 'gitLab'
    ? 'GitLab'
    : providerFamily === 'forgejo'
      ? 'Forgejo'
      : providerFamily === 'github'
        ? 'GitHub'
        : 'Azure DevOps',
  getEnabledProviderOptions: (statuses: Array<{ providerFamily: string; isEnabled: boolean }>) => statuses
    .filter((status) => status.isEnabled)
    .map((status) => ({
      value: status.providerFamily,
      label: status.providerFamily === 'azureDevOps'
        ? 'Azure DevOps'
        : status.providerFamily === 'gitLab'
          ? 'GitLab'
          : status.providerFamily === 'forgejo'
            ? 'Forgejo'
            : 'GitHub',
    })),
  listProviderActivationStatuses: listProviderActivationStatusesMock,
}))

vi.mock('@/services/adoDiscoveryService', () => ({
  listAdoOrganizationScopes: listOrganizationScopesMock,
  listAdoProjects: listProjectsMock,
  listAdoCrawlFilters: listCrawlFiltersMock,
}))

vi.mock('@/services/webhookConfigurationService', () => ({
  createWebhookConfiguration: createWebhookConfigurationMock,
  updateWebhookConfiguration: updateWebhookConfigurationMock,
}))

async function mountCreateForm() {
  const { default: WebhookConfigForm } = await import('@/components/WebhookConfigForm.vue')
  return mount(WebhookConfigForm, {
    props: {
      clientId: CLIENT_ID,
    },
  })
}

async function mountEditForm(config: Record<string, unknown>) {
  const { default: WebhookConfigForm } = await import('@/components/WebhookConfigForm.vue')
  return mount(WebhookConfigForm, {
    props: {
      config,
    },
  })
}

describe('WebhookConfigForm', () => {
  beforeEach(() => {
    vi.clearAllMocks()

    listProviderActivationStatusesMock.mockResolvedValue([
      { providerFamily: 'azureDevOps', isEnabled: true },
      { providerFamily: 'github', isEnabled: true },
      { providerFamily: 'gitLab', isEnabled: true },
      { providerFamily: 'forgejo', isEnabled: true },
    ])

    listOrganizationScopesMock.mockResolvedValue([
      {
        id: SCOPE_ID,
        clientId: CLIENT_ID,
        organizationUrl: 'https://dev.azure.com/example',
        displayName: 'Example Org',
        isEnabled: true,
      },
    ])
    listProjectsMock.mockResolvedValue([
      {
        organizationScopeId: SCOPE_ID,
        projectId: 'project-1',
        projectName: 'Project One',
      },
    ])
    listCrawlFiltersMock.mockResolvedValue([
      {
        canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
        displayName: 'Repository One',
        branchSuggestions: [{ branchName: 'main', isDefault: true }],
      },
    ])

    createWebhookConfigurationMock.mockResolvedValue({
      id: 'webhook-config-1',
      clientId: CLIENT_ID,
      provider: 'azureDevOps',
      organizationScopeId: SCOPE_ID,
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-1',
      isActive: true,
      reviewTemperature: 0.35,
      enabledEvents: ['pullRequestCreated', 'pullRequestUpdated', 'pullRequestCommented'],
      repoFilters: [
        {
          id: 'filter-1',
          repositoryName: 'Repository One',
          displayName: 'Repository One',
          canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
          targetBranchPatterns: ['main'],
        },
      ],
      listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
      generatedSecret: 'generated-secret',
      createdAt: '2026-04-07T09:00:00Z',
    })

    updateWebhookConfigurationMock.mockResolvedValue({
      id: 'webhook-config-1',
      clientId: CLIENT_ID,
      provider: 'azureDevOps',
      organizationScopeId: SCOPE_ID,
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-1',
      isActive: false,
      reviewTemperature: 0.55,
      enabledEvents: ['pullRequestUpdated'],
      repoFilters: [],
      listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
      generatedSecret: null,
      createdAt: '2026-04-07T09:00:00Z',
    })
  })

  it('creates a guided webhook configuration and emits the one-time secret payload', async () => {
    const wrapper = await mountCreateForm()
    await flushPromises()

    await wrapper.get('#webhookOrganizationScope').setValue(SCOPE_ID)
    await flushPromises()
    await wrapper.get('#webhookProjectId').setValue('project-1')
    await flushPromises()
    await wrapper.get('#webhookAddFilter').trigger('click')
    await wrapper.get('[data-testid="webhook-filter-select-0"]').setValue('azureDevOps::repo-1')
    await flushPromises()
    await wrapper.get('#webhookReviewTemperature').setValue('0.3')
    await wrapper.get('[data-testid="webhook-event-pullRequestCreated"]').setValue(true)
    await wrapper.get('[data-testid="webhook-event-pullRequestUpdated"]').setValue(true)
    await wrapper.get('[data-testid="webhook-event-pullRequestCommented"]').setValue(true)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(createWebhookConfigurationMock).toHaveBeenCalledWith(CLIENT_ID, {
      clientId: CLIENT_ID,
      provider: 'azureDevOps',
      organizationScopeId: SCOPE_ID,
      providerProjectKey: 'project-1',
      reviewTemperature: 0.3,
      enabledEvents: ['pullRequestCreated', 'pullRequestUpdated', 'pullRequestCommented'],
      repoFilters: [
        {
          repositoryName: 'Repository One',
          displayName: 'Repository One',
          canonicalSourceRef: {
            provider: 'azureDevOps',
            value: 'repo-1',
          },
          targetBranchPatterns: ['main'],
        },
      ],
    })
    expect(wrapper.emitted('config-saved')?.[0]?.[0]).toMatchObject({
      generatedSecret: 'generated-secret',
      listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
    })
  })

  it('hides disabled manual providers from the provider selector', async () => {
    listProviderActivationStatusesMock.mockResolvedValue([
      { providerFamily: 'azureDevOps', isEnabled: true },
      { providerFamily: 'gitLab', isEnabled: true },
      { providerFamily: 'github', isEnabled: false },
      { providerFamily: 'forgejo', isEnabled: false },
    ])

    const wrapper = await mountCreateForm()
    await flushPromises()

    const options = wrapper.get('#webhookProvider').findAll('option').map((option) => option.text())
    expect(options).toEqual(['Azure DevOps', 'GitLab'])
  })

  it.each([
    {
      provider: 'github',
      host: 'https://github.com',
      projectId: 'acme',
      providerPathSegment: 'github',
    },
    {
      provider: 'gitLab',
      host: 'https://gitlab.example.com',
      projectId: 'acme/platform',
      providerPathSegment: 'gitlab',
    },
    {
      provider: 'forgejo',
      host: 'https://codeberg.org',
      projectId: 'acme-labs',
      providerPathSegment: 'forgejo',
    },
  ])('creates a manual $provider webhook configuration', async ({ provider, host, projectId, providerPathSegment }) => {
    createWebhookConfigurationMock.mockResolvedValueOnce({
      id: `webhook-config-${provider}-1`,
      clientId: CLIENT_ID,
      provider,
      providerScopePath: host,
      providerProjectKey: projectId,
      isActive: true,
      enabledEvents: ['pullRequestUpdated'],
      repoFilters: [
        {
          id: `filter-${provider}-1`,
          repositoryName: 'propr',
          displayName: 'propr',
          canonicalSourceRef: null,
          targetBranchPatterns: [],
        },
      ],
      listenerUrl: `https://propr.example.com/webhooks/v1/providers/${providerPathSegment}/mock-path-key-1`,
      generatedSecret: 'manual-secret',
      createdAt: '2026-04-07T09:00:00Z',
    })

    const wrapper = await mountCreateForm()
    await flushPromises()

    await wrapper.get('#webhookProvider').setValue(provider)
    await flushPromises()
    await wrapper.get('#webhookHostUrl').setValue(host)
    await wrapper.get('#webhookProjectScope').setValue(projectId)
    await wrapper.get('#webhookReviewTemperature').setValue('0.6')
    await wrapper.get('#webhookAddFilter').trigger('click')
    await wrapper.get('[data-testid="webhook-filter-repository-0"]').setValue('propr')
    await wrapper.get('[data-testid="webhook-event-pullRequestUpdated"]').setValue(true)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(createWebhookConfigurationMock).toHaveBeenCalledWith(CLIENT_ID, {
      clientId: CLIENT_ID,
      provider,
      providerScopePath: host,
      providerProjectKey: projectId,
      reviewTemperature: 0.6,
      enabledEvents: ['pullRequestUpdated'],
      repoFilters: [
        {
          repositoryName: 'propr',
          displayName: 'propr',
          canonicalSourceRef: null,
          targetBranchPatterns: [],
        },
      ],
    })
    expect(wrapper.emitted('config-saved')?.[0]?.[0]).toMatchObject({
      provider,
      listenerUrl: `https://propr.example.com/webhooks/v1/providers/${providerPathSegment}/mock-path-key-1`,
    })
  })

  it('updates an existing webhook configuration', async () => {
    const wrapper = await mountEditForm({
      id: 'webhook-config-1',
      clientId: CLIENT_ID,
      provider: 'azureDevOps',
      organizationScopeId: SCOPE_ID,
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-1',
      isActive: true,
      reviewTemperature: 0.25,
      enabledEvents: ['pullRequestCreated', 'pullRequestUpdated'],
      repoFilters: [],
      listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
      createdAt: '2026-04-07T09:00:00Z',
    })
    await flushPromises()

    await wrapper.get('#webhookIsActive').setValue(false)
    await wrapper.get('#webhookReviewTemperature').setValue('0.4')
    await wrapper.get('[data-testid="webhook-event-pullRequestCreated"]').setValue(false)
    await wrapper.get('[data-testid="webhook-event-pullRequestUpdated"]').setValue(true)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(updateWebhookConfigurationMock).toHaveBeenCalledWith('webhook-config-1', {
      isActive: false,
      enabledEvents: ['pullRequestUpdated'],
      repoFilters: [],
      reviewTemperature: 0.4,
    })
    expect(wrapper.emitted('config-saved')).toBeTruthy()
  })

  it('hydrates legacy repository filters from discovered options in edit mode', async () => {
    const wrapper = await mountEditForm({
      id: 'webhook-config-1',
      clientId: CLIENT_ID,
      provider: 'azureDevOps',
      organizationScopeId: SCOPE_ID,
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-1',
      isActive: true,
      reviewTemperature: 0.2,
      enabledEvents: ['pullRequestUpdated'],
      repoFilters: [
        {
          id: 'filter-legacy',
          repositoryName: 'Repository One',
          displayName: 'Repository One',
          canonicalSourceRef: null,
          targetBranchPatterns: ['main'],
        },
      ],
      listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
      createdAt: '2026-04-07T09:00:00Z',
    })
    await flushPromises()

    const filterSelect = wrapper.get('[data-testid="webhook-filter-select-0"]')
    expect((filterSelect.element as HTMLSelectElement).value).toBe('azureDevOps::repo-1')

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(updateWebhookConfigurationMock).toHaveBeenCalledWith('webhook-config-1', {
      isActive: true,
      enabledEvents: ['pullRequestUpdated'],
      repoFilters: [
        {
          repositoryName: 'Repository One',
          displayName: 'Repository One',
          canonicalSourceRef: {
            provider: 'azureDevOps',
            value: 'repo-1',
          },
          targetBranchPatterns: ['main'],
        },
      ],
      reviewTemperature: 0.2,
    })
  })

  it('disables adding repository filters in edit mode', async () => {
    const wrapper = await mountEditForm({
      id: 'webhook-config-1',
      clientId: CLIENT_ID,
      provider: 'azureDevOps',
      organizationScopeId: SCOPE_ID,
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-1',
      isActive: true,
      reviewTemperature: 0.25,
      enabledEvents: ['pullRequestUpdated'],
      repoFilters: [],
      listenerUrl: 'https://propr.example.com/webhooks/v1/providers/ado/mock-path-key-1',
      createdAt: '2026-04-07T09:00:00Z',
    })
    await flushPromises()

    const addFilterButton = wrapper.get('#webhookAddFilter')
    expect((addFilterButton.element as HTMLButtonElement).disabled).toBe(true)

    await addFilterButton.trigger('click')
    await flushPromises()

    expect(wrapper.findAll('.filter-row')).toHaveLength(0)
  })

  it('rejects webhook review temperature outside the supported range', async () => {
    const wrapper = await mountCreateForm()
    await flushPromises()

    await wrapper.get('#webhookOrganizationScope').setValue(SCOPE_ID)
    await flushPromises()
    await wrapper.get('#webhookProjectId').setValue('project-1')
    await flushPromises()
    await wrapper.get('[data-testid="webhook-event-pullRequestUpdated"]').setValue(true)
    await wrapper.get('#webhookReviewTemperature').setValue('-0.1')

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('Review temperature must be between 0.0 and 2.0.')
    expect(createWebhookConfigurationMock).not.toHaveBeenCalled()
  })
})
