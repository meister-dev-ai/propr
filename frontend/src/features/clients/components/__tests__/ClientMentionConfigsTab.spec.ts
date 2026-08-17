// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ClientMentionConfigsTab from '@/features/clients/components/ClientMentionConfigsTab.vue'

const get = vi.fn()
const post = vi.fn()
const patch = vi.fn()
const del = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: get, POST: post, PATCH: patch, DELETE: del }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({ notify: vi.fn() }),
}))

let capabilityState: Array<{ key: string; isAvailable: boolean; message?: string | null }> = []

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getCapability: (key: string) => capabilityState.find((capability) => capability.key === key) ?? null,
  }),
}))

function setCapabilities(capabilities: Array<{ key: string; isAvailable: boolean; message?: string }>) {
  capabilityState = capabilities.map((capability) => ({
    key: capability.key,
    isAvailable: capability.isAvailable,
    message: capability.message ?? null,
  }))
}

const listAdoOrganizationScopes = vi.fn()
const listAdoProjects = vi.fn()
const listAdoCrawlFilters = vi.fn()

vi.mock('@/services/adoDiscoveryService', () => ({
  listAdoOrganizationScopes: (...args: unknown[]) => listAdoOrganizationScopes(...args),
  listAdoProjects: (...args: unknown[]) => listAdoProjects(...args),
  listAdoCrawlFilters: (...args: unknown[]) => listAdoCrawlFilters(...args),
}))

const listProviderActivationStatuses = vi.fn()

vi.mock('@/services/providerActivationService', () => ({
  listProviderActivationStatuses: (...args: unknown[]) => listProviderActivationStatuses(...args),
  formatProviderFamily: (providerFamily: string) =>
    ({ azureDevOps: 'Azure DevOps', github: 'GitHub', gitLab: 'GitLab', forgejo: 'Forgejo' })[providerFamily]
    ?? providerFamily,
}))

const listProviderConnections = vi.fn()

vi.mock('@/services/providerConnectionsService', () => ({
  listProviderConnections: (...args: unknown[]) => listProviderConnections(...args),
}))

const listProviderScopeOptions = vi.fn()
const listProviderRepositoryOptions = vi.fn()

vi.mock('@/services/providerDiscoveryService', () => ({
  listProviderScopeOptions: (...args: unknown[]) => listProviderScopeOptions(...args),
  listProviderRepositoryOptions: (...args: unknown[]) => listProviderRepositoryOptions(...args),
}))

/** One provider activation row, in the shape the form filters on. */
function activation(
  providerFamily: string,
  capabilities: string[] = ['activePullRequestDiscovery', 'reviewThreadReply'],
) {
  return { providerFamily, isEnabled: true, registeredCapabilities: capabilities }
}

function okResponse(data: unknown) {
  return { data, error: undefined, response: { ok: true } }
}

function mountTab() {
  return mount(ClientMentionConfigsTab, {
    props: { clientId: 'client-1' },
    global: {
      stubs: {
        ProgressOrb: true,
        ModalDialog: { props: ['isOpen'], template: '<div v-if="isOpen"><slot /></div>' },
        ConfirmDialog: { props: ['open'], template: '<div v-if="open" class="confirm" />' },
      },
    },
  })
}

