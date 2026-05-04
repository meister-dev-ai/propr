// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const CLIENT_ID = '00000000-0000-0000-0000-000000000001'
const SCOPE_ID = '00000000-0000-0000-0000-000000000101'
const SOURCE_ID_1 = '00000000-0000-0000-0000-000000000201'
const SOURCE_ID_2 = '00000000-0000-0000-0000-000000000202'
const INVALID_SOURCE_ID = '00000000-0000-0000-0000-000000000299'

const mockPost = vi.fn()
const mockPatch = vi.fn()
const listOrganizationScopesMock = vi.fn()
const listProjectsMock = vi.fn()
const listCrawlFiltersMock = vi.fn()
const listProCursorSourcesMock = vi.fn()
let capabilityState: Array<{ key?: string | null; isAvailable?: boolean; message?: string | null }> = []

vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ POST: mockPost, PATCH: mockPatch })),
  getApiErrorMessage: (error: unknown, fallback: string) => {
    if (error && typeof error === 'object' && 'error' in error && typeof error.error === 'string') {
      return error.error
    }

    return fallback
  },
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('@/services/adoDiscoveryService', () => ({
  listAdoOrganizationScopes: listOrganizationScopesMock,
  listAdoProjects: listProjectsMock,
  listAdoCrawlFilters: listCrawlFiltersMock,
}))

vi.mock('@/services/proCursorService', () => ({
  listProCursorSources: listProCursorSourcesMock,
}))

vi.mock('@/services/promptOverridesService', () => ({
  listOverrides: vi.fn().mockResolvedValue([]),
  createOverride: vi.fn(),
  deleteOverride: vi.fn(),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getCapability: (key: string) => capabilityState.find((capability) => capability.key === key) ?? null,
  }),
}))

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

async function mountForm() {
  const { default: CrawlConfigForm } = await import('@/components/CrawlConfigForm.vue')
  return mount(CrawlConfigForm, {
    props: {
      clientId: CLIENT_ID,
    },
    global: {
      stubs: {
        TextViewerModal: { template: '<div class="text-viewer-modal-stub" />' },
      },
    },
  })
}

async function mountEditForm(config: Record<string, unknown>) {
  const { default: CrawlConfigForm } = await import('@/components/CrawlConfigForm.vue')
  return mount(CrawlConfigForm, {
    props: {
      config,
    },
    global: {
      stubs: {
        TextViewerModal: { template: '<div class="text-viewer-modal-stub" />' },
      },
    },
  })
}

function findButtonByText(wrapper: Awaited<ReturnType<typeof mountForm>>, text: string) {
  const button = wrapper.findAll('button').find((candidate) => candidate.text().includes(text))
  if (!button) {
    throw new Error(`Button containing text "${text}" was not found.`)
  }

  return button
}

