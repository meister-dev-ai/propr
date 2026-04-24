// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

const listSourcesMock = vi.fn()
const listBranchesMock = vi.fn()
const createSourceMock = vi.fn()
const getProCursorSourceTokenUsageMock = vi.fn()
const getProCursorRecentEventsMock = vi.fn()
const listOrganizationScopesMock = vi.fn()
const listProjectsMock = vi.fn()
const listSourceOptionsMock = vi.fn()
const listBranchOptionsMock = vi.fn()

let assignedRole: 0 | 1 = 1
let capabilityState: Array<{ key?: string | null; isAvailable?: boolean; message?: string | null }> = []

vi.mock('@/services/proCursorService', () => ({
  listProCursorSources: listSourcesMock,
  listProCursorTrackedBranches: listBranchesMock,
  createProCursorSource: createSourceMock,
  createProCursorTrackedBranch: vi.fn(),
  updateProCursorTrackedBranch: vi.fn(),
  deleteProCursorTrackedBranch: vi.fn(),
  getProCursorSourceTokenUsage: getProCursorSourceTokenUsageMock,
  getProCursorRecentEvents: getProCursorRecentEventsMock,
  queueProCursorRefresh: vi.fn(),
}))

vi.mock('@/services/adoDiscoveryService', () => ({
  listAdoOrganizationScopes: listOrganizationScopesMock,
  listAdoProjects: listProjectsMock,
  listAdoSources: listSourceOptionsMock,
  listAdoBranches: listBranchOptionsMock,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({
    notify: vi.fn(),
  }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: (_clientId: string, minRole: 0 | 1) => assignedRole >= minRole,
    getCapability: (key: string) => capabilityState.find((capability) => capability.key === key) ?? null,
    setLicensingState: (_edition: string, capabilities: Array<{ key?: string | null; isAvailable?: boolean; message?: string | null }>) => {
      capabilityState = capabilities
    },
    clearTokens: () => {
      capabilityState = []
    },
  }),
}))

async function mountTab() {
  const { default: ClientProCursorTab } = await import('@/components/ClientProCursorTab.vue')
  return mount(ClientProCursorTab, {
    props: {
      clientId: 'client-1',
    },
    global: {
      stubs: {
        ProgressOrb: { template: '<div class="orb-stub" />' },
        ModalDialog: {
          props: ['isOpen'],
          template: '<div v-if="isOpen"><slot /><slot name="footer" /></div>',
        },
        ConfirmDialog: {
          props: ['open'],
          template: '<div v-if="open" />',
        },
      },
    },
  })
}

function setCapabilities(capabilities: Array<{ key: string; isAvailable: boolean; message?: string }>) {
  capabilityState = capabilities.map((capability) => ({
    key: capability.key,
    displayName: capability.key,
    requiresCommercial: true,
    defaultWhenCommercial: true,
    overrideState: 'default',
    isAvailable: capability.isAvailable,
    message: capability.message ?? null,
  }))
}

function findButtonByText(wrapper: Awaited<ReturnType<typeof mountTab>>, text: string) {
  const matches = wrapper.findAll('button').filter((candidate) => candidate.text().includes(text))
  const button = matches[matches.length - 1]
  if (!button) {
    throw new Error(`Button containing text "${text}" was not found.`)
  }

  return button
}