describe('ClientMentionConfigsTab', () => {
  beforeEach(() => {
    get.mockReset()
    post.mockReset()
    patch.mockReset()
    del.mockReset()
    // Reset first: a mockReturnValueOnce left queued by an earlier test outlives a plain mockResolvedValue.
    listAdoOrganizationScopes.mockReset().mockResolvedValue([])
    listAdoProjects.mockReset().mockResolvedValue([])
    listAdoCrawlFilters.mockReset().mockResolvedValue([])
    listProviderActivationStatuses.mockReset().mockResolvedValue([activation('azureDevOps')])
    listProviderConnections.mockReset().mockResolvedValue([])
    listProviderScopeOptions.mockReset().mockResolvedValue([])
    listProviderRepositoryOptions.mockReset().mockResolvedValue([])
    setCapabilities([{ key: 'mention-answering', isAvailable: true }])
  })

  it('shows what a license would give instead of an empty table when mention answering is off', async () => {
    setCapabilities([
      { key: 'mention-answering', isAvailable: false, message: 'Mention answering requires a commercial license.' },
    ])

    const wrapper = mountTab()
    await flushPromises()

    expect(get).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Mention answering is unavailable')
    expect(wrapper.text()).toContain('Mention answering requires a commercial license.')
    expect(wrapper.text()).not.toContain('New Config')
  })

  it('tells an operator plainly that nothing is answered when no configuration exists', async () => {
    get.mockResolvedValue(okResponse([]))

    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.text()).toContain('This client answers no mentions')
  })

  it('lists the repositories a configuration answers on', async () => {
    get.mockResolvedValue(
      okResponse([
        {
          id: 'cfg-1',
          clientId: 'client-1',
          provider: 'azureDevOps',
          providerScopePath: 'https://dev.azure.com/org',
          providerProjectKey: 'proj',
          scanIntervalSeconds: 90,
          isActive: true,
          createdAt: '2026-08-11T00:00:00Z',
          repoFilters: [{ id: 'f1', repositoryId: 'repo-guid', displayName: 'payments' }],
        },
      ]),
    )

    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.text()).toContain('payments')
    expect(wrapper.text()).toContain('proj')
    expect(wrapper.text()).toContain('90s')
  })

  it('shows only this client\'s configurations', async () => {
    get.mockResolvedValue(
      okResponse([
        {
          id: 'cfg-other',
          clientId: 'someone-else',
          provider: 'azureDevOps',
          providerScopePath: 'https://dev.azure.com/org',
          providerProjectKey: 'other-project',
          scanIntervalSeconds: 60,
          isActive: true,
          createdAt: '2026-08-11T00:00:00Z',
          repoFilters: [{ id: 'f9', repositoryId: 'other-repo', displayName: 'other-repo' }],
        },
      ]),
    )

    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.text()).not.toContain('other-project')
    expect(wrapper.text()).toContain('This client answers no mentions')

    // The local filter is the second line of defence. Asserting the query proves the server was asked to
    // scope the read, which is the contract that survives a component rewrite.
    expect(get).toHaveBeenCalledWith('/admin/mention-configurations', {
      params: { query: { clientId: 'client-1' } },
    })
  })

  it('refuses to save a configuration naming no repository', async () => {
    get.mockResolvedValue(okResponse([]))

    const wrapper = mountTab()
    await flushPromises()

    // The header's New Config button is the only way in; the empty state describes, it does not duplicate it.
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    // The form opens with nothing picked, so submitting it must not reach the server.
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(post).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Select at least one repository')
  })

  it('does not let a closed edit form select a project in the next one', async () => {
    get.mockResolvedValue(
      okResponse([
        {
          id: 'cfg-1',
          clientId: 'client-1',
          provider: 'azureDevOps',
          providerScopePath: 'https://dev.azure.com/org',
          providerProjectKey: 'proj-guid',
          scanIntervalSeconds: 60,
          isActive: true,
          createdAt: '2026-08-11T00:00:00Z',
          repoFilters: [{ id: 'f1', repositoryId: 'repo-guid', displayName: 'payments-api' }],
        },
      ]),
    )
    listAdoProjects.mockResolvedValue([{ organizationScopeId: 'scope-1', projectId: 'proj-guid', projectName: 'Payments' }])

    // The edit form's organization request is still outstanding when the operator abandons it.
    let releaseScopes: (value: unknown[]) => void = () => {}
    listAdoOrganizationScopes.mockReturnValueOnce(new Promise((resolve) => (releaseScopes = resolve)))

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('.action-btn[title="Edit"]').trigger('click')
    await wrapper.find('.mention-form-actions .btn-secondary').trigger('click')

    listAdoOrganizationScopes.mockResolvedValue([
      { id: 'scope-1', organizationUrl: 'https://dev.azure.com/org', displayName: 'org', isEnabled: true },
    ])
    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    // Only now does the abandoned request answer. It must not reach into the form now on screen.
    releaseScopes([
      { id: 'scope-1', organizationUrl: 'https://dev.azure.com/org', displayName: 'org', isEnabled: true },
    ])
    await flushPromises()

    expect((wrapper.find('#mentionProjectKey').element as HTMLSelectElement).value).toBe('')
  })

  it('stores the provider repository id behind a picked repository name', async () => {
    get.mockResolvedValue(okResponse([]))
    post.mockResolvedValue(okResponse({ id: 'cfg-new' }))
    listAdoOrganizationScopes.mockResolvedValue([
      { id: 'scope-1', organizationUrl: 'https://dev.azure.com/org', displayName: 'org', isEnabled: true },
    ])
    listAdoProjects.mockResolvedValue([{ organizationScopeId: 'scope-1', projectId: 'proj-guid', projectName: 'Payments' }])
    listAdoCrawlFilters.mockResolvedValue([
      {
        canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-guid' },
        displayName: 'payments-api',
        branchSuggestions: [],
      },
    ])

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    await wrapper.find('#mentionScopePath').setValue('scope-1')
    await flushPromises()
    await wrapper.find('#mentionProjectKey').setValue('proj-guid')
    await flushPromises()

    // The operator picks a name; the id underneath is what scanning matches on.
    expect(wrapper.text()).toContain('payments-api')
    await wrapper.find('.mention-repo-list input[type="checkbox"]').setValue(true)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(post).toHaveBeenCalledTimes(1)
    expect(post.mock.calls[0][1].body).toMatchObject({
      providerScopePath: 'https://dev.azure.com/org',
      providerProjectKey: 'proj-guid',
      repoFilters: [
        {
          repositoryId: 'repo-guid',
          displayName: 'payments-api',
          canonicalSourceRef: 'repo-guid',
          sourceProvider: 'azureDevOps',
        },
      ],
    })
  })

  it('offers only providers that are enabled and can both find a question and answer it', async () => {
    get.mockResolvedValue(okResponse([]))
    listProviderActivationStatuses.mockResolvedValue([
      activation('azureDevOps'),
      activation('github'),
      // Both halves are needed, so a deployment holding one of them offers the provider for neither. The
      // capability sets here are hypothetical. Every provider ships with both today; these cases keep
      // that from being assumed.
      activation('gitLab', ['repositoryDiscovery', 'reviewThreadReply']),
      activation('forgejo', ['activePullRequestDiscovery']),
    ])

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    const options = wrapper.find('#mentionProvider').findAll('option').map((option) => option.text())
    expect(options).toEqual(['Azure DevOps', 'GitHub'])
  })

  it('names the fields for what the chosen provider holds, and clears what was picked under the last one', async () => {
    get.mockResolvedValue(okResponse([]))
    listProviderActivationStatuses.mockResolvedValue([activation('azureDevOps'), activation('gitLab')])
    listAdoOrganizationScopes.mockResolvedValue([
      { id: 'scope-1', organizationUrl: 'https://dev.azure.com/org', displayName: 'org', isEnabled: true },
    ])
    listAdoProjects.mockResolvedValue([{ organizationScopeId: 'scope-1', projectId: 'proj-guid', projectName: 'Payments' }])
    listAdoCrawlFilters.mockResolvedValue([
      {
        canonicalSourceRef: { provider: 'azureDevOps', value: 'repo-guid' },
        displayName: 'payments-api',
        branchSuggestions: [],
      },
    ])
    listProviderConnections.mockResolvedValue([
      { id: 'conn-1', providerFamily: 'gitLab', hostBaseUrl: 'https://gitlab.example.com', displayName: 'GitLab', isActive: true },
    ])

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    await wrapper.find('#mentionScopePath').setValue('scope-1')
    await flushPromises()
    await wrapper.find('#mentionProjectKey').setValue('proj-guid')
    await flushPromises()
    await wrapper.find('.mention-repo-list input[type="checkbox"]').setValue(true)

    expect(wrapper.text()).toContain('Organization')
    expect(wrapper.text()).toContain('Project')

    await wrapper.find('#mentionProvider').setValue('gitLab')
    await flushPromises()

    expect(wrapper.text()).toContain('Connection')
    expect(wrapper.text()).toContain('Group')

    // A repository belonging to Azure DevOps must not survive into a GitLab configuration.
    expect(wrapper.find('.mention-repo-list').exists()).toBe(false)
    expect((wrapper.find('#mentionScopePath').element as HTMLSelectElement).value).toBe('')
  })

  it('stores a GitHub repository by its provider-native id, under the connection host', async () => {
    get.mockResolvedValue(okResponse([]))
    post.mockResolvedValue(okResponse({ id: 'cfg-new' }))
    listProviderActivationStatuses.mockResolvedValue([activation('github')])
    listProviderConnections.mockResolvedValue([
      { id: 'conn-1', providerFamily: 'github', hostBaseUrl: 'https://github.com', displayName: 'GitHub', isActive: true },
      { id: 'conn-2', providerFamily: 'gitLab', hostBaseUrl: 'https://gitlab.com', displayName: 'GitLab', isActive: true },
    ])
    listProviderScopeOptions.mockResolvedValue([{ scopePath: 'acme', displayName: 'acme' }])
    listProviderRepositoryOptions.mockResolvedValue([
      { repositoryId: '101', displayName: 'acme/platform', scopePath: 'acme' },
    ])

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()

    // Only the GitHub connection is offered, because the form is for GitHub.
    expect(wrapper.find('#mentionScopePath').findAll('option').map((option) => option.text())).toEqual([
      'Select connection',
      'GitHub',
    ])

    await wrapper.find('#mentionScopePath').setValue('conn-1')
    await flushPromises()
    await wrapper.find('#mentionProjectKey').setValue('acme')
    await flushPromises()

    expect(wrapper.text()).toContain('acme/platform')
    await wrapper.find('.mention-repo-list input[type="checkbox"]').setValue(true)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(post).toHaveBeenCalledTimes(1)
    expect(post.mock.calls[0][1].body).toMatchObject({
      provider: 'github',
      providerScopePath: 'https://github.com',
      providerProjectKey: 'acme',
      repoFilters: [
        {
          repositoryId: '101',
          displayName: 'acme/platform',
          canonicalSourceRef: '101',
          sourceProvider: 'github',
        },
      ],
    })
  })

  it('shows an existing configuration its provider without letting it change', async () => {
    get.mockResolvedValue(
      okResponse([
        {
          id: 'cfg-1',
          clientId: 'client-1',
          provider: 'forgejo',
          providerScopePath: 'https://forgejo.example',
          providerProjectKey: 'acme',
          scanIntervalSeconds: 60,
          isActive: true,
          createdAt: new Date().toISOString(),
          repoFilters: [{ id: 'f1', repositoryId: '101', displayName: 'acme/platform' }],
        },
      ]),
    )
    listProviderConnections.mockResolvedValue([
      { id: 'conn-1', providerFamily: 'forgejo', hostBaseUrl: 'https://forgejo.example', displayName: 'Forgejo', isActive: true },
    ])
    listProviderScopeOptions.mockResolvedValue([{ scopePath: 'acme', displayName: 'acme' }])

    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.text()).toContain('Forgejo')

    await wrapper.find('.action-btn[title="Edit"]').trigger('click')
    await flushPromises()

    const provider = wrapper.find('#mentionProvider').element as HTMLSelectElement
    expect(provider.value).toBe('forgejo')
    expect(provider.disabled).toBe(true)
  })

  /**
   * A stale request that resolves after the operator has moved on must not overwrite the current selection,
   * which is why the discovery loaders compare a request id before applying a result.
   */
  it('a repository listing answered after the owner changed does not overwrite the current one', async () => {
    get.mockResolvedValue(okResponse([]))
    listProviderActivationStatuses.mockResolvedValue([activation('github')])
    listProviderConnections.mockResolvedValue([
      { id: 'conn-1', providerFamily: 'github', hostBaseUrl: 'https://github.com', displayName: 'GitHub', isActive: true },
    ])
    listProviderScopeOptions.mockResolvedValue([
      { scopePath: 'acme', displayName: 'acme' },
      { scopePath: 'contoso', displayName: 'contoso' },
    ])

    let releaseFirst: (value: unknown) => void = () => {}
    listProviderRepositoryOptions
      .mockImplementationOnce(() => new Promise((resolve) => {
        releaseFirst = resolve
      }))
      .mockResolvedValue([{ repositoryId: '202', displayName: 'contoso/tooling', scopePath: 'contoso' }])

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('.section-card-header-actions .btn-primary').trigger('click')
    await flushPromises()
    await wrapper.find('#mentionScopePath').setValue('conn-1')
    await flushPromises()

    void wrapper.find('#mentionProjectKey').setValue('acme')
    await wrapper.find('#mentionProjectKey').setValue('contoso')
    await flushPromises()

    releaseFirst([{ repositoryId: '101', displayName: 'acme/platform', scopePath: 'acme' }])
    await flushPromises()

    expect(wrapper.text()).toContain('contoso/tooling')
    expect(wrapper.text()).not.toContain('acme/platform')
  })
})
