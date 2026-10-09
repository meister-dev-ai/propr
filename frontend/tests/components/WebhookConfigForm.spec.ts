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

vi.mock('@/services/providerConnectionsService', () => ({
  listProviderConnections: vi.fn(async () => [{
    id: 'connection-1', clientId: CLIENT_ID, providerFamily: 'azureDevOps', hostBaseUrl: 'https://dev.azure.com',
    displayName: 'Example connection', isActive: true,
  }]),
  listProviderScopes: vi.fn(async () => [{ id: SCOPE_ID, scopePath: 'https://dev.azure.com/example' }]),
}))

vi.mock('@/services/providerDiscoveryService', () => ({
  listConnectionDescriptor: vi.fn(async () => ({
    provider: 'azureDevOps', scopeLabel: 'Organization', projectLabel: 'Project',
    sourceKinds: [{ kind: 'repository', label: 'Repository' }, { kind: 'adoWiki', label: 'Wiki' }],
    supportsBranches: true, supportsKnowledgeSources: true,
  })),
  listConnectionScopes: async (...args: unknown[]) => (await listOrganizationScopesMock(...args)).map((scope: { id: string; organizationUrl: string; displayName: string }) => ({
    scopeKey: scope.organizationUrl, displayName: scope.displayName, savedScopeId: scope.id,
  })),
  listConnectionProjects: (...args: unknown[]) => listProjectsMock(...args),
  listConnectionSources: async (...args: unknown[]) => (await listCrawlFiltersMock(...args)).map((source: { canonicalSourceRef: { provider: string; value: string }; displayName: string; defaultBranch?: string }) => ({
    organizationScopeId: SCOPE_ID, providerScopePath: args[3], providerProjectKey: args[4],
    repositoryId: source.canonicalSourceRef.value, sourceKind: args[5], canonicalSourceRef: source.canonicalSourceRef,
    displayName: source.displayName, defaultBranch: source.defaultBranch ?? 'main',
  })),
  resolveConnectionSelection: async (...args: unknown[]) => ({
    provider: 'azureDevOps', connectionId: args[1], scopeKey: args[3], organizationScopeId: SCOPE_ID,
    providerScopePath: args[3], providerProjectKey: args[4],
  }),
  listConnectionFilters: (...args: unknown[]) => listCrawlFiltersMock(...args),
  listConnectionBranches: vi.fn(async () => []),
}))

vi.mock('@/services/webhookConfigurationService', () => ({
  createWebhookConfiguration: createWebhookConfigurationMock,
  updateWebhookConfiguration: updateWebhookConfigurationMock,
}))