describe('ClientProCursorTab', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.resetModules()
    vi.stubEnv('VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING', 'true')
    assignedRole = 1
    capabilityState = []
    setCapabilities([{ key: 'procursor', isAvailable: true }])
    listSourcesMock.mockResolvedValue([])
    listBranchesMock.mockResolvedValue([])
    createSourceMock.mockResolvedValue({ sourceId: 'source-1' })
    getProCursorSourceTokenUsageMock.mockResolvedValue({
      clientId: 'client-1',
      sourceId: 'source-1',
      sourceDisplayName: 'Platform Docs',
      from: '2026-03-01',
      to: '2026-03-30',
      granularity: 'daily',
      totals: {
        promptTokens: 72000,
        completionTokens: 500,
        totalTokens: 72500,
        estimatedCostUsd: 0.36,
        eventCount: 12,
        estimatedEventCount: 1,
      },
      byModel: [
        {
          modelName: 'gpt-4.1-mini',
          promptTokens: 2500,
          completionTokens: 500,
          totalTokens: 3000,
          estimatedCostUsd: 0.18,
          eventCount: 2,
          estimatedEventCount: 0,
        },
        {
          modelName: 'text-embedding-3-small',
          promptTokens: 69500,
          completionTokens: 0,
          totalTokens: 69500,
          estimatedCostUsd: 0.18,
          eventCount: 10,
          estimatedEventCount: 1,
        },
      ],
      series: [
        {
          bucketStart: '2026-03-28',
          promptTokens: 12000,
          completionTokens: 0,
          totalTokens: 12000,
          estimatedCostUsd: 0.024,
          breakdown: [
            {
              modelName: 'text-embedding-3-small',
              promptTokens: 12000,
              completionTokens: 0,
              totalTokens: 12000,
              estimatedCostUsd: 0.024,
              estimated: false,
              eventCount: 1,
              estimatedEventCount: 0,
            },
          ],
        },
      ],
      recentEventsHref: '/admin/clients/client-1/procursor/sources/source-1/token-usage/events?limit=50',
      includesEstimatedUsage: true,
      lastRollupCompletedAtUtc: '2026-03-30T12:00:00Z',
    })
    getProCursorRecentEventsMock.mockResolvedValue({
      clientId: 'client-1',
      sourceId: 'source-1',
      items: [
        {
          occurredAtUtc: '2026-03-30T10:15:00Z',
          requestId: 'pcidx:test:source-1:1',
          callType: 'embedding',
          modelName: 'text-embedding-3-small',
          deploymentName: 'text-embedding-3-small',
          promptTokens: 12000,
          completionTokens: 0,
          totalTokens: 12000,
          estimatedCostUsd: 0.024,
          tokensEstimated: false,
          costEstimated: false,
          sourcePath: '/docs/intro.md',
          resourceId: 'ado://wiki/intro',
        },
      ],
    })
    listOrganizationScopesMock.mockResolvedValue([])
    listProjectsMock.mockResolvedValue([])
    listSourceOptionsMock.mockResolvedValue([])
    listBranchOptionsMock.mockResolvedValue([])
  })

  afterEach(() => {
    vi.unstubAllEnvs()
  })

  it('shows the empty state and create action for client administrators', async () => {
    const wrapper = await mountTab()
    await flushPromises()

    expect(wrapper.text()).toContain('No knowledge sources yet')
    expect(wrapper.text()).toContain('Create Source')
  })

  it('renders sources in read-only mode for client users', async () => {
    assignedRole = 0
    listSourcesMock.mockResolvedValue([
      {
        sourceId: 'source-1',
        displayName: 'Platform Docs',
        sourceKind: 'repository',
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-a',
        repositoryId: 'repo-a',
        defaultBranch: 'main',
        rootPath: '/docs',
        symbolMode: 'auto',
        status: 'enabled',
        latestSnapshot: {
          branch: 'main',
          commitSha: 'abcdef1234567890',
          freshnessStatus: 'fresh',
          completedAt: '2026-04-03T10:00:00Z',
          supportsSymbolQueries: true,
        },
      },
    ])
    listBranchesMock.mockResolvedValue([
      {
        branchId: 'branch-1',
        branchName: 'main',
        refreshTriggerMode: 'branchUpdate',
        miniIndexEnabled: true,
        lastSeenCommitSha: 'abcdef1234567890',
        lastIndexedCommitSha: 'abcdef1234567890',
        isEnabled: true,
        freshnessStatus: 'fresh',
      },
    ])

    const wrapper = await mountTab()
    await flushPromises()

    expect(listBranchesMock).toHaveBeenCalledWith('client-1', 'source-1')
    expect(wrapper.text()).toContain('Read-only')
    expect(wrapper.text()).toContain('Platform Docs')
    expect(wrapper.text()).toContain('main')
    expect(wrapper.text()).not.toContain('Add Source')
    expect(wrapper.text()).not.toContain('Add Branch')
  })

  it('creates a guided ProCursor source from cascading Azure DevOps selections', async () => {
    listOrganizationScopesMock.mockResolvedValue([
      {
        id: 'scope-1',
        organizationUrl: 'https://dev.azure.com/example',
        displayName: 'Example Org',
        isEnabled: true,
      },
    ])
    listProjectsMock.mockResolvedValue([
      {
        organizationScopeId: 'scope-1',
        projectId: 'project-1',
        projectName: 'Project One',
      },
    ])
    listSourceOptionsMock.mockResolvedValue([
      {
        sourceKind: 'Repository',
        canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
        displayName: 'Repo One',
        defaultBranch: 'main',
      },
    ])
    listBranchOptionsMock.mockResolvedValue([
      { branchName: 'main', isDefault: true },
      { branchName: 'develop', isDefault: false },
    ])

    const wrapper = await mountTab()
    await flushPromises()

    await findButtonByText(wrapper, 'Create Source').trigger('click')
    await flushPromises()

    await wrapper.get('#procursorDisplayName').setValue('Platform Docs')
    await wrapper.get('#procursorOrganizationScope').setValue('scope-1')
    await flushPromises()
    await wrapper.get('#procursorProjectId').setValue('project-1')
    await flushPromises()
    await wrapper.get('#procursorSourceSelection').setValue('azureDevOps::repo-1')
    await flushPromises()
    await wrapper.get('#procursorInitialBranch').setValue('develop')
    await wrapper.get('#procursorRootPath').setValue('/docs')

    await findButtonByText(wrapper, 'Create Source').trigger('click')
    await flushPromises()

    expect(listOrganizationScopesMock).toHaveBeenCalledWith('client-1')
    expect(listProjectsMock).toHaveBeenCalledWith('client-1', 'scope-1', 'procursor')
    expect(listSourceOptionsMock).toHaveBeenCalledWith('client-1', 'scope-1', 'project-1', 'repository')
    expect(listBranchOptionsMock).toHaveBeenCalledWith('client-1', 'scope-1', 'project-1', 'repository', {
      provider: 'azureDevOps',
      value: 'repo-1',
    })
    expect(createSourceMock).toHaveBeenCalledWith(
      'client-1',
      expect.objectContaining({
        displayName: 'Platform Docs',
        sourceKind: 'repository',
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-1',
        repositoryId: 'repo-1',
        defaultBranch: 'main',
        rootPath: '/docs',
        organizationScopeId: 'scope-1',
        canonicalSourceRef: {
          provider: 'azureDevOps',
          value: 'repo-1',
        },
        sourceDisplayName: 'Repo One',
        trackedBranches: [
          {
            branchName: 'develop',
            refreshTriggerMode: 'branchUpdate',
            miniIndexEnabled: true,
          },
        ],
      }),
    )
  })

  it('shows the stale guided-selection message when save-time validation fails', async () => {
    listOrganizationScopesMock.mockResolvedValue([
      {
        id: 'scope-1',
        organizationUrl: 'https://dev.azure.com/example',
        displayName: 'Example Org',
        isEnabled: true,
      },
    ])
    listProjectsMock.mockResolvedValue([
      {
        organizationScopeId: 'scope-1',
        projectId: 'project-1',
        projectName: 'Project One',
      },
    ])
    listSourceOptionsMock.mockResolvedValue([
      {
        sourceKind: 'Repository',
        canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
        displayName: 'Repo One',
        defaultBranch: 'main',
      },
    ])
    listBranchOptionsMock.mockResolvedValue([
      { branchName: 'main', isDefault: true },
    ])
    createSourceMock.mockRejectedValue(new Error('The selected source is no longer available in Azure DevOps.'))

    const wrapper = await mountTab()
    await flushPromises()

    await findButtonByText(wrapper, 'Create Source').trigger('click')
    await flushPromises()

    await wrapper.get('#procursorDisplayName').setValue('Platform Docs')
    await wrapper.get('#procursorOrganizationScope').setValue('scope-1')
    await flushPromises()
    await wrapper.get('#procursorProjectId').setValue('project-1')
    await flushPromises()
    await wrapper.get('#procursorSourceSelection').setValue('azureDevOps::repo-1')
    await flushPromises()
    await findButtonByText(wrapper, 'Create Source').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('The selected source is no longer available in Azure DevOps.')
  })

  it('loads source-level usage and recent events for administrators', async () => {
    listSourcesMock.mockResolvedValue([
      {
        sourceId: 'source-1',
        displayName: 'Platform Docs',
        sourceKind: 'repository',
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-a',
        repositoryId: 'repo-a',
        defaultBranch: 'main',
        rootPath: '/docs',
        symbolMode: 'auto',
        status: 'enabled',
      },
    ])

    const wrapper = await mountTab()
    await flushPromises()
    await flushPromises()

    expect(getProCursorSourceTokenUsageMock).toHaveBeenCalledWith(
      'client-1',
      'source-1',
      expect.objectContaining({ period: '30d', granularity: 'daily' }),
    )
    expect(getProCursorRecentEventsMock).toHaveBeenCalledWith('client-1', 'source-1', 10)
    expect(wrapper.text()).toContain('Source Usage')
    expect(wrapper.text()).toContain('Recent Safe Events')
    expect(wrapper.text()).toContain('72,500')
    expect(wrapper.text()).toContain('text-embedding-3-small')
    expect(wrapper.text()).toContain('events in this source window used estimated token counts')
  })

  it('shows the rollout gate message when source usage reporting is disabled', async () => {
    vi.resetModules()
    vi.stubEnv('VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING', 'false')
    listSourcesMock.mockResolvedValue([
      {
        sourceId: 'source-1',
        displayName: 'Platform Docs',
        sourceKind: 'repository',
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-a',
        repositoryId: 'repo-a',
        defaultBranch: 'main',
        rootPath: '/docs',
        symbolMode: 'auto',
        status: 'enabled',
      },
    ])

    const wrapper = await mountTab()
    await flushPromises()

    expect(getProCursorSourceTokenUsageMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Usage reporting rollout is disabled')
    expect(wrapper.text()).not.toContain('Source Usage')
  })

  it('shows a non-actionable unavailable state when ProCursor is disabled', async () => {
    setCapabilities([{ key: 'procursor', isAvailable: false, message: 'ProCursor requires commercial.' }])

    const wrapper = await mountTab()
    await flushPromises()

    expect(listSourcesMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('ProCursor is unavailable')
    expect(wrapper.text()).toContain('ProCursor requires commercial.')
    expect(wrapper.text()).not.toContain('Add Source')
    expect(wrapper.text()).not.toContain('Create Source')
    expect(wrapper.text()).not.toContain('Try Again')
  })
})