describe('CrawlConfigForm', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    capabilityState = []
    setCapabilities([{ key: 'procursor', isAvailable: true }])

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
      {
        organizationScopeId: SCOPE_ID,
        projectId: 'project-2',
        projectName: 'Project Two',
      },
    ])
    listCrawlFiltersMock.mockImplementation((_clientId: string, _scopeId: string, projectId: string) => {
      if (projectId === 'project-2') {
        return Promise.resolve([
          {
            canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-2' },
            displayName: 'Repository Two',
            branchSuggestions: [{ branchName: 'main', isDefault: true }],
          },
        ])
      }

      return Promise.resolve([
        {
          canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
          displayName: 'Repository One',
          branchSuggestions: [
            { branchName: 'main', isDefault: true },
            { branchName: 'release/2026', isDefault: false },
          ],
        },
      ])
    })
    listProCursorSourcesMock.mockResolvedValue([
      {
        sourceId: SOURCE_ID_1,
        displayName: 'Platform Docs',
        sourceKind: 'repository',
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-1',
        repositoryId: 'repo-source-1',
        defaultBranch: 'main',
        rootPath: null,
        isEnabled: true,
        symbolMode: 'auto',
        status: 'enabled',
        latestSnapshot: null,
        sourceDisplayName: 'Platform Docs Repo',
      },
      {
        sourceId: SOURCE_ID_2,
        displayName: 'Architecture Wiki',
        sourceKind: 'adoWiki',
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-1',
        repositoryId: 'wiki-source-2',
        defaultBranch: 'wikiMain',
        rootPath: null,
        isEnabled: true,
        symbolMode: 'auto',
        status: 'enabled',
        latestSnapshot: null,
        sourceDisplayName: 'Architecture Wiki',
      },
    ])

    mockPost.mockResolvedValue({
      data: {
        id: 'config-1',
        clientId: CLIENT_ID,
        provider: 'azureDevOps',
        organizationScopeId: SCOPE_ID,
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-1',
        crawlIntervalSeconds: 60,
        reviewTemperature: 0.25,
        isActive: true,
        repoFilters: [
          {
            id: 'filter-1',
            repositoryName: 'Repository One',
            displayName: 'Repository One',
            canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
            targetBranchPatterns: ['release/2026'],
          },
        ],
        proCursorSourceScopeMode: 'allClientSources',
        proCursorSourceIds: [],
        invalidProCursorSourceIds: [],
      },
      response: { ok: true, status: 201 },
    })
    mockPatch.mockResolvedValue({
      data: {
        id: 'config-1',
        clientId: CLIENT_ID,
        provider: 'azureDevOps',
        organizationScopeId: SCOPE_ID,
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-1',
        crawlIntervalSeconds: 60,
        reviewTemperature: 0.45,
        isActive: true,
        repoFilters: [],
        proCursorSourceScopeMode: 'selectedSources',
        proCursorSourceIds: [SOURCE_ID_1],
        invalidProCursorSourceIds: [],
      },
      response: { ok: true, status: 200 },
    })
  })

  it('creates a guided crawl configuration from discovered selections', async () => {
    const wrapper = await mountForm()
    await flushPromises()

    await wrapper.get('#crawlOrganizationScope').setValue(SCOPE_ID)
    await flushPromises()
    await wrapper.get('#crawlProjectId').setValue('project-1')
    await flushPromises()

    await wrapper.get('#crawlAddFilter').trigger('click')
    await wrapper.get('[data-testid="crawl-filter-select-0"]').setValue('azureDevOps::repo-1')
    await flushPromises()
    await wrapper.get('#crawlReviewTemperature').setValue('0.2')

    await findButtonByText(wrapper, 'release/2026').trigger('click')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(listOrganizationScopesMock).toHaveBeenCalledWith(CLIENT_ID)
    expect(listProCursorSourcesMock).toHaveBeenCalledWith(CLIENT_ID)
    expect(listProjectsMock).toHaveBeenCalledWith(CLIENT_ID, SCOPE_ID, 'crawl')
    expect(listCrawlFiltersMock).toHaveBeenCalledWith(CLIENT_ID, SCOPE_ID, 'project-1', 'crawl')
    expect(mockPost).toHaveBeenCalledWith(
      '/admin/crawl-configurations',
      expect.objectContaining({
        body: {
          clientId: CLIENT_ID,
          provider: 'azureDevOps',
          organizationScopeId: SCOPE_ID,
          providerProjectKey: 'project-1',
          crawlIntervalSeconds: 60,
          reviewTemperature: 0.2,
          repoFilters: [
            {
              repositoryName: 'Repository One',
              displayName: 'Repository One',
              canonicalSourceRef: {
                provider: 'azureDevOps',
                value: 'repo-1',
              },
              targetBranchPatterns: ['release/2026'],
            },
          ],
          proCursorSourceScopeMode: 'allClientSources',
          proCursorSourceIds: [],
        },
      }),
    )
    expect(wrapper.emitted('config-saved')).toBeTruthy()
  })

  it('includes review temperature when editing an existing crawl configuration', async () => {
    const wrapper = await mountEditForm({
      id: 'config-1',
      clientId: CLIENT_ID,
      organizationScopeId: SCOPE_ID,
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-1',
      crawlIntervalSeconds: 60,
      reviewTemperature: 0.15,
      isActive: true,
      repoFilters: [],
      proCursorSourceScopeMode: 'allClientSources',
      proCursorSourceIds: [],
      invalidProCursorSourceIds: [],
    })
    await flushPromises()

    await wrapper.get('#crawlReviewTemperature').setValue('0.4')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(mockPatch).toHaveBeenCalledWith(
      '/admin/crawl-configurations/{configId}',
      expect.objectContaining({
        params: { path: { configId: 'config-1' } },
        body: expect.objectContaining({
          reviewTemperature: 0.4,
        }),
      }),
    )
  })

  it('rejects crawl review temperature outside the supported range', async () => {
    const wrapper = await mountForm()
    await flushPromises()

    await wrapper.get('#crawlOrganizationScope').setValue(SCOPE_ID)
    await flushPromises()
    await wrapper.get('#crawlProjectId').setValue('project-1')
    await flushPromises()
    await wrapper.get('#crawlReviewTemperature').setValue('2.5')

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('Review temperature must be between 0.0 and 2.0.')
    expect(mockPost).not.toHaveBeenCalled()
  })

  it('submits a selected-source scope with the chosen ProCursor sources', async () => {
    const wrapper = await mountForm()
    await flushPromises()

    await wrapper.get('#crawlOrganizationScope').setValue(SCOPE_ID)
    await flushPromises()
    await wrapper.get('#crawlProjectId').setValue('project-1')
    await flushPromises()
    await wrapper.get('#crawlSourceScopeSelected').setValue(true)
    await flushPromises()
    await wrapper.get('[data-testid="crawl-procursor-source-checkbox-0"]').setValue(true)
    await wrapper.get('[data-testid="crawl-procursor-source-checkbox-1"]').setValue(true)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(mockPost).toHaveBeenCalledWith(
      '/admin/crawl-configurations',
      expect.objectContaining({
        body: expect.objectContaining({
          proCursorSourceScopeMode: 'selectedSources',
          proCursorSourceIds: expect.arrayContaining([SOURCE_ID_1, SOURCE_ID_2]),
        }),
      }),
    )
  })

  it('clears selected repository filters when the project changes', async () => {
    const wrapper = await mountForm()
    await flushPromises()

    await wrapper.get('#crawlOrganizationScope').setValue(SCOPE_ID)
    await flushPromises()
    await wrapper.get('#crawlProjectId').setValue('project-1')
    await flushPromises()

    await wrapper.get('#crawlAddFilter').trigger('click')
    await wrapper.get('[data-testid="crawl-filter-select-0"]').setValue('azureDevOps::repo-1')
    await flushPromises()

    expect(wrapper.text()).toContain('Repository One')

    await wrapper.get('#crawlProjectId').setValue('project-2')
    await flushPromises()

    expect(listCrawlFiltersMock).toHaveBeenLastCalledWith(CLIENT_ID, SCOPE_ID, 'project-2', 'crawl')
    expect(wrapper.findAll('[data-testid^="crawl-filter-select-"]')).toHaveLength(0)
    expect(wrapper.text()).toContain('No filters selected — all repositories are crawled.')
  })

  it('shows the save-time stale-selection error returned by the API', async () => {
    mockPost.mockResolvedValue({
      data: undefined,
      error: { error: 'The selected crawl filter is no longer available in Azure DevOps.' },
      response: { ok: false, status: 409 },
    })

    const wrapper = await mountForm()
    await flushPromises()

    await wrapper.get('#crawlOrganizationScope').setValue(SCOPE_ID)
    await flushPromises()
    await wrapper.get('#crawlProjectId').setValue('project-1')
    await flushPromises()
    await wrapper.get('#crawlAddFilter').trigger('click')
    await wrapper.get('[data-testid="crawl-filter-select-0"]').setValue('azureDevOps::repo-1')
    await flushPromises()

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('The selected crawl filter is no longer available in Azure DevOps.')
  })

  it('shows repair messaging and removes stale source associations before saving edits', async () => {
    setCapabilities([{ key: 'procursor', isAvailable: true }])

    const wrapper = await mountEditForm({
      id: 'config-1',
      clientId: CLIENT_ID,
      organizationScopeId: SCOPE_ID,
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-1',
      crawlIntervalSeconds: 60,
      isActive: true,
      repoFilters: [],
      proCursorSourceScopeMode: 'selectedSources',
      proCursorSourceIds: [SOURCE_ID_1, INVALID_SOURCE_ID],
      invalidProCursorSourceIds: [INVALID_SOURCE_ID],
    })
    await flushPromises()

    expect(wrapper.text()).toContain('1 saved ProCursor source is no longer eligible for this client.')
    expect(wrapper.text()).toContain('1 selected')

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(mockPatch).toHaveBeenCalledWith(
      '/admin/crawl-configurations/{configId}',
      expect.objectContaining({
        params: { path: { configId: 'config-1' } },
        body: expect.objectContaining({
          proCursorSourceScopeMode: 'selectedSources',
          proCursorSourceIds: [SOURCE_ID_1],
        }),
      }),
    )
  })

  it('does not allow removing repository filters in legacy edit mode when filter edits are disabled', async () => {
    const wrapper = await mountEditForm({
      id: 'config-1',
      clientId: CLIENT_ID,
      provider: 'azureDevOps',
      organizationScopeId: null,
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-1',
      crawlIntervalSeconds: 60,
      isActive: true,
      repoFilters: [
        {
          id: 'filter-legacy',
          repositoryName: 'Repository One',
          displayName: 'Repository One',
          canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-1' },
          targetBranchPatterns: ['main'],
        },
      ],
      proCursorSourceScopeMode: 'allClientSources',
      proCursorSourceIds: [],
      invalidProCursorSourceIds: [],
    })
    await flushPromises()

    const removeButton = wrapper.get('.btn-remove-row')
    expect((removeButton.element as HTMLButtonElement).disabled).toBe(true)
    expect(wrapper.findAll('.filter-row')).toHaveLength(1)

    await removeButton.trigger('click')
    await flushPromises()

    expect(wrapper.findAll('.filter-row')).toHaveLength(1)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(mockPatch).toHaveBeenCalledWith(
      '/admin/crawl-configurations/{configId}',
      expect.objectContaining({
        params: { path: { configId: 'config-1' } },
        body: expect.objectContaining({
          repoFilters: undefined,
        }),
      }),
    )
  })

  it('hides selected-source controls and skips loading ProCursor sources when unavailable', async () => {
    setCapabilities([{ key: 'procursor', isAvailable: false, message: 'ProCursor requires commercial.' }])

    const wrapper = await mountForm()
    await flushPromises()

    expect(listProCursorSourcesMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('ProCursor requires commercial.')
    expect(wrapper.find('#crawlSourceScopeSelected').exists()).toBe(false)
  })
})