async function mountCreateForm() {
  const { default: WebhookConfigForm } = await import('@/components/WebhookConfigForm.vue')
  const wrapper = mount(WebhookConfigForm, {
    props: {
      clientId: CLIENT_ID,
    },
  })
  await flushPromises()
  await wrapper.get('#webhook-connection').setValue('connection-1')
  await flushPromises()
  return wrapper
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
  beforeEach(async () => {
    vi.clearAllMocks()
    vi.restoreAllMocks()
    const discovery = await import('@/services/providerDiscoveryService')
    const connections = await import('@/services/providerConnectionsService')
    vi.mocked(discovery.listConnectionDescriptor).mockResolvedValue({
      provider: 'azureDevOps', scopeLabel: 'Organization', projectLabel: 'Project',
      sourceKinds: [{ kind: 'repository', label: 'Repository' }], supportsBranches: true, supportsKnowledgeSources: true,
    })
    vi.mocked(connections.listProviderConnections).mockResolvedValue([{
      id: 'connection-1', clientId: CLIENT_ID, providerFamily: 'azureDevOps', hostBaseUrl: 'https://dev.azure.com',
      displayName: 'Example connection', isActive: true,
    }] as Awaited<ReturnType<typeof connections.listProviderConnections>>)

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
      connectionId: 'connection-1',
      scopeKey: 'https://dev.azure.com/example',
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

    await wrapper.get('#webhook-scope').setValue('https://dev.azure.com/example')
    await flushPromises()
    await wrapper.get('#webhook-project').setValue('project-1')
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
      connectionId: 'connection-1',
      scopeKey: 'https://dev.azure.com/example',
      providerScopePath: 'https://dev.azure.com/example',
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

  it('offers only active saved connections', async () => {
    const connections = await import('@/services/providerConnectionsService')
    vi.spyOn(connections, 'listProviderConnections').mockResolvedValue([
      { id: 'connection-1', providerFamily: 'azureDevOps', displayName: 'Active connection', isActive: true },
      { id: 'disabled-connection', providerFamily: 'github', displayName: 'Disabled connection', isActive: false },
    ] as Awaited<ReturnType<typeof connections.listProviderConnections>>)
    const wrapper = await mountCreateForm()
    expect(wrapper.get('#webhook-connection').findAll('option').map(option => option.text())).toEqual(['Select a connection', 'Active connection'])
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
  ])('creates a connection-scoped $provider webhook configuration', async ({ provider, host, projectId, providerPathSegment }) => {
    const discovery = await import('@/services/providerDiscoveryService')
    const connections = await import('@/services/providerConnectionsService')
    vi.spyOn(connections, 'listProviderConnections').mockResolvedValue([{
      id: 'connection-1', clientId: CLIENT_ID, providerFamily: provider, hostBaseUrl: host,
      displayName: 'Selected connection', isActive: true,
    }] as Awaited<ReturnType<typeof connections.listProviderConnections>>)
    vi.spyOn(discovery, 'listConnectionDescriptor').mockResolvedValue({
      provider: provider as 'github' | 'gitLab' | 'forgejo', scopeLabel: 'Native owner', projectLabel: null,
      sourceKinds: [{ kind: 'repository', label: 'Repository' }], supportsBranches: false, supportsKnowledgeSources: false,
    })
    vi.spyOn(discovery, 'listConnectionScopes').mockResolvedValue([{ scopeKey: projectId, displayName: projectId }])
    vi.spyOn(discovery, 'listConnectionSources').mockResolvedValue([{
      providerScopePath: host, providerProjectKey: projectId, repositoryId: '101', sourceKind: 'repository',
      canonicalSourceRef: { provider, value: '101' }, displayName: 'propr', defaultBranch: null,
    }])
    vi.spyOn(discovery, 'resolveConnectionSelection').mockResolvedValue({
      provider: provider as 'github' | 'gitLab' | 'forgejo', connectionId: 'connection-1', scopeKey: projectId,
      providerScopePath: host, providerProjectKey: projectId, organizationScopeId: null,
    })
    vi.spyOn(discovery, 'listConnectionFilters').mockResolvedValue([{
      canonicalSourceRef: { provider, value: '101' }, displayName: 'propr', branchSuggestions: [],
    }])
    createWebhookConfigurationMock.mockResolvedValueOnce({
      id: 'manual-config', generatedSecret: 'native-secret', listenerUrl: `https://propr.example.com/webhooks/v1/providers/${providerPathSegment}/native`,
    })
    const wrapper = await mountCreateForm()
    await wrapper.get('#webhook-scope').setValue(projectId)
    await flushPromises()
    expect(wrapper.find('#webhook-project').exists()).toBe(false)
    await wrapper.get('#webhookReviewTemperature').setValue('0.6')
    await wrapper.get('#webhookAddFilter').trigger('click')
    await wrapper.get('[data-testid="webhook-filter-select-0"]').setValue(provider + '::101')
    await wrapper.get('[data-testid="webhook-event-pullRequestUpdated"]').setValue(true)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(createWebhookConfigurationMock).toHaveBeenCalledWith(CLIENT_ID, expect.objectContaining({
      provider, connectionId: 'connection-1', scopeKey: projectId, providerScopePath: host, providerProjectKey: projectId,
      enabledEvents: ['pullRequestUpdated'], reviewTemperature: 0.6,
      repoFilters: [{ repositoryName: 'propr', displayName: 'propr', canonicalSourceRef: { provider, value: '101' }, targetBranchPatterns: [] }],
    }))
    expect(wrapper.emitted('config-saved')?.[0]?.[0]).toMatchObject({ generatedSecret: 'native-secret' })
  })

  it('does not emit a pending create receipt after the form unmounts', async () => {
    let release: (value: unknown) => void = () => {}
    createWebhookConfigurationMock.mockImplementationOnce(() => new Promise(resolve => { release = resolve }))
    const wrapper = await mountCreateForm()
    await wrapper.get('#webhook-scope').setValue('https://dev.azure.com/example')
    await flushPromises()
    await wrapper.get('#webhook-project').setValue('project-1')
    await flushPromises()
    await wrapper.get('[data-testid="webhook-event-pullRequestCreated"]').setValue(true)
    await wrapper.find('form').trigger('submit')
    wrapper.unmount()
    release({ id: 'created', generatedSecret: 'fixture-secret' })
    await flushPromises()
    expect(wrapper.emitted('config-saved')).toBeUndefined()
  })

  it('refuses changed filters while discovery is unresolved and keeps unrelated edits available', async () => {
    const connections = await import('@/services/providerConnectionsService')
    vi.mocked(connections.listProviderConnections).mockResolvedValueOnce([])
    const wrapper = await mountEditForm({ id: 'cfg', clientId: CLIENT_ID, provider: 'azureDevOps', organizationScopeId: SCOPE_ID,
      providerScopePath: 'https://dev.azure.com/saved', providerProjectKey: 'Saved Workspace', enabledEvents: ['pullRequestUpdated'],
      repoFilters: [{ id: 'filter', repositoryName: 'Saved Repository', canonicalSourceRef: { provider: 'azureDevOps', value: 'saved' }, targetBranchPatterns: ['main'] }] })
    await flushPromises()
    await wrapper.find('.branch-patterns-input input, .branch-input-wrapper input').setValue('release/*')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(updateWebhookConfigurationMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Resolve the connection before changing repository filters.')
    await wrapper.find('.branch-patterns-input input, .branch-input-wrapper input').setValue('main')
    await wrapper.get('#webhookReviewTemperature').setValue('0.4')
    await wrapper.find('form').trigger('submit'); await flushPromises()
    expect(updateWebhookConfigurationMock.mock.calls[0][1].repoFilters).toBeUndefined()
  })

  it('renders saved target coordinates and repositories when the connection is unavailable', async () => {
    const connections = await import('@/services/providerConnectionsService')
    vi.mocked(connections.listProviderConnections).mockResolvedValueOnce([])
    const wrapper = await mountEditForm({ id: 'cfg', clientId: CLIENT_ID, provider: 'azureDevOps',
      providerScopePath: 'https://saved.example.com/legacy', providerProjectKey: 'Saved Workspace', enabledEvents: ['pullRequestUpdated'],
      repoFilters: [{ id: 'filter', repositoryName: 'Saved Repository', canonicalSourceRef: { provider: 'azureDevOps', value: 'saved' }, targetBranchPatterns: ['main'] }] })
    await flushPromises()
    const { default: fields } = await import('@/components/ConnectionDiscoveryFields.vue')
    expect(wrapper.getComponent(fields).text()).toContain('https://saved.example.com/legacy')
    expect(wrapper.getComponent(fields).text()).toContain('Saved Workspace')
    expect(wrapper.get('[data-testid="webhook-filter-select-0"]').text()).toContain('Saved Repository')
  })

  it('reloads connections and releases a pending save when the reused owner changes client', async () => {
    const connections = await import('@/services/providerConnectionsService')
    let release: (value: unknown) => void = () => {}
    createWebhookConfigurationMock.mockImplementationOnce(() => new Promise(resolve => { release = resolve }))
    const wrapper = await mountCreateForm()
    await wrapper.get('#webhook-scope').setValue('https://dev.azure.com/example'); await flushPromises()
    await wrapper.get('#webhook-project').setValue('project-1'); await flushPromises()
    await wrapper.get('[data-testid="webhook-event-pullRequestCreated"]').setValue(true)
    await wrapper.find('form').trigger('submit')
    const nextClient = '00000000-0000-0000-0000-000000000002'
    await wrapper.setProps({ clientId: nextClient }); await flushPromises()
    expect(connections.listProviderConnections).toHaveBeenLastCalledWith(nextClient)
    expect(wrapper.text()).not.toContain('Creating...')
    release({ id: 'old-result' }); await flushPromises()
    expect(wrapper.emitted('config-saved')).toBeUndefined()
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
      repoFilters: undefined,
      connectionId: 'connection-1',
      scopeKey: 'https://dev.azure.com/example',
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

    expect(updateWebhookConfigurationMock).toHaveBeenCalledWith('webhook-config-1', expect.objectContaining({ repoFilters: undefined }))

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

    await wrapper.get('#webhook-scope').setValue('https://dev.azure.com/example')
    await flushPromises()
    await wrapper.get('#webhook-project').setValue('project-1')
    await flushPromises()
    await wrapper.get('[data-testid="webhook-event-pullRequestUpdated"]').setValue(true)
    await wrapper.get('#webhookReviewTemperature').setValue('-0.1')

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('Review temperature must be between 0.0 and 2.0.')
    expect(createWebhookConfigurationMock).not.toHaveBeenCalled()
  })
})
